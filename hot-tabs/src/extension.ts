import * as vscode from 'vscode';
import * as cp from 'node:child_process';
import * as path from 'node:path';
import { promisify } from 'node:util';

const execFile = promisify(cp.execFile);
const MINUTE_MS = 60 * 1000;

type ScoreMap = Map<string, number>;

type HotTab = {
  tab: vscode.Tab;
  uri: vscode.Uri;
  label: string;
  score: number;
  originalIndex: number;
};

type RunOptions = {
  source: 'manual' | 'startup' | 'interval';
  silent: boolean;
};

export function activate(context: vscode.ExtensionContext) {
  const output = vscode.window.createOutputChannel('Hot Tabs');
  let intervalHandle: NodeJS.Timeout | undefined;
  let startupHandle: NodeJS.Timeout | undefined;

  const disposable = vscode.commands.registerCommand('hotTabs.reorderAndPin', async () => {
    await reorderAndPinActiveGroup(output, { source: 'manual', silent: false });
  });

  const clearAutomation = () => {
    if (intervalHandle) {
      clearInterval(intervalHandle);
      intervalHandle = undefined;
    }
    if (startupHandle) {
      clearTimeout(startupHandle);
      startupHandle = undefined;
    }
  };

  const configureAutomation = (scheduleStartupRun: boolean) => {
    clearAutomation();

    const config = vscode.workspace.getConfiguration('hotTabs');
    const runOnStartup = config.get<boolean>('runOnStartup', true);
    const intervalMinutes = Math.max(0, Math.floor(config.get<number>('automaticRunIntervalMinutes', 120)));

    if (scheduleStartupRun && runOnStartup) {
      // Give VS Code a brief moment to restore tabs before touching the active editor group.
      startupHandle = setTimeout(() => {
        void reorderAndPinActiveGroup(output, { source: 'startup', silent: true });
      }, 5000);
    }

    if (intervalMinutes > 0) {
      intervalHandle = setInterval(() => {
        void reorderAndPinActiveGroup(output, { source: 'interval', silent: true });
      }, intervalMinutes * MINUTE_MS);
    }

    output.appendLine(
      `Hot Tabs: automatic runs configured. startup=${runOnStartup}, intervalMinutes=${intervalMinutes || 'disabled'}`
    );
  };

  configureAutomation(true);

  context.subscriptions.push(
    disposable,
    output,
    vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration('hotTabs')) {
        configureAutomation(false);
      }
    }),
    {
      dispose: clearAutomation
    }
  );
}

export function deactivate() {
  // Timers are disposed through context.subscriptions.
}

async function reorderAndPinActiveGroup(output: vscode.OutputChannel, options: RunOptions): Promise<void> {
  const config = vscode.workspace.getConfiguration('hotTabs');
  const days = Math.max(1, Math.floor(config.get<number>('days', 90)));
  const pinCount = Math.max(0, Math.floor(config.get<number>('pinCount', 5)));
  const includeZeroScoreTabs = config.get<boolean>('includeZeroScoreTabs', true);

  const group = vscode.window.tabGroups.activeTabGroup;
  const activeUri = getTextFileUri(group.activeTab);
  const openTabs = group.tabs;

  const candidates: HotTab[] = [];
  let skippedDirty = 0;
  let skippedUnsupported = 0;

  for (let i = 0; i < openTabs.length; i += 1) {
    const tab = openTabs[i];
    const uri = getTextFileUri(tab);

    if (!uri) {
      skippedUnsupported += 1;
      continue;
    }

    // Closing a dirty tab can prompt the user. Skip it to avoid interrupting or losing editor state.
    if (tab.isDirty) {
      skippedDirty += 1;
      continue;
    }

    candidates.push({ tab, uri, label: tab.label, score: 0, originalIndex: i });
  }

  if (candidates.length === 0) {
    report(output, options, 'Hot Tabs: no clean text-file tabs to reorder in the active editor group.', 'info');
    return;
  }

  const scoreCache = new Map<string, { root: string; scores: ScoreMap }>();

  for (const item of candidates) {
    const folder = vscode.workspace.getWorkspaceFolder(item.uri);
    if (!folder) {
      continue;
    }

    try {
      const cacheKey = folder.uri.fsPath;
      let cached = scoreCache.get(cacheKey);
      if (!cached) {
        const gitRoot = await findGitRoot(folder.uri.fsPath);
        const scores = await getGitChurnScores(gitRoot, days);
        cached = { root: gitRoot, scores };
        scoreCache.set(cacheKey, cached);
      }

      const rel = normalizePath(path.relative(cached.root, item.uri.fsPath));
      item.score = cached.scores.get(rel) ?? 0;
    } catch (error) {
      output.appendLine(`[warn] Could not score ${item.uri.fsPath}: ${toErrorMessage(error)}`);
    }
  }

  const reorderable = includeZeroScoreTabs ? candidates : candidates.filter(t => t.score > 0);

  if (reorderable.length === 0) {
    report(output, options, `Hot Tabs: no open tabs had Git edits in the last ${days} days.`, 'info');
    return;
  }

  const desiredOrder = [...reorderable].sort((a, b) => {
    if (b.score !== a.score) {
      return b.score - a.score;
    }
    return a.originalIndex - b.originalIndex;
  });

  output.clear();
  output.appendLine(`Hot Tabs: ${options.source} run; Git churn scores for the last ${days} day(s)`);
  for (const [index, item] of desiredOrder.entries()) {
    const pinMarker = index < pinCount ? ' PIN' : '';
    output.appendLine(`${String(index + 1).padStart(2, '0')}. ${String(item.score).padStart(6, ' ')}${pinMarker}  ${item.uri.fsPath}`);
  }

  const orderAlreadyCorrect = arraysEqual(
    reorderable.map(t => t.uri.fsPath),
    desiredOrder.map(t => t.uri.fsPath)
  );
  const pinStateAlreadyCorrect = desiredOrder.every((item, index) => item.tab.isPinned === index < pinCount);

  if (orderAlreadyCorrect && pinStateAlreadyCorrect) {
    report(output, options, `Hot Tabs: tabs are already ordered and pinned correctly; no tabs were closed.`, 'info');
    return;
  }

  if (orderAlreadyCorrect) {
    await applyPinState(desiredOrder, pinCount, group.viewColumn, activeUri);
    report(output, options, `Hot Tabs: order was already correct; updated pin state without closing tabs.`, 'info');
    return;
  }

  const closed = await vscode.window.tabGroups.close(desiredOrder.map(t => t.tab), true);
  if (!closed) {
    report(output, options, 'Hot Tabs: VS Code did not close all selected tabs, so the reorder was cancelled.', 'warn');
    return;
  }

  for (const [index, item] of desiredOrder.entries()) {
    await vscode.window.showTextDocument(item.uri, {
      viewColumn: group.viewColumn,
      preserveFocus: false,
      preview: false
    });

    if (index < pinCount) {
      await vscode.commands.executeCommand('workbench.action.pinEditor');
    }
  }

  if (activeUri) {
    // Restore the tab the user was on, if it is still available.
    await vscode.window.showTextDocument(activeUri, {
      viewColumn: group.viewColumn,
      preserveFocus: false,
      preview: false
    });
  }

  const pinned = Math.min(pinCount, desiredOrder.length);
  const suffixParts: string[] = [];
  if (skippedDirty) {
    suffixParts.push(`${skippedDirty} dirty tab(s) skipped`);
  }
  if (skippedUnsupported) {
    suffixParts.push(`${skippedUnsupported} non-text tab(s) skipped`);
  }
  const suffix = suffixParts.length ? ` (${suffixParts.join(', ')})` : '';

  report(
    output,
    options,
    `Hot Tabs: reordered ${desiredOrder.length} tab(s), pinned ${pinned}, using ${days} days of Git history${suffix}.`,
    'info'
  );
}

async function applyPinState(
  desiredOrder: HotTab[],
  pinCount: number,
  viewColumn: vscode.ViewColumn,
  activeUri: vscode.Uri | undefined
): Promise<void> {
  for (const [index, item] of desiredOrder.entries()) {
    const shouldBePinned = index < pinCount;
    if (item.tab.isPinned === shouldBePinned) {
      continue;
    }

    await vscode.window.showTextDocument(item.uri, {
      viewColumn,
      preserveFocus: false,
      preview: false
    });

    await vscode.commands.executeCommand(shouldBePinned ? 'workbench.action.pinEditor' : 'workbench.action.unpinEditor');
  }

  if (activeUri) {
    await vscode.window.showTextDocument(activeUri, {
      viewColumn,
      preserveFocus: false,
      preview: false
    });
  }
}

function arraysEqual(left: string[], right: string[]): boolean {
  if (left.length !== right.length) {
    return false;
  }

  return left.every((value, index) => value === right[index]);
}

function report(output: vscode.OutputChannel, options: RunOptions, message: string, level: 'info' | 'warn'): void {
  output.appendLine(`[${options.source}] ${message}`);

  if (options.silent) {
    return;
  }

  if (level === 'warn') {
    void vscode.window.showWarningMessage(message);
  } else {
    void vscode.window.showInformationMessage(message);
  }
}

function getTextFileUri(tab: vscode.Tab | undefined): vscode.Uri | undefined {
  if (!tab) {
    return undefined;
  }

  const input = tab.input;
  if (input instanceof vscode.TabInputText && input.uri.scheme === 'file') {
    return input.uri;
  }

  // For diffs, ranking the modified side is usually what a developer expects.
  if (input instanceof vscode.TabInputTextDiff && input.modified.scheme === 'file') {
    return input.modified;
  }

  return undefined;
}

async function findGitRoot(cwd: string): Promise<string> {
  const { stdout } = await execFile('git', ['-C', cwd, 'rev-parse', '--show-toplevel'], {
    maxBuffer: 1024 * 1024
  });

  return stdout.trim();
}

async function getGitChurnScores(gitRoot: string, days: number): Promise<ScoreMap> {
  const since = `${days} days ago`;
  const { stdout } = await execFile(
    'git',
    ['-C', gitRoot, 'log', `--since=${since}`, '--numstat', '--format=', '--', '.'],
    { maxBuffer: 1024 * 1024 * 64 }
  );

  const scores: ScoreMap = new Map();

  for (const rawLine of stdout.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line) {
      continue;
    }

    const parts = line.split('\t');
    if (parts.length < 3) {
      continue;
    }

    const additions = parseNumstatCount(parts[0]);
    const deletions = parseNumstatCount(parts[1]);
    const filePath = normalizeGitPath(parts.slice(2).join('\t'));
    const score = additions + deletions;

    scores.set(filePath, (scores.get(filePath) ?? 0) + score);
  }

  return scores;
}

function parseNumstatCount(value: string): number {
  // Git uses '-' for binary files in numstat. Count binary changes as one edit unit.
  if (value === '-') {
    return 1;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : 0;
}

function normalizeGitPath(filePath: string): string {
  // A rename can appear like: src/{old.ts => new.ts}
  const braceRename = filePath.match(/^(.*)\{(.+) => (.+)\}(.*)$/);
  if (braceRename) {
    return normalizePath(`${braceRename[1]}${braceRename[3]}${braceRename[4]}`);
  }

  // Or like: old/path/file.ts => new/path/file.ts
  const arrow = ' => ';
  const index = filePath.lastIndexOf(arrow);
  if (index >= 0) {
    return normalizePath(filePath.slice(index + arrow.length));
  }

  return normalizePath(filePath);
}

function normalizePath(filePath: string): string {
  return filePath.replace(/\\/g, '/');
}

function toErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

  return String(error);
}
