const vscode = require('vscode');
const path = require('path');
const { TextDecoder } = require('util');
const { spawn } = require('child_process');

let bundledRgPath;
try {
  bundledRgPath = require('@vscode/ripgrep').rgPath;
} catch {
  bundledRgPath = undefined;
}

/** @type {SearchResultsProvider | undefined} */
let provider;
/** @type {vscode.TreeView<any> | undefined} */
let treeView;
/** @type {ParsedSearch | undefined} */
let lastSearch;
/** @type {vscode.OutputChannel | undefined} */
let outputChannel;
let lastDiagnosticText = 'No Context Copy Search has run yet.';

function activate(context) {
  outputChannel = vscode.window.createOutputChannel('Context Copy Search');
  provider = new SearchResultsProvider();
  treeView = vscode.window.createTreeView('contextCopySearch.results', {
    treeDataProvider: provider,
    showCollapseAll: true
  });

  context.subscriptions.push(
    treeView,
    vscode.commands.registerCommand('contextCopySearch.previewMatches', previewMatches),
    vscode.commands.registerCommand('contextCopySearch.searchAndCopy', searchAndCopyContexts),
    // Backward-compatible aliases kept for old keybindings/tasks. They are not shown in the Command Palette.
    vscode.commands.registerCommand('contextCopySearch.search', previewMatches),
    vscode.commands.registerCommand('contextCopySearch.refresh', refreshSearch),
    vscode.commands.registerCommand('contextCopySearch.copyAll', copyAllContexts),
    vscode.commands.registerCommand('contextCopySearch.openMatch', openMatch),
    vscode.commands.registerCommand('contextCopySearch.openFileResult', openFileResult),
    vscode.commands.registerCommand('contextCopySearch.showDiagnostics', showDiagnostics),
    vscode.commands.registerCommand('contextCopySearch.resetFilters', resetExtensionFilters)
  );
}

function deactivate() {}

class SearchResultsProvider {
  constructor() {
    this._onDidChangeTreeData = new vscode.EventEmitter();
    this.onDidChangeTreeData = this._onDidChangeTreeData.event;
    /** @type {FileResult[]} */
    this.files = [];
    this.queryLabel = '';
  }

  /** @param {FileResult[]} files @param {string} queryLabel */
  setResults(files, queryLabel) {
    this.files = files;
    this.queryLabel = queryLabel;
    this._onDidChangeTreeData.fire(undefined);
  }

  refresh() {
    this._onDidChangeTreeData.fire(undefined);
  }

  getTreeItem(element) {
    if (element.type === 'file') {
      const rel = vscode.workspace.asRelativePath(element.uri, false);
      const item = new vscode.TreeItem(`${rel} (${element.matches.length})`, vscode.TreeItemCollapsibleState.Expanded);
      item.id = `file:${element.uri.toString()}`;
      item.resourceUri = element.uri;
      item.tooltip = element.uri.fsPath;
      item.contextValue = 'file';
      item.iconPath = vscode.ThemeIcon.File;
      item.command = {
        command: 'contextCopySearch.openFileResult',
        title: 'Open File',
        arguments: [element]
      };
      return item;
    }

    const line = element.line + 1;
    const col = element.character + 1;
    const preview = oneLinePreview(element.preview);
    const item = new vscode.TreeItem(`Ln ${line}: ${preview}`, vscode.TreeItemCollapsibleState.None);
    item.id = `match:${element.uri.toString()}:${element.line}:${element.character}:${element.length}`;
    item.description = `Col ${col}`;
    item.tooltip = `${vscode.workspace.asRelativePath(element.uri, false)}:${line}:${col}\n${element.preview}`;
    item.contextValue = 'match';
    item.iconPath = new vscode.ThemeIcon('search');
    item.command = {
      command: 'contextCopySearch.openMatch',
      title: 'Open Match',
      arguments: [element]
    };
    return item;
  }

  getChildren(element) {
    if (!element) {
      return this.files;
    }
    if (element.type === 'file') {
      return element.matches;
    }
    return [];
  }
}

/**
 * @typedef {{ type: 'file', uri: vscode.Uri, matches: MatchResult[] }} FileResult
 * @typedef {{ type: 'match', uri: vscode.Uri, line: number, character: number, length: number, preview: string }} MatchResult
 * @typedef {{ raw: string, regex: RegExp, textQuery: { pattern: string, isRegExp: boolean, isCaseSensitive: boolean } }} ParsedSearch
 */

async function previewMatches() {
  const parsed = await promptForParsedSearch('Context Copy Search: Preview Matches');
  if (!parsed) {
    return;
  }

  lastSearch = parsed;
  await executeSearch(lastSearch);
}

async function searchAndCopyContexts() {
  const parsed = await promptForParsedSearch('Context Copy Search: Search and Copy Contexts');
  if (!parsed) {
    return;
  }

  lastSearch = parsed;
  await executeSearch(lastSearch);
  await copyAllContexts();
}

/** @param {string} title */
async function promptForParsedSearch(title) {
  const selected = getSelectedTextForDefaultSearch();
  const raw = await vscode.window.showInputBox({
    title,
    prompt: 'Text search. Use /pattern/ or /pattern/i for regex; otherwise plain text search is used.',
    placeHolder: 'Search text or /regex/i',
    value: selected
  });

  if (!raw) {
    return undefined;
  }

  return parseSearch(raw);
}

function getSelectedTextForDefaultSearch() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.selection.isEmpty) {
    return '';
  }
  const text = editor.document.getText(editor.selection).trim();
  if (!text || text.includes('\n') || text.includes('\r') || text.length > 200) {
    return '';
  }
  return text;
}

async function refreshSearch() {
  if (!lastSearch) {
    await previewMatches();
    return;
  }
  await executeSearch(lastSearch);
}

/** @param {ParsedSearch} search */
async function executeSearch(search) {
  const config = vscode.workspace.getConfiguration('contextCopySearch');
  const searchEngine = config.get('searchEngine', 'auto');

  /** @type {FileResult[]} */
  let files;
  let engine = 'workspace search';

  const wantsRipgrep = searchEngine === 'auto' || searchEngine === 'ripgrep';
  if (wantsRipgrep) {
    try {
      files = await executeRipgrepSearch(search, getRipgrepCommand());
      engine = bundledRgPath ? 'bundled ripgrep' : 'ripgrep from PATH';
    } catch (error) {
      if (searchEngine === 'ripgrep') {
        vscode.window.showWarningMessage(`ripgrep unavailable, using slower fallback scan: ${error && error.message ? error.message : String(error)}`);
      }
      files = await executeFallbackFileScan(search);
      engine = 'fallback file scan; run npm install to enable bundled ripgrep';
    }
  } else {
    files = await executeFallbackFileScan(search);
    engine = 'fallback file scan';
  }

  provider.setResults(files, search.raw);
  await focusResultsView();

  const count = files.reduce((n, file) => n + file.matches.length, 0);
  vscode.window.showInformationMessage(
    `Context Copy Search: ${count} match${count === 1 ? '' : 'es'} in ${files.length} file${files.length === 1 ? '' : 's'} using ${engine}.`
  );
}

function getRipgrepCommand() {
  return bundledRgPath || 'rg';
}


/** @param {ParsedSearch} search @param {string} rgExecutable */
async function executeRipgrepSearch(search, rgExecutable) {
  const config = vscode.workspace.getConfiguration('contextCopySearch');
  const includeGlob = config.get('includeGlob', '');
  const excludeGlob = config.get('excludeGlob', '');
  const maxMatches = config.get('maxMatches', 10000);
  const searchHidden = getEffectiveSearchHidden();
  const ignoreOptions = getEffectiveIgnoreOptions();
  const workspaceFolders = vscode.workspace.workspaceFolders || [];

  const includes = parseGlobList(includeGlob);
  const extensionExcludes = parseGlobList(excludeGlob);
  const vscodeExcludes = getVSCodeExcludeGlobs();
  const excludes = mergeUnique([...vscodeExcludes, ...extensionExcludes]);

  /** @type {Map<string, FileResult>} */
  const byFile = new Map();

  const diagnosticLines = [
    `Query: ${search.raw}`,
    `Engine: ${bundledRgPath ? 'bundled ripgrep' : 'ripgrep from PATH'}`,
    `Workspace folders: ${workspaceFolders.map(f => f.uri.fsPath).join(' | ') || '(none)'}`,
    `Extension includeGlob: ${includes.length ? includes.join(', ') : '(none)'}`,
    `VS Code excludes applied: ${vscodeExcludes.length ? vscodeExcludes.join(', ') : '(none)'}`,
    `Extension extra excludeGlob: ${extensionExcludes.length ? extensionExcludes.join(', ') : '(none)'}`,
    `Ignore files: ${ignoreOptions.respectIgnoreFiles ? 'on' : 'off'}; global: ${ignoreOptions.useGlobalIgnoreFiles ? 'on' : 'off'}; parent: ${ignoreOptions.useParentIgnoreFiles ? 'on' : 'off'}; hidden: ${searchHidden ? 'included' : 'not included'}`,
    ''
  ];

  await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: `Context search: ${search.raw}`,
      cancellable: true
    },
    async (progress, token) => {
      let totalMatches = 0;

      for (const folder of workspaceFolders) {
        if (token.isCancellationRequested || totalMatches >= maxMatches) {
          break;
        }

        const folderPath = folder.uri.fsPath;
        const args = buildRipgrepArgs(search, includes, excludes, {
          ...ignoreOptions,
          searchHidden
        });
        diagnosticLines.push(`cwd: ${folderPath}`);
        diagnosticLines.push(`argv: ${formatCommandForDiagnostics(rgExecutable, args)}`);
        diagnosticLines.push('');

        await new Promise((resolve, reject) => {
          const child = spawn(rgExecutable, args, { cwd: folderPath });
          let stdout = '';
          let stderr = '';
          let resolved = false;

          const finish = () => {
            if (resolved) return;
            resolved = true;
            resolve(undefined);
          };

          const kill = () => {
            try { child.kill(); } catch {}
            finish();
          };

          token.onCancellationRequested(kill);

          child.stdout.on('data', chunk => {
            stdout += chunk.toString('utf8');
            const lines = stdout.split(/\r?\n/);
            stdout = lines.pop() || '';

            for (const line of lines) {
              if (token.isCancellationRequested || totalMatches >= maxMatches) {
                kill();
                return;
              }

              const parsedMatches = parseRipgrepJsonLine(line, folder.uri);
              if (!parsedMatches || parsedMatches.length === 0) {
                continue;
              }

              for (const parsed of parsedMatches) {
                if (token.isCancellationRequested || totalMatches >= maxMatches) {
                  kill();
                  return;
                }
                addMatch(byFile, parsed);
                totalMatches += 1;
                if (totalMatches % 100 === 0) {
                  progress.report({ message: `${totalMatches} matches` });
                }
              }
            }
          });

          child.stderr.on('data', chunk => {
            stderr += chunk.toString('utf8');
          });

          child.on('error', error => {
            if (resolved) return;
            resolved = true;
            reject(error);
          });

          child.on('close', code => {
            if (stdout.trim()) {
              for (const line of stdout.split(/\r?\n/)) {
                if (!line.trim() || totalMatches >= maxMatches) continue;
                const parsedMatches = parseRipgrepJsonLine(line, folder.uri);
                if (parsedMatches) {
                  for (const parsed of parsedMatches) {
                    if (totalMatches >= maxMatches) break;
                    addMatch(byFile, parsed);
                    totalMatches += 1;
                  }
                }
              }
            }

            // ripgrep exits 1 for no matches, which is not an error.
            if (code && code !== 1 && stderr.trim()) {
              const short = stderr.trim().split(/\r?\n/).slice(0, 3).join(' ');
              vscode.window.showWarningMessage(`ripgrep warning: ${short}`);
            }
            finish();
          });
        });
      }
    }
  );

  const sorted = sortFileResults(byFile);
  diagnosticLines.push(`Matched files: ${sorted.length}`);
  for (const file of sorted) {
    diagnosticLines.push(`- ${vscode.workspace.asRelativePath(file.uri, false)} (${file.matches.length})`);
  }
  lastDiagnosticText = diagnosticLines.join('\n');
  logDiagnostics(lastDiagnosticText);

  return sorted;
}

/**
 * @param {ParsedSearch} search
 * @param {string[]} includes
 * @param {string[]} excludes
 * @param {{ respectIgnoreFiles: boolean, useGlobalIgnoreFiles?: boolean, useParentIgnoreFiles?: boolean, searchHidden: boolean }} options
 */
function buildRipgrepArgs(search, includes, excludes, options) {
  const args = [
    '--json',
    '--line-number',
    '--column',
    '--with-filename',
    '--no-heading',
    '--color', 'never',
    '--no-messages'
  ];

  if (!options.respectIgnoreFiles) {
    args.push('--no-ignore');
  } else {
    if (!options.useGlobalIgnoreFiles) {
      args.push('--no-ignore-global');
    }
    if (!options.useParentIgnoreFiles) {
      args.push('--no-ignore-parent');
    }
  }
  if (options.searchHidden) {
    args.push('--hidden');
  }

  if (!search.textQuery.isRegExp) {
    args.push('--fixed-strings');
  }
  if (!search.textQuery.isCaseSensitive) {
    args.push('--ignore-case');
  }

  for (const glob of includes) {
    args.push('--glob', glob);
  }
  for (const glob of excludes) {
    args.push('--glob', glob.startsWith('!') ? glob : `!${glob}`);
  }

  args.push('--regexp', search.textQuery.pattern);
  args.push('.');
  return args;
}

function getEffectiveSearchHidden() {
  const explicit = getExplicitExtensionSetting('searchHidden');
  return explicit !== undefined ? Boolean(explicit) : false;
}

function getEffectiveIgnoreOptions() {
  const searchConfig = vscode.workspace.getConfiguration('search');
  const explicitRespect = getExplicitExtensionSetting('respectIgnoreFiles');
  const respectIgnoreFiles = explicitRespect !== undefined
    ? Boolean(explicitRespect)
    : Boolean(searchConfig.get('useIgnoreFiles', true));

  return {
    respectIgnoreFiles,
    useGlobalIgnoreFiles: Boolean(searchConfig.get('useGlobalIgnoreFiles', true)),
    useParentIgnoreFiles: Boolean(searchConfig.get('useParentIgnoreFiles', true))
  };
}

/** @param {string} key */
function getExplicitExtensionSetting(key) {
  const inspected = vscode.workspace.getConfiguration('contextCopySearch').inspect(key);
  if (!inspected) {
    return undefined;
  }
  if (inspected.workspaceFolderValue !== undefined) return inspected.workspaceFolderValue;
  if (inspected.workspaceValue !== undefined) return inspected.workspaceValue;
  if (inspected.globalValue !== undefined) return inspected.globalValue;
  return undefined;
}

function getVSCodeExcludeGlobs() {
  const searchConfig = vscode.workspace.getConfiguration('search');
  const useExcludeSettings = Boolean(searchConfig.get('useExcludeSettings', true));
  if (!useExcludeSettings) {
    return [];
  }

  const out = [];
  collectGlobObject(out, vscode.workspace.getConfiguration('files').get('exclude', {}));
  collectGlobObject(out, searchConfig.get('exclude', {}));
  return mergeUnique(out);
}

/** @param {string[]} out @param {any} obj */
function collectGlobObject(out, obj) {
  if (!obj || typeof obj !== 'object') {
    return;
  }
  for (const [glob, value] of Object.entries(obj)) {
    if (value === false || value === null || value === undefined) {
      continue;
    }
    if (String(glob).trim()) {
      out.push(String(glob).trim());
    }
  }
}

/** @param {string[]} values */
function mergeUnique(values) {
  const seen = new Set();
  const out = [];
  for (const value of values) {
    const key = String(value || '').trim();
    if (!key || seen.has(key)) {
      continue;
    }
    seen.add(key);
    out.push(key);
  }
  return out;
}

/** @param {string} executable @param {string[]} args */
function formatCommandForDiagnostics(executable, args) {
  return [executable, ...args].map(quoteArg).join(' ');
}

/** @param {string} arg */
function quoteArg(arg) {
  if (/^[A-Za-z0-9_./:=-]+$/.test(arg)) {
    return arg;
  }
  return JSON.stringify(arg);
}

/** @param {string} text */
function logDiagnostics(text) {
  if (!outputChannel) return;
  outputChannel.clear();
  outputChannel.appendLine(text);
}

async function showDiagnostics() {
  if (!outputChannel) {
    outputChannel = vscode.window.createOutputChannel('Context Copy Search');
  }
  outputChannel.clear();
  outputChannel.appendLine(lastDiagnosticText);
  outputChannel.show(true);
}

async function resetExtensionFilters() {
  const config = vscode.workspace.getConfiguration('contextCopySearch');
  const keys = ['includeGlob', 'excludeGlob', 'respectIgnoreFiles', 'searchHidden'];
  for (const key of keys) {
    try { await config.update(key, undefined, vscode.ConfigurationTarget.Workspace); } catch {}
    try { await config.update(key, undefined, vscode.ConfigurationTarget.Global); } catch {}
  }
  vscode.window.showInformationMessage('Context Copy Search filters reset. Run Search again.');
}

/** @param {string} line @param {vscode.Uri} folderUri */
function parseRipgrepJsonLine(line, folderUri) {
  if (!line || !line.trim()) {
    return undefined;
  }

  let event;
  try {
    event = JSON.parse(line);
  } catch {
    return undefined;
  }

  if (!event || event.type !== 'match' || !event.data) {
    return undefined;
  }

  const data = event.data;
  const pathText = data.path && data.path.text;
  const lineText = data.lines && data.lines.text;
  if (!pathText || typeof lineText !== 'string') {
    return undefined;
  }

  const cleanLineText = lineText.replace(/\r?\n$/u, '');
  const fileUri = vscode.Uri.joinPath(folderUri, normalizeRipgrepPath(pathText));
  const sourceLine = Math.max(0, (data.line_number || 1) - 1);
  const submatches = Array.isArray(data.submatches) && data.submatches.length > 0
    ? data.submatches
    : [{ start: Math.max(0, (data.column_number || 1) - 1), end: Math.max(1, data.column_number || 1) }];

  return submatches.map(submatch => {
    const startByte = typeof submatch.start === 'number' ? submatch.start : 0;
    const endByte = typeof submatch.end === 'number'
      ? submatch.end
      : startByte + Math.max(1, String(submatch.match && submatch.match.text || '').length);
    const character = byteOffsetToUtf16Column(cleanLineText, startByte);
    const endCharacter = byteOffsetToUtf16Column(cleanLineText, endByte);

    return {
      type: 'match',
      uri: fileUri,
      line: sourceLine,
      character,
      length: Math.max(1, endCharacter - character),
      preview: cleanLineText
    };
  });
}

/** @param {string} pathText */
function normalizeRipgrepPath(pathText) {
  let cleaned = pathText.replace(/\\/g, '/');
  if (cleaned.startsWith('./')) {
    cleaned = cleaned.slice(2);
  }
  return cleaned;
}

/** @param {string} text @param {number} byteOffset */
function byteOffsetToUtf16Column(text, byteOffset) {
  if (byteOffset <= 0) {
    return 0;
  }
  const bytes = Buffer.from(text, 'utf8');
  const safe = Math.max(0, Math.min(byteOffset, bytes.length));
  return bytes.slice(0, safe).toString('utf8').length;
}

/** @param {ParsedSearch} search */
async function executeFallbackFileScan(search) {
  const config = vscode.workspace.getConfiguration('contextCopySearch');
  const includeGlobSetting = config.get('includeGlob', '');
  const includeGlob = includeGlobSetting && String(includeGlobSetting).trim() ? includeGlobSetting : '**/*';
  const excludeGlob = config.get('excludeGlob', undefined);
  const maxFiles = config.get('maxFiles', 5000);
  const maxMatches = config.get('maxMatches', 10000);
  const maxFileBytes = config.get('maxFileBytes', 1000000);

  /** @type {Map<string, FileResult>} */
  const byFile = new Map();

  await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: `Context search fallback: ${search.raw}`,
      cancellable: true
    },
    async (progress, token) => {
      const files = await vscode.workspace.findFiles(includeGlob, excludeGlob, maxFiles, token);
      let totalMatches = 0;

      for (let i = 0; i < files.length; i++) {
        if (token.isCancellationRequested || totalMatches >= maxMatches) {
          break;
        }

        const uri = files[i];
        if (i % 50 === 0) {
          progress.report({ message: `${i}/${files.length} files, ${totalMatches} matches` });
        }

        let bytes;
        try {
          const stat = await vscode.workspace.fs.stat(uri);
          if (stat.size > maxFileBytes) {
            continue;
          }
          bytes = await vscode.workspace.fs.readFile(uri);
        } catch {
          continue;
        }

        const text = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
        if (looksBinary(text)) {
          continue;
        }

        const lineStarts = computeLineStarts(text);
        const lines = text.split(/\r?\n/);
        const re = cloneRegex(search.regex);
        let match;

        while ((match = re.exec(text)) !== null) {
          if (token.isCancellationRequested || totalMatches >= maxMatches) {
            break;
          }
          if (match[0].length === 0) {
            re.lastIndex += 1;
          }

          const pos = positionAtOffset(lineStarts, match.index);
          addMatch(byFile, {
            type: 'match',
            uri,
            line: pos.line,
            character: pos.character,
            length: Math.max(1, match[0].length),
            preview: lines[pos.line] || ''
          });
          totalMatches += 1;
        }
      }
    }
  );

  return sortFileResults(byFile);
}

/** @param {Map<string, FileResult>} byFile @param {MatchResult} match */
function addMatch(byFile, match) {
  const key = match.uri.toString();
  if (!byFile.has(key)) {
    byFile.set(key, { type: 'file', uri: match.uri, matches: [] });
  }
  byFile.get(key).matches.push(match);
}

/** @param {Map<string, FileResult>} byFile */
function sortFileResults(byFile) {
  const files = [...byFile.values()].sort((a, b) => vscode.workspace.asRelativePath(a.uri).localeCompare(vscode.workspace.asRelativePath(b.uri)));
  for (const file of files) {
    file.matches.sort((a, b) => a.line - b.line || a.character - b.character);
  }
  return files;
}

async function focusResultsView() {
  try {
    await vscode.commands.executeCommand('contextCopySearch.results.focus');
  } catch {
    // The focus command is provided by VS Code for contributed views, but ignore if unavailable.
  }
}

/** @param {string} raw */
function parseSearch(raw) {
  const config = vscode.workspace.getConfiguration('contextCopySearch');
  const defaultCaseSensitive = config.get('caseSensitive', false);

  try {
    const regexParts = parseSlashRegex(raw);
    if (regexParts) {
      const flags = normalizeFlags(regexParts.flags || (defaultCaseSensitive ? '' : 'i'));
      const isCaseSensitive = !flags.includes('i');
      return {
        raw,
        regex: new RegExp(regexParts.source, flags),
        textQuery: {
          pattern: regexParts.source,
          isRegExp: true,
          isCaseSensitive
        }
      };
    }

    return {
      raw,
      regex: new RegExp(escapeRegExp(raw), defaultCaseSensitive ? 'g' : 'gi'),
      textQuery: {
        pattern: raw,
        isRegExp: false,
        isCaseSensitive: defaultCaseSensitive
      }
    };
  } catch (error) {
    vscode.window.showErrorMessage(`Invalid search expression: ${error && error.message ? error.message : String(error)}`);
    return undefined;
  }
}

/** @param {string} raw */
function parseSlashRegex(raw) {
  if (!raw.startsWith('/')) {
    return undefined;
  }
  const lastSlash = raw.lastIndexOf('/');
  if (lastSlash <= 0) {
    return undefined;
  }
  return {
    source: raw.slice(1, lastSlash),
    flags: raw.slice(lastSlash + 1)
  };
}

/** @param {string} flags */
function normalizeFlags(flags) {
  let out = '';
  for (const flag of flags) {
    if (!out.includes(flag) && 'dgimsuvy'.includes(flag)) {
      out += flag;
    }
  }
  if (!out.includes('g')) {
    out += 'g';
  }
  return out.replace(/y/g, '');
}

/** @param {RegExp} regex */
function cloneRegex(regex) {
  return new RegExp(regex.source, regex.flags);
}

/** @param {string} value */
function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** @param {string} value */
function parseGlobList(value) {
  if (!value || typeof value !== 'string') {
    return [];
  }

  // Users configure globs as a comma-separated string. Do not split commas that
  // are inside brace groups, e.g. **/*.{js,ts} or **/{node_modules,.git}/**.
  const parts = [];
  let current = '';
  let braceDepth = 0;
  let escaped = false;

  for (const ch of value) {
    if (escaped) {
      current += ch;
      escaped = false;
      continue;
    }

    if (ch === '\\') {
      current += ch;
      escaped = true;
      continue;
    }

    if (ch === '{') {
      braceDepth += 1;
      current += ch;
      continue;
    }

    if (ch === '}') {
      braceDepth = Math.max(0, braceDepth - 1);
      current += ch;
      continue;
    }

    if (ch === ',' && braceDepth === 0) {
      const trimmed = current.trim();
      if (trimmed) {
        parts.push(trimmed);
      }
      current = '';
      continue;
    }

    current += ch;
  }

  const trimmed = current.trim();
  if (trimmed) {
    parts.push(trimmed);
  }
  return parts;
}


/** @param {string} text */
function oneLinePreview(text) {
  const normalized = String(text || '').replace(/\s+/g, ' ').trim();
  return normalized.length > 180 ? `${normalized.slice(0, 177)}...` : normalized;
}

/** @param {string} text */
function looksBinary(text) {
  const sample = text.slice(0, 4096);
  return sample.includes('\u0000');
}

/** @param {string} text */
function computeLineStarts(text) {
  const starts = [0];
  for (let i = 0; i < text.length; i++) {
    if (text.charCodeAt(i) === 10) {
      starts.push(i + 1);
    }
  }
  return starts;
}

/** @param {number[]} lineStarts @param {number} offset */
function positionAtOffset(lineStarts, offset) {
  let lo = 0;
  let hi = lineStarts.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (lineStarts[mid] <= offset) {
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  const line = Math.max(0, hi);
  return { line, character: offset - lineStarts[line] };
}

/** @param {FileResult | undefined} file */
async function openFileResult(file) {
  if (!file || !file.uri) {
    vscode.window.showWarningMessage('No file was selected.');
    return;
  }
  const first = Array.isArray(file.matches) && file.matches.length > 0 ? file.matches[0] : undefined;
  if (first) {
    await openMatch(first);
    return;
  }
  const doc = await vscode.workspace.openTextDocument(file.uri);
  await vscode.window.showTextDocument(doc, { preview: false, preserveFocus: false });
}

/** @param {MatchResult | any} value */
async function openMatch(value) {
  const match = normalizeMatchArgument(value);
  if (!match) {
    vscode.window.showWarningMessage('No search match was selected. Run Context Copy Search again and click a match line.');
    return;
  }

  try {
    const doc = await vscode.workspace.openTextDocument(match.uri);
    const safeLine = Math.max(0, Math.min(match.line, doc.lineCount - 1));
    const lineLength = doc.lineAt(safeLine).text.length;
    const safeCharacter = Math.max(0, Math.min(match.character, lineLength));
    const safeEnd = Math.max(safeCharacter, Math.min(safeCharacter + Math.max(1, match.length || 1), lineLength));
    const start = new vscode.Position(safeLine, safeCharacter);
    const end = new vscode.Position(safeLine, safeEnd);
    const range = new vscode.Range(start, end);

    const editor = await vscode.window.showTextDocument(doc, {
      preview: false,
      preserveFocus: false,
      selection: range
    });
    editor.selection = new vscode.Selection(start, end);
    editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
  } catch (error) {
    vscode.window.showErrorMessage(`Could not open match: ${error && error.message ? error.message : String(error)}`);
  }
}

/** @param {any} value */
function normalizeMatchArgument(value) {
  if (!value) {
    return undefined;
  }
  if (value.type === 'match' && value.uri) {
    return value;
  }
  // Defensive fallback for cases where VS Code invokes a command with a TreeItem-ish object.
  if (value.resourceUri && typeof value.label === 'string') {
    return {
      type: 'match',
      uri: value.resourceUri,
      line: 0,
      character: 0,
      length: 1,
      preview: value.label
    };
  }
  return undefined;
}

async function copyAllContexts() {
  if (!provider || provider.files.length === 0) {
    vscode.window.showWarningMessage('No Context Copy Search results to copy. Run Context Copy Search: Search first.');
    return;
  }

  const blocks = [];
  let copiedContexts = 0;
  let copiedMatches = 0;

  await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: 'Copying search contexts',
      cancellable: false
    },
    async (progress) => {
      for (const file of provider.files) {
        progress.report({ message: vscode.workspace.asRelativePath(file.uri, false) });
        let doc;
        try {
          doc = await vscode.workspace.openTextDocument(file.uri);
        } catch {
          continue;
        }

        const contexts = new Map();
        for (const match of file.matches) {
          const context = await findBestContext(doc, match.line);
          const key = `${context.startLine}:${context.endLine}`;
          if (!contexts.has(key)) {
            contexts.set(key, { ...context, matches: [] });
          }
          contexts.get(key).matches.push(match);
        }

        for (const context of contexts.values()) {
          copiedContexts += 1;
          copiedMatches += context.matches.length;
          const rel = vscode.workspace.asRelativePath(file.uri, false);
          const sortedMatches = [...context.matches].sort((a, b) => a.line - b.line || a.character - b.character);
          const matchList = sortedMatches.map(m => `${m.line + 1}:${m.character + 1}`).join(', ');
          const headerText = `==== ${rel} | ${context.label} | matches: ${matchList} ====`;
          const header = makeCommentHeader(doc, headerText);
          const text = formatContextTextWithMatchMarkers(doc, context, sortedMatches, rel).replace(/\s+$/u, '');
          blocks.push(`${header}\n${text}`);
        }
      }
    }
  );

  const totalSearchMatches = provider.files.reduce((n, file) => n + file.matches.length, 0);
  const output = [`// ==== Context Copy Search: ${copiedMatches}/${totalSearchMatches} matches copied in ${copiedContexts} context block${copiedContexts === 1 ? '' : 's'} ====`, ...blocks].join('\n\n');
  await vscode.env.clipboard.writeText(output);
  vscode.window.showInformationMessage(`Copied ${copiedMatches} match${copiedMatches === 1 ? '' : 'es'} in ${copiedContexts} context block${copiedContexts === 1 ? '' : 's'} to clipboard.`);
}

/**
 * @param {vscode.TextDocument} doc
 * @param {{ startLine: number, endLine: number }} context
 * @param {MatchResult[]} matches
 * @param {string} rel
 */
function formatContextTextWithMatchMarkers(doc, context, matches, rel) {
  const insertMarkers = vscode.workspace.getConfiguration('contextCopySearch').get('insertMatchMarkers', true);
  const eol = doc.eol === vscode.EndOfLine.CRLF ? '\r\n' : '\n';
  const out = [];

  /** @type {Map<number, MatchResult[]>} */
  const byLine = new Map();
  for (const match of matches) {
    if (match.line < context.startLine || match.line > context.endLine) {
      continue;
    }
    if (!byLine.has(match.line)) {
      byLine.set(match.line, []);
    }
    byLine.get(match.line).push(match);
  }

  for (let i = context.startLine; i <= context.endLine; i++) {
    const lineMatches = byLine.get(i) || [];
    if (insertMarkers && lineMatches.length > 0) {
      const columns = lineMatches
        .sort((a, b) => a.character - b.character)
        .map(m => m.character + 1)
        .join(', ');
      out.push(makeCommentHeader(doc, `MATCH ${rel}:${i + 1}:${columns}`));
    }
    out.push(doc.lineAt(i).text);
  }

  return out.join(eol);
}

/** @param {vscode.TextDocument} doc @param {number} line */
async function findBestContext(doc, line) {
  const symbolContext = await findSymbolContext(doc, line);
  if (symbolContext) {
    return symbolContext;
  }
  return fallbackContext(doc, line);
}

/** @param {vscode.TextDocument} doc @param {number} line */
async function findSymbolContext(doc, line) {
  let symbols;
  try {
    symbols = await vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', doc.uri);
  } catch {
    return undefined;
  }

  if (!Array.isArray(symbols) || symbols.length === 0) {
    return undefined;
  }

  const paths = [];
  collectContainingSymbolPaths(symbols, line, [], paths);

  const usefulPaths = paths
    .map(path => path.filter(symbol => symbol && symbol.range && isUsefulContextSymbol(symbol.kind)))
    .filter(path => path.length > 0);

  if (usefulPaths.length === 0) {
    return undefined;
  }

  // Prefer the deepest symbol path. If that deepest match is a method/function
  // inside a class/struct/interface, copy the enclosing class-like symbol instead
  // of only the method. This keeps class fields/constructor/helpers with the match.
  usefulPaths.sort((a, b) => {
    const aLast = a[a.length - 1];
    const bLast = b[b.length - 1];
    return rangeSize(aLast.range) - rangeSize(bLast.range);
  });

  const bestPath = usefulPaths[0];
  const innermost = bestPath[bestPath.length - 1];
  const enclosingClassLike = [...bestPath].reverse().find(symbol => isClassLikeContextSymbol(symbol.kind));
  const chosen = enclosingClassLike && enclosingClassLike !== innermost ? enclosingClassLike : innermost;
  const label = chosen === enclosingClassLike && chosen !== innermost
    ? `${symbolKindName(chosen.kind)} ${chosen.name} (contains ${symbolKindName(innermost.kind)} ${innermost.name})`
    : `${symbolKindName(chosen.kind)} ${chosen.name}`;

  return {
    startLine: chosen.range.start.line,
    endLine: chosen.range.end.line,
    label: label.trim(),
    source: 'symbols'
  };
}

/**
 * @param {any[]} symbols
 * @param {number} line
 * @param {any[]} path
 * @param {any[][]} out
 */
function collectContainingSymbolPaths(symbols, line, path, out) {
  for (const symbol of symbols) {
    if (!symbol || !symbol.range || !containsLine(symbol.range, line)) {
      continue;
    }

    const nextPath = [...path, symbol];
    out.push(nextPath);

    if (Array.isArray(symbol.children) && symbol.children.length > 0) {
      collectContainingSymbolPaths(symbol.children, line, nextPath, out);
    }
  }
}

/** @param {vscode.Range} range @param {number} line */
function containsLine(range, line) {
  return range.start.line <= line && range.end.line >= line;
}

/** @param {vscode.Range} range */
function rangeSize(range) {
  return (range.end.line - range.start.line) * 100000 + (range.end.character - range.start.character);
}

/** @param {number} kind */
function isUsefulContextSymbol(kind) {
  return new Set([
    vscode.SymbolKind.Class,
    vscode.SymbolKind.Method,
    vscode.SymbolKind.Function,
    vscode.SymbolKind.Constructor,
    vscode.SymbolKind.Interface,
    vscode.SymbolKind.Struct,
    vscode.SymbolKind.Module,
    vscode.SymbolKind.Namespace,
    vscode.SymbolKind.Enum
  ]).has(kind);
}

/** @param {number} kind */
function isClassLikeContextSymbol(kind) {
  return new Set([
    vscode.SymbolKind.Class,
    vscode.SymbolKind.Interface,
    vscode.SymbolKind.Struct,
    vscode.SymbolKind.Enum
  ]).has(kind);
}

/** @param {number} kind */
function symbolKindName(kind) {
  for (const [name, value] of Object.entries(vscode.SymbolKind)) {
    if (value === kind) {
      return name.toLowerCase();
    }
  }
  return 'symbol';
}

/** @param {vscode.TextDocument} doc @param {number} line */
function fallbackContext(doc, line) {
  if (isPythonDocument(doc)) {
    return pythonFallbackContext(doc, line);
  }
  if (isBraceLanguageDocument(doc)) {
    return braceFallbackContext(doc, line);
  }

  const contextLines = vscode.workspace.getConfiguration('contextCopySearch').get('fallbackContextLines', 8);
  return {
    startLine: Math.max(0, line - contextLines),
    endLine: Math.min(doc.lineCount - 1, line + contextLines),
    label: `context around line ${line + 1}`,
    source: 'fallback'
  };
}

/** @param {vscode.TextDocument} doc */
function isPythonDocument(doc) {
  return doc.languageId === 'python' || doc.uri.fsPath.endsWith('.py');
}

/** @param {vscode.TextDocument} doc */
function isBraceLanguageDocument(doc) {
  const ext = path.extname(doc.uri.fsPath).toLowerCase();
  return ['javascript', 'typescript', 'javascriptreact', 'typescriptreact', 'java', 'c', 'cpp'].includes(doc.languageId)
    || ['.js', '.jsx', '.mjs', '.cjs', '.ts', '.tsx', '.java', '.c', '.cc', '.cpp', '.cxx', '.h', '.hh', '.hpp', '.hxx'].includes(ext);
}

/** @param {vscode.TextDocument} doc @param {number} line */
function pythonFallbackContext(doc, line) {
  let start = line;
  for (let i = line; i >= 0; i--) {
    const text = doc.lineAt(i).text;
    if (/^\s*(async\s+def|def|class)\s+\w+/.test(text)) {
      start = i;
      break;
    }
  }

  while (start > 0 && /^\s*@/.test(doc.lineAt(start - 1).text)) {
    start -= 1;
  }

  const baseIndent = indentationOf(doc.lineAt(start).text);
  let end = doc.lineCount - 1;
  for (let i = start + 1; i < doc.lineCount; i++) {
    const text = doc.lineAt(i).text;
    if (text.trim() === '') {
      continue;
    }
    if (indentationOf(text) <= baseIndent && !/^\s*(#|@)/.test(text)) {
      end = Math.max(start, i - 1);
      break;
    }
  }

  return {
    startLine: start,
    endLine: end,
    label: `python block around line ${line + 1}`,
    source: 'python-fallback'
  };
}

/** @param {string} text */
function indentationOf(text) {
  const match = text.match(/^\s*/);
  return match ? match[0].replace(/\t/g, '    ').length : 0;
}

/** @param {vscode.TextDocument} doc @param {number} line */
function braceFallbackContext(doc, line) {
  const classLike = findEnclosingClassLikeBlock(doc, line);
  if (classLike) {
    return classLike;
  }

  const openLine = findContainingOpenBraceLine(doc, line);
  if (openLine === undefined) {
    const contextLines = vscode.workspace.getConfiguration('contextCopySearch').get('fallbackContextLines', 8);
    return {
      startLine: Math.max(0, line - contextLines),
      endLine: Math.min(doc.lineCount - 1, line + contextLines),
      label: `context around line ${line + 1}`,
      source: 'brace-fallback'
    };
  }

  let start = openLine;
  for (let i = openLine - 1; i >= Math.max(0, openLine - 20); i--) {
    const text = doc.lineAt(i).text.trim();
    if (text === '') {
      break;
    }
    if (text.startsWith('}') || text.endsWith(';')) {
      break;
    }
    start = i;
    if (/\b(class|struct|interface|enum|function)\b/.test(text) || /\)\s*(const\s*)?$/.test(text)) {
      continue;
    }
  }

  const end = findClosingBraceLine(doc, openLine) ?? Math.min(doc.lineCount - 1, line + 20);
  return {
    startLine: start,
    endLine: end,
    label: `brace block around line ${line + 1}`,
    source: 'brace-fallback'
  };
}


/** @param {vscode.TextDocument} doc @param {number} line */
function findEnclosingClassLikeBlock(doc, line) {
  const candidates = [];

  for (let i = line; i >= 0; i--) {
    const text = doc.lineAt(i).text;
    const trimmed = text.trim();
    if (!trimmed || trimmed.startsWith('//') || trimmed.startsWith('*')) {
      continue;
    }

    if (!/\b(class|struct|interface|enum)\b/.test(text)) {
      continue;
    }

    const openLine = findFirstOpenBraceLine(doc, i, Math.min(doc.lineCount - 1, i + 30));
    if (openLine === undefined || openLine > line) {
      continue;
    }

    const endLine = findClosingBraceLine(doc, openLine);
    if (endLine === undefined || endLine < line) {
      continue;
    }

    candidates.push({
      startLine: findDeclarationStartLine(doc, i),
      endLine,
      label: classLikeLabelFromLine(text, line),
      source: 'brace-class-fallback'
    });
  }

  candidates.sort((a, b) => b.startLine - a.startLine);
  return candidates[0];
}

/** @param {vscode.TextDocument} doc @param {number} startLine @param {number} endLine */
function findFirstOpenBraceLine(doc, startLine, endLine) {
  for (let i = startLine; i <= endLine; i++) {
    if (doc.lineAt(i).text.includes('{')) {
      return i;
    }
  }
  return undefined;
}

/** @param {vscode.TextDocument} doc @param {number} declarationLine */
function findDeclarationStartLine(doc, declarationLine) {
  let start = declarationLine;
  for (let i = declarationLine - 1; i >= 0; i--) {
    const trimmed = doc.lineAt(i).text.trim();
    if (/^(@|\[|export\s|public\s|private\s|protected\s|abstract\s|final\s)/.test(trimmed)) {
      start = i;
      continue;
    }
    break;
  }
  return start;
}

/** @param {string} text @param {number} line */
function classLikeLabelFromLine(text, line) {
  const match = text.match(/\b(class|struct|interface|enum)\s+([A-Za-z_$][\w$]*)/);
  if (match) {
    return `${match[1]} ${match[2]} (contains match around line ${line + 1})`;
  }
  return `class-like block around line ${line + 1}`;
}

/** @param {vscode.TextDocument} doc @param {number} line */
function findContainingOpenBraceLine(doc, line) {
  let depth = 0;
  for (let i = line; i >= 0; i--) {
    const text = doc.lineAt(i).text;
    for (let j = text.length - 1; j >= 0; j--) {
      const ch = text[j];
      if (ch === '}') {
        depth += 1;
      } else if (ch === '{') {
        if (depth === 0) {
          return i;
        }
        depth -= 1;
      }
    }
  }
  return undefined;
}

/** @param {vscode.TextDocument} doc @param {number} openLine */
function findClosingBraceLine(doc, openLine) {
  let depth = 0;
  for (let i = openLine; i < doc.lineCount; i++) {
    const text = doc.lineAt(i).text;
    for (let j = 0; j < text.length; j++) {
      const ch = text[j];
      if (ch === '{') {
        depth += 1;
      } else if (ch === '}') {
        depth -= 1;
        if (depth === 0) {
          return i;
        }
      }
    }
  }
  return undefined;
}

/** @param {vscode.TextDocument} doc @param {number} startLine @param {number} endLine */
function rangeForWholeLines(doc, startLine, endLine) {
  const safeStart = Math.max(0, Math.min(startLine, doc.lineCount - 1));
  const safeEnd = Math.max(0, Math.min(endLine, doc.lineCount - 1));
  return new vscode.Range(
    new vscode.Position(safeStart, 0),
    doc.lineAt(safeEnd).rangeIncludingLineBreak.end
  );
}

/** @param {vscode.TextDocument} doc @param {string} text */
function makeCommentHeader(doc, text) {
  if (isPythonDocument(doc)) {
    return `# ${text}`;
  }
  return `// ${text}`;
}

module.exports = {
  activate,
  deactivate
};
