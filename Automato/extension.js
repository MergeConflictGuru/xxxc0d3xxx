"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.activate = activate;
exports.deactivate = deactivate;
const node_child_process_1 = require("node:child_process");
const node_crypto_1 = require("node:crypto");
const node_fs_1 = require("node:fs");
const path = __importStar(require("node:path"));
const node_util_1 = require("node:util");
const vscode = __importStar(require("vscode"));
const clipboard_1 = require("./clipboard");
const clipboardEvents_1 = require("./clipboardEvents");
const comments_1 = require("./comments");
const patch_1 = require("./patch");
const execFileAsync = (0, node_util_1.promisify)(node_child_process_1.execFile);
const OUTPUT_NAME = 'Automato';
let watcher;
let statusBar;
let output;
function configuration() {
    const config = vscode.workspace.getConfiguration('automato');
    return {
        watchClipboard: config.get('watchClipboard', true),
        pollIntervalMs: Math.max(200, config.get('pollIntervalMs', 650)),
        whitespaceMatching: config.get('whitespaceMatching', 'auto'),
        confirmBeforeApply: config.get('confirmBeforeApply', true),
        stageChanges: config.get('stageChanges', false)
    };
}
function compactError(error, limit = 520) {
    const text = error instanceof Error ? error.message : String(error);
    const oneLine = text.replace(/\s*\n\s*/g, ' ').trim();
    return oneLine.length <= limit ? oneLine : oneLine.slice(0, limit - 1).trimEnd() + '…';
}
async function gitRootForPath(filePath) {
    try {
        const result = await execFileAsync('git', ['-C', path.dirname(filePath), 'rev-parse', '--show-toplevel'], { windowsHide: true, maxBuffer: 1024 * 1024 });
        return result.stdout.trim();
    }
    catch {
        throw new Error('The active file is not inside a Git repository.');
    }
}
async function currentRepositoryRoot() {
    const active = vscode.window.activeTextEditor?.document;
    if (active?.uri.scheme === 'file') {
        return gitRootForPath(active.uri.fsPath);
    }
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) {
        throw new Error('Open a Git repository first.');
    }
    for (const folder of folders) {
        if (folder.uri.scheme !== 'file') {
            continue;
        }
        try {
            return await gitRootForPath(folder.uri.fsPath);
        }
        catch {
            // Try the next workspace folder.
        }
    }
    throw new Error('No local Git repository is open.');
}
function safeCopiedFileName(original, relativePath, hasComment) {
    if (hasComment) {
        return original;
    }
    const encodedPath = relativePath.replace(/[^a-zA-Z0-9._-]+/g, '!');
    const extension = path.extname(original);
    const maximumStemLength = Math.max(24, 220 - extension.length);
    return (`FILE_PATH__${encodedPath}`).slice(0, maximumStemLength) + extension;
}
async function copyActiveFile(context) {
    if (!vscode.workspace.isTrusted) {
        throw new Error('Automato is disabled until this workspace is trusted.');
    }
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.uri.scheme !== 'file') {
        throw new Error('Open a local file in the editor first.');
    }
    const document = editor.document;
    const repoRoot = await gitRootForPath(document.uri.fsPath);
    const relativePath = path.relative(repoRoot, document.uri.fsPath).replace(/\\/g, '/');
    if (relativePath.startsWith('../') || path.isAbsolute(relativePath)) {
        throw new Error('The active file is outside the detected Git repository.');
    }
    const pathHeader = await (0, comments_1.makePathHeader)(document.languageId, relativePath);
    const originalText = document.getText();
    const payload = pathHeader.header
        ? `${pathHeader.header}\n${originalText}`
        : originalText;
    await node_fs_1.promises.mkdir(context.globalStorageUri.fsPath, { recursive: true });
    const existingEntries = await node_fs_1.promises.readdir(context.globalStorageUri.fsPath, { withFileTypes: true });
    await Promise.all(existingEntries
        .filter(entry => entry.isDirectory() && entry.name.startsWith('copy-'))
        .map(entry => node_fs_1.promises.rm(path.join(context.globalStorageUri.fsPath, entry.name), { recursive: true, force: true })));
    const copyDirectory = await node_fs_1.promises.mkdtemp(path.join(context.globalStorageUri.fsPath, 'copy-'));
    const copiedName = safeCopiedFileName(path.basename(document.uri.fsPath), relativePath, pathHeader.usedComment);
    const copiedPath = path.join(copyDirectory, copiedName);
    await node_fs_1.promises.writeFile(copiedPath, payload, 'utf8');
    await (0, clipboard_1.setWindowsFileAndTextClipboard)(context, copiedPath, payload);
    const unsavedNote = document.isDirty ? ' including unsaved editor changes' : '';
    await vscode.window.showInformationMessage(`Copied ${relativePath}${unsavedNote} as a file with text fallback.`);
}
function clipboardHash(text) {
    return (0, node_crypto_1.createHash)('sha256').update(text).digest('hex');
}
function statusText(enabled, pending = false) {
    if (pending) {
        return '$(question) Automato: Patch pending';
    }
    return enabled ? '$(pulse) Automato: Watching' : '$(circle-slash) Automato: Paused';
}
function updateStatus(enabled, pending = false) {
    if (!statusBar) {
        return;
    }
    statusBar.text = statusText(enabled, pending);
    statusBar.tooltip = enabled
        ? 'Automato is watching copied text for unified diffs. Click to pause.'
        : 'Automato clipboard watcher is paused. Click to resume.';
    statusBar.show();
}
function dirtyEditorContents(repoRoot) {
    const contents = new Map();
    const root = path.resolve(repoRoot);
    for (const document of vscode.workspace.textDocuments) {
        if (!document.isDirty || document.uri.scheme !== 'file') {
            continue;
        }
        const relative = path.relative(root, document.uri.fsPath);
        if (relative.startsWith('..') || path.isAbsolute(relative)) {
            continue;
        }
        const normalized = relative.replace(/\\/g, '/');
        contents.set(normalized, document.getText());
        if (process.platform === 'win32') {
            contents.set(normalized.toLowerCase(), document.getText());
        }
    }
    return contents;
}
function comparableFilePath(filePath) {
    const resolved = path.resolve(filePath);
    return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
}
function dirtyDocumentsForPrepared(prepared) {
    const targets = new Set(prepared.files
        .filter(file => file.kind !== 'add')
        .map(file => comparableFilePath(path.join(prepared.repoRoot, file.path))));
    return vscode.workspace.textDocuments.filter(document => document.uri.scheme === 'file' &&
        document.isDirty &&
        targets.has(comparableFilePath(document.uri.fsPath)));
}
async function prepareAgainstCurrentEditors(patch, repoRoot) {
    return (0, patch_1.preparePatch)(patch, repoRoot, configuration().whitespaceMatching, dirtyEditorContents(repoRoot));
}
async function promptAndApply(prepared) {
    const config = configuration();
    const dirtyDocuments = dirtyDocumentsForPrepared(prepared);
    if (dirtyDocuments.length === 0) {
        await (0, patch_1.checkPreparedPatch)(prepared, config.stageChanges);
    }
    const summary = (0, patch_1.summarizeFiles)(prepared.files);
    output?.appendLine(`\nPatch detected for ${prepared.repoRoot}:\n${summary}\n`);
    if (config.confirmBeforeApply) {
        updateStatus(true, true);
        const compactFiles = prepared.files
            .slice(0, 5)
            .map(file => `${file.kind === 'add' ? '+' : file.kind === 'delete' ? '−' : '•'} ${file.path}`)
            .join('  ');
        const remaining = prepared.files.length > 5 ? `  …and ${prepared.files.length - 5} more` : '';
        const choice = await vscode.window.showInformationMessage(`Automato found a valid patch (${prepared.hunkCount} hunk${prepared.hunkCount === 1 ? '' : 's'}): ${compactFiles}${remaining}`, { modal: false }, 'Apply', 'Ignore');
        updateStatus(true, false);
        if (choice !== 'Apply') {
            output?.appendLine('Patch ignored.');
            return;
        }
    }
    const currentlyDirtyDocuments = dirtyDocumentsForPrepared(prepared);
    for (const document of currentlyDirtyDocuments) {
        const saved = await document.save();
        if (!saved) {
            throw new patch_1.PatchError(`Could not save ${document.uri.fsPath} before applying the accepted patch.`);
        }
    }
    // Rebuild against disk after approval. This catches edits made while the notification
    // was pending and accommodates format-on-save changes before Git sees the patch.
    const finalPrepared = await (0, patch_1.preparePatch)(prepared.sourceText, prepared.repoRoot, config.whitespaceMatching);
    await (0, patch_1.checkPreparedPatch)(finalPrepared, config.stageChanges);
    await (0, patch_1.applyPreparedPatch)(finalPrepared, config.stageChanges);
    const verb = config.stageChanges ? 'Applied and staged' : 'Applied';
    await vscode.window.showInformationMessage(`${verb} ${finalPrepared.hunkCount} hunk${finalPrepared.hunkCount === 1 ? '' : 's'} in ${finalPrepared.files.length} file${finalPrepared.files.length === 1 ? '' : 's'}.`);
    output?.appendLine(`${verb} patch successfully.`);
}
async function inspectClipboard(showNoPatchMessage) {
    if (!watcher) {
        throw new Error('The Automato clipboard watcher is not initialized.');
    }
    await watcher.inspectNow(showNoPatchMessage);
}
class ClipboardReadError extends Error {
    constructor(message) {
        super(message);
        this.name = 'ClipboardReadError';
    }
}
function delay(milliseconds) {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}
async function readClipboardWithRetry() {
    let lastError;
    for (let attempt = 0; attempt < 7; attempt += 1) {
        try {
            return await vscode.env.clipboard.readText();
        }
        catch (error) {
            lastError = error;
            await delay(30 * (attempt + 1));
        }
    }
    throw new ClipboardReadError(`Windows kept the clipboard temporarily unavailable: ${compactError(lastError)}`);
}
class ClipboardWatcher {
    context;
    watchdogTimer;
    nativeEvents;
    lastHash;
    busy = false;
    enabled = false;
    queued = false;
    queuedForce = false;
    queuedShowNoPatchMessage = false;
    constructor(context) {
        this.context = context;
    }
    async start() {
        if (this.enabled) {
            return;
        }
        this.enabled = true;
        this.lastHash = this.context.globalState.get('automato.lastClipboardHash');
        if (process.platform === 'win32') {
            this.nativeEvents = new clipboardEvents_1.ClipboardChangeEvents(this.context, () => {
                void this.runCheck(true, false);
            }, message => output?.appendLine(message));
            this.nativeEvents.start();
        }
        // Inspect shortly after activation. A patch copied while the extension host was
        // restarting must not be silently adopted as the new baseline.
        this.scheduleWatchdog(100);
        updateStatus(true);
    }
    stop() {
        this.enabled = false;
        if (this.watchdogTimer) {
            clearTimeout(this.watchdogTimer);
            this.watchdogTimer = undefined;
        }
        this.nativeEvents?.dispose();
        this.nativeEvents = undefined;
        this.queued = false;
        this.queuedForce = false;
        this.queuedShowNoPatchMessage = false;
        updateStatus(false);
    }
    async restart() {
        this.stop();
        if (configuration().watchClipboard) {
            await this.start();
        }
    }
    isEnabled() {
        return this.enabled;
    }
    async inspectNow(showNoPatchMessage) {
        if (!vscode.workspace.isTrusted) {
            throw new Error('Automato is disabled until this workspace is trusted.');
        }
        await this.runCheck(true, showNoPatchMessage, true);
    }
    watchdogInterval() {
        const configured = configuration().pollIntervalMs;
        // Windows receives immediate WM_CLIPBOARDUPDATE events. This slower timer is
        // only a safety net in case PowerShell or the native listener is unavailable.
        return process.platform === 'win32' ? Math.max(1500, configured) : configured;
    }
    scheduleWatchdog(delayMs = this.watchdogInterval()) {
        if (!this.enabled) {
            return;
        }
        if (this.watchdogTimer) {
            clearTimeout(this.watchdogTimer);
        }
        this.watchdogTimer = setTimeout(() => {
            this.watchdogTimer = undefined;
            void this.runCheck(false, false).finally(() => {
                if (this.enabled) {
                    this.scheduleWatchdog();
                }
            });
        }, delayMs);
    }
    async rememberHash(hash) {
        this.lastHash = hash;
        try {
            await this.context.globalState.update('automato.lastClipboardHash', hash);
        }
        catch (error) {
            output?.appendLine(`Could not persist clipboard state: ${compactError(error)}`);
        }
    }
    async runCheck(force, showNoPatchMessage, allowWhenPaused = false) {
        if ((!this.enabled && !allowWhenPaused) || !vscode.workspace.isTrusted) {
            return;
        }
        if (this.busy) {
            this.queued = true;
            this.queuedForce = this.queuedForce || force;
            this.queuedShowNoPatchMessage = this.queuedShowNoPatchMessage || showNoPatchMessage;
            return;
        }
        this.busy = true;
        try {
            const text = await readClipboardWithRetry();
            const hash = clipboardHash(text);
            if (!force && hash === this.lastHash) {
                return;
            }
            const patch = (0, patch_1.extractPatchFromClipboard)(text);
            if (!patch) {
                await this.rememberHash(hash);
                if (showNoPatchMessage) {
                    await vscode.window.showInformationMessage('The clipboard does not contain a Git-style unified diff.');
                }
                return;
            }
            // Mark this exact clipboard payload handled only after preparation and the
            // user's decision finish. If the extension host dies midway, startup retries it.
            try {
                const repoRoot = await currentRepositoryRoot();
                const prepared = await prepareAgainstCurrentEditors(patch, repoRoot);
                await promptAndApply(prepared);
            }
            finally {
                await this.rememberHash(hash);
            }
        }
        catch (error) {
            if (error instanceof ClipboardReadError) {
                // Clipboard locking is transient and common while another application is
                // publishing several formats. Stay silent and let the next event/watchdog retry.
                output?.appendLine(error.message);
            }
            else {
                output?.appendLine(`Patch refused: ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
                await vscode.window.showErrorMessage(`Automato refused the copied patch: ${compactError(error)}`);
            }
        }
        finally {
            this.busy = false;
            updateStatus(this.enabled);
            if (this.queued && this.enabled) {
                const queuedForce = this.queuedForce;
                const queuedShow = this.queuedShowNoPatchMessage;
                this.queued = false;
                this.queuedForce = false;
                this.queuedShowNoPatchMessage = false;
                setTimeout(() => {
                    void this.runCheck(queuedForce, queuedShow);
                }, 50);
            }
        }
    }
    dispose() {
        this.stop();
    }
}
async function runCommand(action) {
    try {
        await action();
    }
    catch (error) {
        output?.appendLine(error instanceof Error ? error.stack ?? error.message : String(error));
        const prefix = error instanceof patch_1.PatchError ? 'Patch refused' : 'Automato';
        await vscode.window.showErrorMessage(`${prefix}: ${compactError(error)}`);
    }
}
async function activate(context) {
    output = vscode.window.createOutputChannel(OUTPUT_NAME);
    statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 35);
    statusBar.command = 'automato.toggleWatcher';
    context.subscriptions.push(output, statusBar);
    watcher = new ClipboardWatcher(context);
    context.subscriptions.push(watcher);
    context.subscriptions.push(vscode.commands.registerCommand('automato.copyActiveFile', () => runCommand(() => copyActiveFile(context))), vscode.commands.registerCommand('automato.inspectClipboard', () => runCommand(() => inspectClipboard(true))), vscode.commands.registerCommand('automato.toggleWatcher', () => runCommand(async () => {
        if (!watcher) {
            return;
        }
        if (watcher.isEnabled()) {
            watcher.stop();
            await vscode.workspace.getConfiguration('automato').update('watchClipboard', false, vscode.ConfigurationTarget.Global);
        }
        else {
            await watcher.start();
            await vscode.workspace.getConfiguration('automato').update('watchClipboard', true, vscode.ConfigurationTarget.Global);
        }
    })), vscode.workspace.onDidChangeConfiguration(event => {
        if (event.affectsConfiguration('automato')) {
            void watcher?.restart();
        }
    }));
    if (configuration().watchClipboard) {
        await watcher.start();
    }
    else {
        updateStatus(false);
    }
}
function deactivate() {
    watcher?.dispose();
}
//# sourceMappingURL=extension.js.map