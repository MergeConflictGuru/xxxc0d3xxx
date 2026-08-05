import { execFile } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { promises as fs, watch as watchFileSystem } from 'node:fs';
import type { FSWatcher } from 'node:fs';
import { homedir } from 'node:os';
import * as path from 'node:path';
import { promisify } from 'node:util';
import * as vscode from 'vscode';
import { setWindowsFileAndTextClipboard } from './clipboard';
import { ClipboardChangeEvents } from './clipboardEvents';
import { makePathHeader } from './comments';
import {
    applyPreparedPatch,
    checkPreparedPatch,
    extractPatchFromClipboard,
    PatchError,
    preparePatch,
    PreparedPatch,
    WhitespaceSetting
} from './patch';

const execFileAsync = promisify(execFile);
const OUTPUT_NAME = 'Automato';
const RICKROLL_URL = 'https://www.youtube.com/watch?v=dQw4w9WgXcQ';
const YOUTUBE_URL_REGEX = /(^|[^\w.-])((?:(?:https?:)?\/\/)?(?:[\w-]+\.)?(?:youtube(?:-nocookie)?\.com|youtu\.be)(?::\d+)?(?:\/[^\s<>\"'`]*)?)/gim;

function rickrollYoutubeLinks(text: string): string {
    return text.replace(YOUTUBE_URL_REGEX, (_match, prefix: string, url: string) => {
        const trailingPunctuation = /[),.;:!?\]}]+$/.exec(url)?.[0] ?? '';
        return `${prefix}${RICKROLL_URL}${trailingPunctuation}`;
    });
}

let watcher: ClipboardWatcher | undefined;
let dispatchBus: PatchDispatchBus | undefined;
let downloadsWatcher: DownloadsPatchWatcher | undefined;
let statusBar: vscode.StatusBarItem | undefined;
let output: vscode.OutputChannel | undefined;
let lastDiagnosticsReport = '';

interface AutomatoConfiguration {
    watchPatchSources: boolean;
    pollIntervalMs: number;
    whitespaceMatching: WhitespaceSetting;
    confirmBeforeApply: boolean;
    stageChanges: boolean;
}

function configuration(): AutomatoConfiguration {
    const config = vscode.workspace.getConfiguration('automato');
    return {
        watchPatchSources: config.get<boolean>('watchPatchSources', true),
        pollIntervalMs: Math.max(200, config.get<number>('pollIntervalMs', 650)),
        whitespaceMatching: config.get<WhitespaceSetting>('whitespaceMatching', 'auto'),
        confirmBeforeApply: config.get<boolean>('confirmBeforeApply', true),
        stageChanges: config.get<boolean>('stageChanges', false)
    };
}

function compactError(error: unknown, limit = 520): string {
    const text = error instanceof Error ? error.message : String(error);
    const oneLine = text.replace(/\s*\n\s*/g, ' ').trim();
    return oneLine.length <= limit ? oneLine : oneLine.slice(0, limit - 1).trimEnd() + '…';
}

function normalizeLineEndings(text: string): string {
    return text.replace(/\r\n?/g, '\n');
}

function logDiagnostic(message: string): void {
    const timestamp = new Date().toLocaleTimeString();
    output?.appendLine(`[${timestamp}] ${message}`);
}

function publishDiagnostics(report: string): string {
    lastDiagnosticsReport = report.trim();
    if (lastDiagnosticsReport) {
        output?.appendLine(lastDiagnosticsReport);
    }
    return lastDiagnosticsReport;
}

async function copyDiagnostics(report = lastDiagnosticsReport): Promise<void> {
    const text = report.trim() || 'No patch diagnostics are available.';
    await vscode.env.clipboard.writeText(text);
    await vscode.window.showInformationMessage('Patch diagnostics copied.');
}

let patchPromptActive = false;

function showPatchRejection(message: string, report: string): void {
    // Diagnostics are already in Output. Never let an error notification hide or
    // block an Apply/Ignore prompt; a later valid patch must always take priority.
    if (patchPromptActive) {
        return;
    }
    void vscode.window.showErrorMessage(message, 'Copy Diagnostics').then(choice => {
        if (choice === 'Copy Diagnostics') {
            return copyDiagnostics(report);
        }
        return undefined;
    });
}

async function gitRootForDirectory(directory: string): Promise<string> {
    try {
        const result = await execFileAsync(
            'git', ['-C', directory, 'rev-parse', '--show-toplevel'],
            { windowsHide: true, maxBuffer: 1024 * 1024 }
        );
        return path.resolve(result.stdout.trim());
    } catch {
        throw new Error(`${directory} is not inside a Git repository.`);
    }
}

async function gitRootForPath(filePath: string): Promise<string> {
    return gitRootForDirectory(path.dirname(filePath));
}

async function repositoryRootsInWindow(): Promise<string[]> {
    const candidates: string[] = [];
    const active = vscode.window.activeTextEditor?.document;
    if (active?.uri.scheme === 'file') {
        candidates.push(path.dirname(active.uri.fsPath));
    }
    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        if (folder.uri.scheme === 'file') {
            candidates.push(folder.uri.fsPath);
        }
    }

    const roots: string[] = [];
    const seen = new Set<string>();
    for (const candidate of candidates) {
        try {
            const root = await gitRootForDirectory(candidate);
            const key = process.platform === 'win32' ? root.toLowerCase() : root;
            if (!seen.has(key)) {
                seen.add(key);
                roots.push(root);
            }
        } catch {
            // A multi-root window may contain non-Git folders.
        }
    }
    return roots;
}

interface GitHeaderToken {
    raw: string;
    value: string;
    start: number;
    end: number;
    quoted: boolean;
}

interface MissingPatchPath {
    sectionIndex: number;
    path: string;
}

interface CandidateVerdict {
    status: 'accepted' | 'rejected';
    filePath: string;
    reason?: string;
    details?: string[];
}

interface PatchMismatch {
    filePath: string;
    reason: string;
    details?: string[];
}

interface ResolvedPatch {
    patch: string;
    verdicts: CandidateVerdict[];
}

class PatchReportError extends PatchError {
    public constructor(public readonly verdicts: CandidateVerdict[]) {
        super('Patch rejected.');
        this.name = 'PatchReportError';
    }
}

function decodeGitHeaderToken(raw: string): string {
    if (!raw.startsWith('"')) {
        return raw;
    }

    const bytes: number[] = [];
    for (let index = 1; index < raw.length - 1; index += 1) {
        const character = raw[index];
        if (character !== '\\') {
            bytes.push(...Buffer.from(character, 'utf8'));
            continue;
        }

        index += 1;
        const escaped = raw[index];
        const simpleEscapes: Record<string, number> = {
            'a': 0x07,
            'b': 0x08,
            't': 0x09,
            'n': 0x0a,
            'v': 0x0b,
            'f': 0x0c,
            'r': 0x0d,
            '"': 0x22,
            '\\': 0x5c
        };
        if (escaped in simpleEscapes) {
            bytes.push(simpleEscapes[escaped]);
            continue;
        }
        if (/[0-7]/.test(escaped)) {
            let octal = escaped;
            while (octal.length < 3 && index + 1 < raw.length - 1 && /[0-7]/.test(raw[index + 1])) {
                octal += raw[index + 1];
                index += 1;
            }
            bytes.push(Number.parseInt(octal, 8));
            continue;
        }
        bytes.push(...Buffer.from(escaped, 'utf8'));
    }
    return Buffer.from(bytes).toString('utf8');
}

function encodeGitHeaderToken(value: string, forceQuoted = false): string {
    if (!forceQuoted && !/[\s"\\]/.test(value)) {
        return value;
    }

    let encoded = '"';
    for (const byte of Buffer.from(value, 'utf8')) {
        if (byte === 0x22) {
            encoded += '\\"';
        } else if (byte === 0x5c) {
            encoded += '\\\\';
        } else if (byte === 0x09) {
            encoded += '\\t';
        } else if (byte === 0x0a) {
            encoded += '\\n';
        } else if (byte === 0x0d) {
            encoded += '\\r';
        } else if (byte >= 0x20 && byte <= 0x7e) {
            encoded += String.fromCharCode(byte);
        } else {
            encoded += `\\${byte.toString(8).padStart(3, '0')}`;
        }
    }
    return encoded + '"';
}

function readGitHeaderToken(text: string, start = 0): GitHeaderToken | undefined {
    while (start < text.length && /\s/.test(text[start])) {
        start += 1;
    }
    if (start >= text.length) {
        return undefined;
    }

    let end = start;
    if (text[start] === '"') {
        end += 1;
        let escaped = false;
        while (end < text.length) {
            const character = text[end];
            end += 1;
            if (character === '"' && !escaped) {
                break;
            }
            if (character === '\\' && !escaped) {
                escaped = true;
            } else {
                escaped = false;
            }
        }
    } else {
        while (end < text.length && !/\s/.test(text[end])) {
            end += 1;
        }
    }

    const raw = text.slice(start, end);
    return {
        raw,
        value: decodeGitHeaderToken(raw),
        start,
        end,
        quoted: raw.startsWith('"')
    };
}

function repositoryPathFromHeader(value: string): string | undefined {
    if (value === '/dev/null') {
        return undefined;
    }
    const withoutSide = value.startsWith('a/') || value.startsWith('b/')
        ? value.slice(2)
        : value;
    const normalized = withoutSide.replace(/\\/g, '/');
    if (!normalized || normalized.startsWith('../') || path.posix.isAbsolute(normalized)) {
        return undefined;
    }
    return normalized;
}

function markerPath(section: string, marker: '--- ' | '+++ '): string | undefined {
    const line = section.split(/\r?\n/).find(candidate => candidate.startsWith(marker));
    if (!line) {
        return undefined;
    }
    const token = readGitHeaderToken(line, marker.length);
    return token ? repositoryPathFromHeader(token.value) : undefined;
}

async function fileExists(filePath: string): Promise<boolean> {
    try {
        const stats = await fs.stat(filePath);
        return stats.isFile();
    } catch {
        return false;
    }
}

async function repositoryFiles(repoRoot: string): Promise<string[]> {
    const result = await execFileAsync(
        'git',
        ['-C', repoRoot, 'ls-files', '--cached', '--others', '--exclude-standard', '-z'],
        { windowsHide: true, maxBuffer: 64 * 1024 * 1024 }
    );
    return result.stdout
        .split('\0')
        .filter(Boolean)
        .map(file => file.replace(/\\/g, '/'));
}

function levenshteinDistance(left: string, right: string): number {
    if (left === right) {
        return 0;
    }
    if (left.length === 0) {
        return right.length;
    }
    if (right.length === 0) {
        return left.length;
    }

    let previous = Array.from({ length: right.length + 1 }, (_, index) => index);
    for (let leftIndex = 1; leftIndex <= left.length; leftIndex += 1) {
        const current = [leftIndex];
        for (let rightIndex = 1; rightIndex <= right.length; rightIndex += 1) {
            const substitution = previous[rightIndex - 1] + (left[leftIndex - 1] === right[rightIndex - 1] ? 0 : 1);
            current[rightIndex] = Math.min(
                current[rightIndex - 1] + 1,
                previous[rightIndex] + 1,
                substitution
            );
        }
        previous = current;
    }
    return previous[right.length];
}

function commonSuffixComponents(left: string, right: string): number {
    const leftParts = left.split('/');
    const rightParts = right.split('/');
    let count = 0;
    while (
        count < leftParts.length &&
        count < rightParts.length &&
        leftParts[leftParts.length - 1 - count] === rightParts[rightParts.length - 1 - count]
    ) {
        count += 1;
    }
    return count;
}

function repositoryPathCandidates(wanted: string, candidates: string[]): string[] {
    const caseInsensitive = process.platform === 'win32';
    const comparableWanted = caseInsensitive ? wanted.toLowerCase() : wanted;
    const wantedBase = path.posix.basename(comparableWanted);
    const sameName = candidates.filter(candidate => {
        const comparableCandidate = caseInsensitive ? candidate.toLowerCase() : candidate;
        return path.posix.basename(comparableCandidate) === wantedBase;
    });

    return sameName
        .map(candidate => {
            const comparableCandidate = caseInsensitive ? candidate.toLowerCase() : candidate;
            return {
                candidate,
                suffixComponents: commonSuffixComponents(comparableWanted, comparableCandidate),
                editDistance: levenshteinDistance(comparableWanted, comparableCandidate),
                depthDifference: Math.abs(
                    comparableWanted.split('/').length - comparableCandidate.split('/').length
                ),
                depth: comparableCandidate.split('/').length
            };
        })
        .sort((left, right) =>
            right.suffixComponents - left.suffixComponents ||
            left.editDistance - right.editDistance ||
            left.depthDifference - right.depthDifference ||
            left.depth - right.depth ||
            left.candidate.localeCompare(right.candidate)
        )
        .map(item => item.candidate);
}

function rewriteMarkerLine(
    line: string,
    marker: '--- ' | '+++ ',
    oldPath: string,
    newPath: string
): string {
    if (!line.startsWith(marker)) {
        return line;
    }
    const token = readGitHeaderToken(line, marker.length);
    if (!token || repositoryPathFromHeader(token.value) !== oldPath) {
        return line;
    }

    const side = token.value.startsWith('a/') || token.value.startsWith('b/')
        ? token.value.slice(0, 2)
        : '';
    const replacement = encodeGitHeaderToken(`${side}${newPath}`, token.quoted);
    return line.slice(0, token.start) + replacement + line.slice(token.end);
}

function rewriteDiffHeaderLine(line: string, oldPath: string, newPath: string): string {
    const prefix = 'diff --git ';
    if (!line.startsWith(prefix)) {
        return line;
    }

    const first = readGitHeaderToken(line, prefix.length);
    const second = first ? readGitHeaderToken(line, first.end) : undefined;
    if (!first || !second) {
        return line;
    }

    let firstRaw = first.raw;
    let secondRaw = second.raw;
    if (repositoryPathFromHeader(first.value) === oldPath) {
        const side = first.value.startsWith('a/') || first.value.startsWith('b/') ? first.value.slice(0, 2) : '';
        firstRaw = encodeGitHeaderToken(`${side}${newPath}`, first.quoted);
    }
    if (repositoryPathFromHeader(second.value) === oldPath) {
        const side = second.value.startsWith('a/') || second.value.startsWith('b/') ? second.value.slice(0, 2) : '';
        secondRaw = encodeGitHeaderToken(`${side}${newPath}`, second.quoted);
    }

    return line.slice(0, first.start) + firstRaw + line.slice(first.end, second.start) + secondRaw + line.slice(second.end);
}

function rewriteSectionPath(section: string, oldPath: string, newPath: string): string {
    return section
        .split(/(\r?\n)/)
        .map(part => {
            if (part === '\n' || part === '\r\n') {
                return part;
            }
            return rewriteMarkerLine(
                rewriteMarkerLine(
                    rewriteDiffHeaderLine(part, oldPath, newPath),
                    '--- ',
                    oldPath,
                    newPath
                ),
                '+++ ',
                oldPath,
                newPath
            );
        })
        .join('');
}

async function resolveMissingPatchPaths(patch: string, repoRoot: string): Promise<ResolvedPatch> {
    const sections = patch.split(/(?=^diff --git )/m);
    const missing: MissingPatchPath[] = [];
    const verdicts: CandidateVerdict[] = [];

    for (let sectionIndex = 0; sectionIndex < sections.length; sectionIndex += 1) {
        const oldPath = markerPath(sections[sectionIndex], '--- ');
        const newPath = markerPath(sections[sectionIndex], '+++ ');

        // Additions and renames intentionally refer to a path that may not exist yet.
        // Only repair ordinary modifications where both patch sides name the same file.
        if (!oldPath || !newPath || oldPath !== newPath) {
            continue;
        }
        if (!await fileExists(path.join(repoRoot, oldPath))) {
            missing.push({ sectionIndex, path: oldPath });
        }
    }

    if (missing.length === 0) {
        return { patch, verdicts };
    }

    const files = await repositoryFiles(repoRoot);
    const editorContents = dirtyEditorContents(repoRoot);
    for (const item of missing) {
        const candidates = repositoryPathCandidates(item.path, files);
        if (candidates.length === 0) {
            verdicts.push({
                status: 'rejected',
                filePath: path.join(repoRoot, item.path),
                reason: 'file not found'
            });
            throw new PatchReportError(verdicts);
        }

        const accepted: string[] = [];
        const candidateVerdicts: CandidateVerdict[] = [];
        for (const candidate of candidates) {
            const rewrittenSection = rewriteSectionPath(sections[item.sectionIndex], item.path, candidate);
            try {
                await preparePatch(
                    rewrittenSection,
                    repoRoot,
                    configuration().whitespaceMatching,
                    editorContents
                );
                accepted.push(candidate);
                candidateVerdicts.push({
                    status: 'accepted',
                    filePath: path.join(repoRoot, candidate)
                });
            } catch (error) {
                candidateVerdicts.push(await rejectedVerdictForPatch(
                    error,
                    rewrittenSection,
                    repoRoot,
                    editorContents
                ));
            }
        }

        if (accepted.length === 1) {
            sections[item.sectionIndex] = rewriteSectionPath(
                sections[item.sectionIndex],
                item.path,
                accepted[0]
            );
            verdicts.push(...candidateVerdicts);
            continue;
        }

        if (accepted.length > 1) {
            const matchingFiles = accepted.map(candidate => path.join(repoRoot, candidate));
            const acceptedKeys = new Set(matchingFiles.map(comparableFilePath));
            const reason = `multiple files match: ${matchingFiles.join(' | ')}`;
            verdicts.push(...candidateVerdicts.map(verdict =>
                verdict.status === 'accepted' && acceptedKeys.has(comparableFilePath(verdict.filePath))
                    ? { ...verdict, status: 'rejected' as const, reason }
                    : verdict
            ));
        } else {
            verdicts.push(...candidateVerdicts);
        }
        throw new PatchReportError(verdicts);
    }

    return { patch: sections.join(''), verdicts };
}

function safeCopiedFileName(original: string, relativePath: string, hasComment: boolean): string {
    if (hasComment) {
        return original;
    }
    const encodedPath = relativePath.replace(/[^a-zA-Z0-9._-]+/g, '!');
    const extension = path.extname(original);
    const maximumStemLength = Math.max(24, 220 - extension.length);
    return (`FILE_PATH__${encodedPath}`).slice(0, maximumStemLength) + extension;
}

async function copyActiveFile(context: vscode.ExtensionContext): Promise<void> {
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

    const pathHeader = await makePathHeader(document.languageId, relativePath);
    const originalText = document.getText();
    const firstLine = originalText.split(/\r?\n/, 1)[0].replace(/^\uFEFF/, '');
    let payload = pathHeader.header && firstLine !== pathHeader.header
        ? `${pathHeader.header}\n${originalText}`
        : originalText;
    payload = rickrollYoutubeLinks(payload);

    await fs.mkdir(context.globalStorageUri.fsPath, { recursive: true });
    const existingEntries = await fs.readdir(context.globalStorageUri.fsPath, { withFileTypes: true });
    await Promise.all(existingEntries
        .filter(entry => entry.isDirectory() && entry.name.startsWith('copy-'))
        .map(entry => fs.rm(path.join(context.globalStorageUri.fsPath, entry.name), { recursive: true, force: true })));
    const copyDirectory = await fs.mkdtemp(path.join(context.globalStorageUri.fsPath, 'copy-'));
    const copiedName = safeCopiedFileName(path.basename(document.uri.fsPath), relativePath, pathHeader.usedComment);
    const copiedPath = path.join(copyDirectory, copiedName);
    await fs.writeFile(copiedPath, payload, 'utf8');

    await setWindowsFileAndTextClipboard(context, copiedPath, payload);
    const unsavedNote = document.isDirty ? ' including unsaved editor changes' : '';
    await vscode.window.showInformationMessage(
        `Copied ${relativePath}${unsavedNote} as a file with text fallback.`
    );
}

function clipboardHash(text: string): string {
    return createHash('sha256').update(text).digest('hex');
}

function statusText(enabled: boolean, pending = false): string {
    if (pending) {
        return '$(question) Automato: Patch pending';
    }
    return enabled ? '$(pulse) Automato: Watching' : '$(circle-slash) Automato: Paused';
}

function updateStatus(enabled: boolean, pending = false): void {
    if (!statusBar) {
        return;
    }
    statusBar.text = statusText(enabled, pending);
    statusBar.tooltip = enabled
        ? 'Automato is watching the clipboard and Downloads for patches. Click to pause.'
        : 'Automato patch sources are paused. Click to resume.';
    statusBar.show();
}

function dirtyEditorContents(repoRoot: string): Map<string, string> {
    const contents = new Map<string, string>();
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

function comparableFilePath(filePath: string): string {
    const resolved = path.resolve(filePath);
    return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
}

function dirtyDocumentsForPrepared(prepared: PreparedPatch): vscode.TextDocument[] {
    const targets = new Set(prepared.files
        .filter(file => file.kind !== 'add')
        .map(file => comparableFilePath(path.join(prepared.repoRoot, file.path))));
    return vscode.workspace.textDocuments.filter(document =>
        document.uri.scheme === 'file' &&
        document.isDirty &&
        targets.has(comparableFilePath(document.uri.fsPath))
    );
}

async function prepareAgainstCurrentEditors(
    patch: string,
    repoRoot: string
): Promise<{ prepared: PreparedPatch; verdicts: CandidateVerdict[] }> {
    const editorContents = dirtyEditorContents(repoRoot);
    const resolved = await resolveMissingPatchPaths(normalizeLineEndings(patch), repoRoot);
    try {
        const prepared = await preparePatch(
            resolved.patch,
            repoRoot,
            configuration().whitespaceMatching,
            editorContents
        );
        const known = new Set(resolved.verdicts.map(verdict => comparableFilePath(verdict.filePath)));
        const verdicts = [...resolved.verdicts];
        for (const file of prepared.files) {
            const filePath = path.join(repoRoot, file.path);
            if (!known.has(comparableFilePath(filePath))) {
                verdicts.push({ status: 'accepted', filePath });
            }
        }
        return { prepared, verdicts };
    } catch (error) {
        if (error instanceof PatchReportError) {
            throw error;
        }
        throw new PatchReportError([
            ...resolved.verdicts,
            await rejectedVerdictForPatch(error, resolved.patch, repoRoot, editorContents)
        ]);
    }
}

function editorTextForPath(contents: Map<string, string> | undefined, relativePath: string): string | undefined {
    if (!contents) {
        return undefined;
    }
    return contents.get(relativePath) ??
        (process.platform === 'win32' ? contents.get(relativePath.toLowerCase()) : undefined);
}

function blockMatches(lines: string[], start: number, expected: string[]): boolean {
    return expected.every((line, offset) => lines[start + offset] === line);
}

async function firstPatchMismatch(
    patch: string,
    repoRoot: string,
    editorContents?: Map<string, string>
): Promise<PatchMismatch | undefined> {
    for (const section of patch.split(/(?=^diff --git )/m)) {
        const oldPath = markerPath(section, '--- ');
        const newPath = markerPath(section, '+++ ');
        if (!oldPath) {
            if (newPath && await fileExists(path.join(repoRoot, newPath))) {
                return {
                    filePath: newPath,
                    reason: 'file already exists'
                };
            }
            continue;
        }

        let currentText = editorTextForPath(editorContents, oldPath);
        if (currentText === undefined) {
            try {
                currentText = await fs.readFile(path.join(repoRoot, oldPath), 'utf8');
            } catch (error) {
                if (isMissingFileError(error)) {
                    return {
                        filePath: oldPath,
                        reason: 'file not found'
                    };
                }
                continue;
            }
        }

        const currentLines = normalizeLineEndings(currentText).split('\n');
        const patchLines = normalizeLineEndings(section).split('\n');
        for (let index = 0; index < patchLines.length; index += 1) {
            const hunkHeader = patchLines[index];
            const header = /^@@ -(\d+)(?:,\d+)? \+\d+(?:,\d+)? @@/.exec(hunkHeader);
            if (!header) {
                continue;
            }

            const oldStart = Number.parseInt(header[1], 10);
            const expected: string[] = [];
            for (index += 1; index < patchLines.length && !patchLines[index].startsWith('@@ '); index += 1) {
                const prefix = patchLines[index][0];
                if (prefix === ' ' || prefix === '-') {
                    expected.push(patchLines[index].slice(1));
                }
            }
            index -= 1;

            const declaredStart = Math.max(0, oldStart - 1);
            if (expected.length === 0 || blockMatches(currentLines, declaredStart, expected)) {
                continue;
            }
            if (currentLines.some((_line, possibleStart) => blockMatches(currentLines, possibleStart, expected))) {
                continue;
            }

            for (let offset = 0; offset < expected.length; offset += 1) {
                const found = currentLines[declaredStart + offset];
                if (found !== expected[offset]) {
                    const lineNumber = oldStart + offset;
                    return {
                        filePath: oldPath,
                        reason: `${hunkHeader} does not match`,
                        details: [
                            `expected ${lineNumber}: ${JSON.stringify(expected[offset])}`,
                            `found    ${lineNumber}: ${found === undefined ? '<EOF>' : JSON.stringify(found)}`
                        ]
                    };
                }
            }
        }
    }
    return undefined;
}

function firstPatchFile(patch: string): string | undefined {
    for (const section of patch.split(/(?=^diff --git )/m)) {
        const target = markerPath(section, '--- ') ?? markerPath(section, '+++ ');
        if (target) {
            return target;
        }
    }
    return undefined;
}

function patchErrorFile(error: unknown): string | undefined {
    const message = error instanceof Error ? error.message : String(error);
    return /^(.+?):\s+hunk\s+\d+\b/.exec(message)?.[1];
}

function displayFilePath(repoRoot: string, filePath: string): string {
    return path.isAbsolute(filePath) || path.win32.isAbsolute(filePath)
        ? filePath
        : path.join(repoRoot, filePath);
}

async function rejectedVerdictForPatch(
    error: unknown,
    patch: string,
    repoRoot: string,
    editorContents?: Map<string, string>
): Promise<CandidateVerdict> {
    try {
        const mismatch = await firstPatchMismatch(patch, repoRoot, editorContents);
        if (mismatch) {
            return {
                status: 'rejected',
                filePath: displayFilePath(repoRoot, mismatch.filePath),
                reason: mismatch.reason,
                details: mismatch.details
            };
        }
    } catch {
        // The original patch error is still the most useful fallback.
    }

    const target = patchErrorFile(error) ?? firstPatchFile(patch);
    return {
        status: 'rejected',
        filePath: target ? displayFilePath(repoRoot, target) : repoRoot,
        reason: compactError(error)
    };
}

function formatCandidateReport(verdicts: CandidateVerdict[]): string {
    const seen = new Set<string>();
    const lines: string[] = [];
    for (const verdict of verdicts) {
        const details = verdict.details ?? [];
        const key = [verdict.status, comparableFilePath(verdict.filePath), verdict.reason ?? '', ...details].join('\0');
        if (seen.has(key)) {
            continue;
        }
        seen.add(key);
        const accepted = verdict.status === 'accepted';
        lines.push(
            `${accepted ? 'INFO  🟢 ACCEPTED' : 'ERROR 🔴 REJECTED'} ${verdict.filePath}` +
            (verdict.reason ? ` — ${verdict.reason}` : '')
        );
        lines.push(...details.map(detail => `      ${detail}`));
    }
    return lines.join('\n');
}

async function promptAndApply(prepared: PreparedPatch): Promise<'applied' | 'ignored'> {
    const config = configuration();
    const dirtyDocuments = dirtyDocumentsForPrepared(prepared);
    if (dirtyDocuments.length === 0) {
        await checkPreparedPatch(prepared, config.stageChanges);
    }

    updateStatus(true, true);
    patchPromptActive = true;
    const compactFiles = prepared.files
        .slice(0, 5)
        .map(file => `${file.kind === 'add' ? '+' : file.kind === 'delete' ? '−' : '•'} ${file.path}`)
        .join('  ');
    const remaining = prepared.files.length > 5 ? `  …and ${prepared.files.length - 5} more` : '';
    let choice: string | undefined;
    try {
        choice = await vscode.window.showInformationMessage(
            `Automato found a valid patch (${prepared.hunkCount} hunk${prepared.hunkCount === 1 ? '' : 's'}): ${compactFiles}${remaining}`,
            { modal: true },
            'Apply',
            'Ignore'
        );
    } finally {
        patchPromptActive = false;
        updateStatus(true, false);
    }
    if (choice !== 'Apply') {
        return 'ignored';
    }

    const currentlyDirtyDocuments = dirtyDocumentsForPrepared(prepared);
    for (const document of currentlyDirtyDocuments) {
        const saved = await document.save();
        if (!saved) {
            throw new PatchError(`Could not save ${document.uri.fsPath} before applying the accepted patch.`);
        }
    }

    // Rebuild against disk after approval. This catches edits made while the notification
    // was pending and accommodates format-on-save changes before Git sees the patch.
    const finalPrepared = await preparePatch(
        normalizeLineEndings(prepared.sourceText),
        prepared.repoRoot,
        config.whitespaceMatching
    );
    await checkPreparedPatch(finalPrepared, config.stageChanges);
    await applyPreparedPatch(finalPrepared, config.stageChanges);
    const verb = config.stageChanges ? 'Applied and staged' : 'Applied';
    await vscode.window.showInformationMessage(
        `${verb} ${finalPrepared.hunkCount} hunk${finalPrepared.hunkCount === 1 ? '' : 's'} in ${finalPrepared.files.length} file${finalPrepared.files.length === 1 ? '' : 's'}.`
    );
    return 'applied';
}

async function rescanAll(): Promise<void> {
    if (!watcher) {
        throw new Error('The Automato clipboard watcher is not initialized.');
    }
    if (!downloadsWatcher) {
        throw new Error('The Automato Downloads watcher is not initialized.');
    }

    const generation = randomUUID();
    const [, summary] = await Promise.all([
        watcher.inspectNow(false, generation),
        downloadsWatcher.inspectNow(false, generation)
    ]);
    vscode.window.setStatusBarMessage(
        `$(check) Automato rescanned clipboard and ${summary.directories} Downloads folder${summary.directories === 1 ? '' : 's'}; ` +
        `found ${summary.validPatches} downloaded patch${summary.validPatches === 1 ? '' : 'es'} ` +
        `among ${summary.candidates} candidate file${summary.candidates === 1 ? '' : 's'}.`,
        7000
    );
}

class ClipboardReadError extends Error {
    public constructor(message: string) {
        super(message);
        this.name = 'ClipboardReadError';
    }
}

function delay(milliseconds: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function readClipboardWithRetry(): Promise<string> {
    let lastError: unknown;
    for (let attempt = 0; attempt < 7; attempt += 1) {
        try {
            return await vscode.env.clipboard.readText();
        } catch (error) {
            lastError = error;
            await delay(30 * (attempt + 1));
        }
    }
    throw new ClipboardReadError(
        `Windows kept the clipboard temporarily unavailable: ${compactError(lastError)}`
    );
}

interface PatchDispatch {
    id: string;
    patch: string;
    source: string;
    createdAt: number;
    publisherInstanceId?: string;
}

interface PatchCandidate {
    repoRoot: string;
    prepared: PreparedPatch;
    exactPathMatch: boolean;
    exactPathCount: number;
    verdicts: CandidateVerdict[];
}

interface PatchCandidateSearch {
    candidate?: PatchCandidate;
    report: string;
}

async function exactPatchPathCoverage(patch: string, repoRoot: string): Promise<{ exact: number; total: number }> {
    let exact = 0;
    let total = 0;
    for (const section of patch.split(/(?=^diff --git )/m)) {
        const oldPath = markerPath(section, '--- ');
        const newPath = markerPath(section, '+++ ');
        if (!oldPath || !newPath || oldPath !== newPath) {
            continue;
        }
        total += 1;
        if (await fileExists(path.join(repoRoot, oldPath))) {
            exact += 1;
        }
    }
    return { exact, total };
}

async function bestPatchCandidateInWindow(patch: string): Promise<PatchCandidateSearch> {
    const roots = await repositoryRootsInWindow();
    const candidates: PatchCandidate[] = [];
    const rejected: CandidateVerdict[] = [];
    for (const repoRoot of roots) {
        const coverage = await exactPatchPathCoverage(patch, repoRoot);
        try {
            const result = await prepareAgainstCurrentEditors(patch, repoRoot);
            candidates.push({
                repoRoot,
                prepared: result.prepared,
                exactPathMatch: coverage.total > 0 && coverage.exact === coverage.total,
                exactPathCount: coverage.exact,
                verdicts: result.verdicts
            });
        } catch (error) {
            if (error instanceof PatchReportError) {
                rejected.push(...error.verdicts);
            } else {
                rejected.push(await rejectedVerdictForPatch(
                    error,
                    patch,
                    repoRoot,
                    dirtyEditorContents(repoRoot)
                ));
            }
        }
    }

    candidates.sort((left, right) =>
        Number(right.exactPathMatch) - Number(left.exactPathMatch) ||
        right.exactPathCount - left.exactPathCount ||
        left.repoRoot.localeCompare(right.repoRoot)
    );

    if (candidates.length === 0) {
        if (roots.length === 0) {
            rejected.push({ status: 'rejected', filePath: '(no open Git repository)', reason: 'repository not found' });
        }
        return { report: formatCandidateReport(rejected) };
    }

    if (candidates.length > 1) {
        const rootsList = candidates.map(candidate => candidate.repoRoot).join(' | ');
        const verdicts = [
            ...rejected,
            ...candidates.flatMap(candidate => candidate.verdicts.map(verdict =>
                verdict.status === 'accepted'
                    ? { ...verdict, status: 'rejected' as const, reason: `multiple repositories match: ${rootsList}` }
                    : verdict
            ))
        ];
        return { report: formatCandidateReport(verdicts) };
    }

    const candidate = candidates[0];
    return {
        candidate,
        report: formatCandidateReport(candidate.verdicts)
    };
}

function isAlreadyExistsError(error: unknown): boolean {
    return error instanceof Error && 'code' in error && (error as NodeJS.ErrnoException).code === 'EEXIST';
}

function isMissingFileError(error: unknown): boolean {
    return error instanceof Error && 'code' in error && (error as NodeJS.ErrnoException).code === 'ENOENT';
}

class PatchDispatchBus implements vscode.Disposable {
    private readonly instanceId = `${process.pid}-${randomUUID()}`;
    private readonly dispatchDirectory: string;
    private fileWatcher: FSWatcher | undefined;
    private scanTimer: NodeJS.Timeout | undefined;
    private periodicTimer: NodeJS.Timeout | undefined;
    private active = false;
    private scanning = false;
    private rescanRequested = false;
    private readonly seenGenerations = new Map<string, number>();
    private readonly processing = new Set<string>();

    public constructor() {
        // This location is shared by every local VS Code process/profile for the same
        // OS user, unlike extension-host memory or per-window Memento state.
        this.dispatchDirectory = path.join(homedir(), '.automato-vscode', 'patch-dispatch');
    }

    public async start(): Promise<void> {
        if (this.active) {
            return;
        }
        this.active = true;
        await fs.mkdir(this.dispatchDirectory, { recursive: true });
        try {
            this.fileWatcher = watchFileSystem(this.dispatchDirectory, { persistent: false }, () => {
                this.scheduleScan(25);
            });
            this.fileWatcher.on('error', error => {
                output?.appendLine(`Patch dispatch watcher: ${compactError(error)}`);
            });
        } catch (error) {
            output?.appendLine(`Could not watch patch dispatch directory: ${compactError(error)}`);
        }
        this.periodicTimer = setInterval(() => this.scheduleScan(0), 500);
        this.scheduleScan(0);
    }

    public stop(): void {
        this.active = false;
        if (this.scanTimer) {
            clearTimeout(this.scanTimer);
            this.scanTimer = undefined;
        }
        if (this.periodicTimer) {
            clearInterval(this.periodicTimer);
            this.periodicTimer = undefined;
        }
        this.fileWatcher?.close();
        this.fileWatcher = undefined;
    }

    public async publish(patch: string, source: string, freshGeneration?: string): Promise<void> {
        await fs.mkdir(this.dispatchDirectory, { recursive: true });
        const normalizedPatch = normalizeLineEndings(patch);
        const id = clipboardHash(
            freshGeneration === undefined ? normalizedPatch : `${normalizedPatch}\0${freshGeneration}`
        );
        const dispatchPath = this.dispatchPath(id);
        const dispatch: PatchDispatch = {
            id,
            patch: normalizedPatch,
            source,
            createdAt: Date.now(),
            publisherInstanceId: this.instanceId
        };

        try {
            await fs.writeFile(dispatchPath, JSON.stringify(dispatch), { encoding: 'utf8', flag: 'wx' });
            vscode.window.setStatusBarMessage(`$(diff) Automato detected a patch from ${source}`, 5000);
        } catch (error) {
            if (!isAlreadyExistsError(error)) {
                throw error;
            }
            try {
                const stats = await fs.stat(dispatchPath);
                const ageMs = Date.now() - stats.mtimeMs;
                if (ageMs > 8_000) {
                    // Treat a deliberate re-copy as a fresh attempt, even when the old
                    // attempt was ignored or left a stale claim behind.
                    await this.removeDispatchFiles(id);
                    await fs.writeFile(dispatchPath, JSON.stringify(dispatch), { encoding: 'utf8', flag: 'wx' });
                    vscode.window.setStatusBarMessage(`$(diff) Automato re-detected a patch from ${source}`, 5000);
                } else {
                    vscode.window.setStatusBarMessage(`$(diff) Automato detected an already-routed patch`, 5000);
                }
            } catch (retryError) {
                if (!isAlreadyExistsError(retryError) && !isMissingFileError(retryError)) {
                    throw retryError;
                }
            }
        }
        this.scheduleScan(0);
    }

    private dispatchPath(id: string): string {
        return path.join(this.dispatchDirectory, `dispatch-${id}.json`);
    }

    private claimPath(id: string): string {
        return path.join(this.dispatchDirectory, `dispatch-${id}.claim`);
    }

    private scheduleScan(delayMs: number): void {
        if (!this.active) {
            return;
        }
        if (this.scanTimer) {
            clearTimeout(this.scanTimer);
        }
        this.scanTimer = setTimeout(() => {
            this.scanTimer = undefined;
            void this.scan();
        }, delayMs);
    }

    private async scan(): Promise<void> {
        if (!this.active) {
            return;
        }
        if (this.scanning) {
            this.rescanRequested = true;
            return;
        }
        this.scanning = true;
        try {
            await this.cleanupOldDispatches();
            const names = await fs.readdir(this.dispatchDirectory);
            for (const name of names) {
                const match = /^dispatch-([a-f0-9]{64})\.json$/.exec(name);
                if (!match) {
                    continue;
                }
                const dispatchPath = path.join(this.dispatchDirectory, name);
                let dispatch: PatchDispatch;
                try {
                    dispatch = JSON.parse(await fs.readFile(dispatchPath, 'utf8')) as PatchDispatch;
                } catch (error) {
                    if (!isMissingFileError(error)) {
                        output?.appendLine(`Could not read patch dispatch ${name}: ${compactError(error)}`);
                    }
                    continue;
                }
                if (this.seenGenerations.get(dispatch.id) === dispatch.createdAt || this.processing.has(dispatch.id)) {
                    continue;
                }
                this.processing.add(dispatch.id);
                try {
                    await this.processDispatch(dispatch);
                } finally {
                    this.processing.delete(dispatch.id);
                }
            }
        } catch (error) {
            if (!isMissingFileError(error)) {
                output?.appendLine(`Patch dispatch scan failed: ${compactError(error)}`);
            }
        } finally {
            this.scanning = false;
            if (this.rescanRequested) {
                this.rescanRequested = false;
                this.scheduleScan(0);
            }
        }
    }

    private async processDispatch(dispatch: PatchDispatch): Promise<void> {
        if (!vscode.workspace.isTrusted) {
            this.seenGenerations.set(dispatch.id, dispatch.createdAt);
            return;
        }

        const search = await bestPatchCandidateInWindow(dispatch.patch);
        const candidate = search.candidate;
        if (!candidate) {
            // Give exact and fuzzy matches in every other VS Code window time to claim
            // the dispatch before reporting a failure in the window the user focuses.
            if (await this.claimExists(dispatch.id)) {
                this.seenGenerations.set(dispatch.id, dispatch.createdAt);
                return;
            }

            const settleRemaining = 4000 - (Date.now() - dispatch.createdAt);
            if (settleRemaining > 0 || !vscode.window.state.focused) {
                setTimeout(() => this.scheduleScan(0), settleRemaining > 0 ? settleRemaining : 1000);
                return;
            }

            if (!await this.dispatchIsCurrent(dispatch)) {
                this.seenGenerations.set(dispatch.id, dispatch.createdAt);
                return;
            }

            this.seenGenerations.set(dispatch.id, dispatch.createdAt);
            const report = publishDiagnostics(search.report || 'ERROR 🔴 REJECTED patch — no matching file');
            showPatchRejection('Automato rejected the patch.', report);
            await this.removeDispatchFiles(dispatch.id);
            return;
        }

        if (!candidate.exactPathMatch) {
            // A fuzzy same-filename repair is useful, but an exact-path repository in
            // another window must get first refusal before this window can claim it.
            const settleRemaining = 2500 - (Date.now() - dispatch.createdAt);
            if (settleRemaining > 0) {
                setTimeout(() => this.scheduleScan(0), settleRemaining);
                return;
            }
        }

        // Only the focused VS Code window may claim a valid patch. Otherwise a
        // background window can own the dispatch and show the Apply dialog off-screen.
        if (!vscode.window.state.focused) {
            setTimeout(() => this.scheduleScan(0), 500);
            return;
        }

        if (!await this.dispatchIsCurrent(dispatch)) {
            this.seenGenerations.set(dispatch.id, dispatch.createdAt);
            return;
        }

        const claimed = await this.tryClaim(dispatch, candidate);
        if (!claimed) {
            this.seenGenerations.set(dispatch.id, dispatch.createdAt);
            return;
        }

        try {
            publishDiagnostics(search.report);
            await promptAndApply(candidate.prepared);
            this.seenGenerations.set(dispatch.id, dispatch.createdAt);
            await this.removeDispatchFiles(dispatch.id);
        } catch (error) {
            await fs.rm(this.claimPath(dispatch.id), { force: true });
            this.seenGenerations.set(dispatch.id, dispatch.createdAt);
            const rejection = await rejectedVerdictForPatch(
                error,
                candidate.prepared.sourceText,
                candidate.repoRoot
            );
            const report = publishDiagnostics(formatCandidateReport([rejection]));
            showPatchRejection(
                `Automato rejected the patch: ${rejection.reason ?? 'unknown reason'}`,
                report
            );
            await this.removeDispatchFiles(dispatch.id);
        }
    }

    private async dispatchIsCurrent(dispatch: PatchDispatch): Promise<boolean> {
        try {
            const current = JSON.parse(await fs.readFile(this.dispatchPath(dispatch.id), 'utf8')) as PatchDispatch;
            return current.createdAt === dispatch.createdAt;
        } catch {
            return false;
        }
    }

    private async claimExists(id: string): Promise<boolean> {
        try {
            await fs.access(this.claimPath(id));
            return true;
        } catch {
            return false;
        }
    }

    private async tryClaim(dispatch: PatchDispatch, candidate: PatchCandidate): Promise<boolean> {
        const claimPath = this.claimPath(dispatch.id);
        const claim = JSON.stringify({
            instanceId: this.instanceId,
            repoRoot: candidate.repoRoot,
            exactPathMatch: candidate.exactPathMatch,
            claimedAt: Date.now()
        });
        try {
            await fs.writeFile(claimPath, claim, { encoding: 'utf8', flag: 'wx' });
            return true;
        } catch (error) {
            if (!isAlreadyExistsError(error)) {
                throw error;
            }
            try {
                const stats = await fs.stat(claimPath);
                let belongsToOlderDispatch = false;
                try {
                    const existing = JSON.parse(await fs.readFile(claimPath, 'utf8')) as { claimedAt?: number };
                    belongsToOlderDispatch = typeof existing.claimedAt === 'number' &&
                        existing.claimedAt < dispatch.createdAt;
                } catch {
                    belongsToOlderDispatch = stats.mtimeMs < dispatch.createdAt;
                }
                if (belongsToOlderDispatch || Date.now() - stats.mtimeMs > 60_000) {
                    await fs.rm(claimPath, { force: true });
                    await fs.writeFile(claimPath, claim, { encoding: 'utf8', flag: 'wx' });
                    return true;
                }
            } catch (retryError) {
                if (!isAlreadyExistsError(retryError) && !isMissingFileError(retryError)) {
                    throw retryError;
                }
            }
            return false;
        }
    }

    private async cleanupOldDispatches(): Promise<void> {
        const names = await fs.readdir(this.dispatchDirectory);
        const now = Date.now();
        for (const name of names) {
            const match = /^dispatch-([a-f0-9]{64})\.json$/.exec(name);
            if (!match) {
                continue;
            }
            const filePath = path.join(this.dispatchDirectory, name);
            try {
                const stats = await fs.stat(filePath);
                if (now - stats.mtimeMs > 120_000) {
                    await this.removeDispatchFiles(match[1]);
                }
            } catch {
                // Another extension host may be cleaning the same dispatch.
            }
        }
    }

    private async removeDispatchFiles(id: string): Promise<void> {
        await Promise.all([
            fs.rm(this.dispatchPath(id), { force: true }),
            fs.rm(this.claimPath(id), { force: true })
        ]);
        this.seenGenerations.delete(id);
    }

    public dispose(): void {
        this.stop();
    }
}

interface DownloadsScanSummary {
    directories: number;
    candidates: number;
    validPatches: number;
}

function expandWindowsEnvironment(value: string): string {
    return value.replace(/%([^%]+)%/g, (_match, name: string) => process.env[name] ?? `%${name}%`);
}

async function configuredDownloadsDirectories(): Promise<string[]> {
    const candidates = new Set<string>();
    candidates.add(path.join(homedir(), 'Downloads'));

    if (process.platform === 'win32') {
        const oneDrive = process.env.OneDrive ?? process.env.OneDriveConsumer;
        if (oneDrive) {
            candidates.add(path.join(oneDrive, 'Downloads'));
        }

        const key = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders';
        for (const valueName of ['{374DE290-123F-4565-9164-39C4925E467B}', 'Downloads']) {
            try {
                const result = await execFileAsync('reg', ['query', key, '/v', valueName], {
                    windowsHide: true,
                    maxBuffer: 1024 * 1024
                });
                for (const line of result.stdout.split(/\r?\n/)) {
                    const match = /\sREG_(?:EXPAND_)?SZ\s+(.+?)\s*$/.exec(line);
                    if (match) {
                        candidates.add(expandWindowsEnvironment(match[1].trim().replace(/^"|"$/g, '')));
                    }
                }
            } catch {
                // The default profile Downloads path remains available as a fallback.
            }
        }
    }

    const directories: string[] = [];
    const seen = new Set<string>();
    for (const candidate of candidates) {
        const resolved = path.resolve(candidate);
        const key = process.platform === 'win32' ? resolved.toLowerCase() : resolved;
        if (seen.has(key)) {
            continue;
        }
        seen.add(key);
        try {
            const stats = await fs.stat(resolved);
            if (stats.isDirectory()) {
                directories.push(resolved);
            }
        } catch {
            // A candidate may not exist on this machine.
        }
    }
    return directories;
}

class DownloadsPatchWatcher implements vscode.Disposable {
    private static readonly knownFilesStorageKey = 'automato.downloadSignatures.v1';
    private directories: string[] = [];
    private readonly fileWatchers = new Map<string, FSWatcher>();
    private scanTimer: NodeJS.Timeout | undefined;
    private periodicTimer: NodeJS.Timeout | undefined;
    private active = false;
    private initialized = false;
    private scanning = false;
    private rescanRequested = false;
    private readonly knownFiles = new Map<string, string>();

    public constructor(
        private readonly bus: PatchDispatchBus,
        private readonly context: vscode.ExtensionContext
    ) {}

    public async start(): Promise<void> {
        if (this.active) {
            return;
        }
        this.active = true;
        this.restoreKnownFiles();
        this.directories = await configuredDownloadsDirectories();
        if (this.directories.length === 0) {
            logDiagnostic('Downloads patch watcher could not find an existing Downloads directory.');
            return;
        }

        await this.scan(true, false);
        for (const directory of this.directories) {
            try {
                const watcher = watchFileSystem(directory, { persistent: false }, () => {
                    this.scheduleScan(200);
                });
                watcher.on('error', error => {
                    logDiagnostic(`Downloads watcher for ${directory}: ${compactError(error)}`);
                });
                this.fileWatchers.set(directory, watcher);
            } catch (error) {
                logDiagnostic(`Could not watch ${directory}: ${compactError(error)}`);
            }
        }
        this.periodicTimer = setInterval(() => this.scheduleScan(0), 1500);
    }

    public stop(): void {
        this.active = false;
        if (this.scanTimer) {
            clearTimeout(this.scanTimer);
            this.scanTimer = undefined;
        }
        if (this.periodicTimer) {
            clearInterval(this.periodicTimer);
            this.periodicTimer = undefined;
        }
        for (const watcher of this.fileWatchers.values()) {
            watcher.close();
        }
        this.fileWatchers.clear();
    }

    public async restart(): Promise<void> {
        this.stop();
        this.initialized = false;
        this.knownFiles.clear();
        this.directories = [];
        if (configuration().watchPatchSources) {
            await this.start();
        }
    }

    private restoreKnownFiles(): void {
        this.knownFiles.clear();
        const stored = this.context.globalState.get<Record<string, string>>(
            DownloadsPatchWatcher.knownFilesStorageKey,
            {}
        );
        for (const [filePath, signature] of Object.entries(stored)) {
            if (typeof signature === 'string') {
                this.knownFiles.set(filePath, signature);
            }
        }
    }

    private async persistKnownFiles(): Promise<void> {
        await this.context.globalState.update(
            DownloadsPatchWatcher.knownFilesStorageKey,
            Object.fromEntries(this.knownFiles)
        );
    }

    public async inspectNow(
        showMessage = true,
        freshGeneration = randomUUID()
    ): Promise<DownloadsScanSummary> {
        while (this.scanning) {
            await delay(50);
        }
        if (this.directories.length === 0) {
            this.directories = await configuredDownloadsDirectories();
        }
        if (this.directories.length === 0) {
            throw new Error('No existing Downloads directory could be located. Open Automato diagnostics for details.');
        }

        const summary = await this.scan(false, true, freshGeneration);
        if (showMessage) {
            const message = summary.validPatches > 0
                ? `Scanned ${summary.directories} Downloads folder${summary.directories === 1 ? '' : 's'}: ` +
                  `found and broadcast ${summary.validPatches} valid patch${summary.validPatches === 1 ? '' : 'es'} ` +
                  `among ${summary.candidates} candidate file${summary.candidates === 1 ? '' : 's'}.`
                : `Scanned ${summary.directories} Downloads folder${summary.directories === 1 ? '' : 's'}: ` +
                  `none of ${summary.candidates} candidate file${summary.candidates === 1 ? '' : 's'} contained a Git-style patch.`;
            await vscode.window.showInformationMessage(message, 'Copy Diagnostics').then(choice => {
                if (choice === 'Copy Diagnostics') {
                    void copyDiagnostics();
                }
            });
        }
        return summary;
    }

    private scheduleScan(delayMs: number): void {
        if (!this.active) {
            return;
        }
        if (this.scanTimer) {
            clearTimeout(this.scanTimer);
        }
        this.scanTimer = setTimeout(() => {
            this.scanTimer = undefined;
            void this.scan(false, false);
        }, delayMs);
    }

    private async scan(
        initial: boolean,
        forcePublish: boolean,
        freshGeneration?: string
    ): Promise<DownloadsScanSummary> {
        const summary: DownloadsScanSummary = {
            directories: this.directories.length,
            candidates: 0,
            validPatches: 0
        };
        if ((!this.active && !forcePublish) || this.scanning) {
            if (this.scanning) {
                this.rescanRequested = true;
            }
            return summary;
        }

        this.scanning = true;
        try {
            const present = new Set<string>();
            for (const directory of this.directories) {
                let entries;
                try {
                    entries = await fs.readdir(directory, { withFileTypes: true });
                } catch (error) {
                    if (!isMissingFileError(error)) {
                        logDiagnostic(`Downloads scan failed for ${directory}: ${compactError(error)}`);
                    }
                    continue;
                }

                for (const entry of entries) {
                    if (!entry.isFile() || !this.isPatchFile(entry.name)) {
                        continue;
                    }
                    summary.candidates += 1;
                    const filePath = path.join(directory, entry.name);
                    present.add(filePath);
                    try {
                        const stats = await fs.stat(filePath);
                        const signature = `${stats.size}:${stats.mtimeMs}`;
                        const previous = this.knownFiles.get(filePath);
                        this.knownFiles.set(filePath, signature);

                        const changed = previous !== undefined && previous !== signature;
                        const newlyAppeared = previous === undefined && (initial || this.initialized);
                        const shouldPublish = forcePublish || changed || newlyAppeared;
                        if (!shouldPublish) {
                            continue;
                        }

                        if (await this.publishFile(filePath, freshGeneration)) {
                            summary.validPatches += 1;
                        }
                    } catch (error) {
                        if (!isMissingFileError(error)) {
                            logDiagnostic(`Could not inspect downloaded patch ${entry.name}: ${compactError(error)}`);
                        }
                    }
                }
            }

            for (const filePath of this.knownFiles.keys()) {
                if (!present.has(filePath)) {
                    this.knownFiles.delete(filePath);
                }
            }
            await this.persistKnownFiles();
            if (initial) {
            }
            this.initialized = true;
        } catch (error) {
            if (!isMissingFileError(error)) {
                logDiagnostic(`Downloads scan failed: ${compactError(error)}`);
            }
        } finally {
            this.scanning = false;
            if (this.rescanRequested) {
                this.rescanRequested = false;
                this.scheduleScan(0);
            }
        }
        return summary;
    }

    private isPatchFile(fileName: string): boolean {
        const extension = path.extname(fileName).toLowerCase();
        return extension === '.patch' || extension === '.diff' || extension === '.txt';
    }

    private async publishFile(filePath: string, freshGeneration?: string): Promise<boolean> {
        let lastError: unknown;
        for (let attempt = 0; attempt < 5; attempt += 1) {
            try {
                const stats = await fs.stat(filePath);
                if (stats.size > 16 * 1024 * 1024) {
                    logDiagnostic(`Ignored oversized patch candidate ${filePath}.`);
                    return false;
                }
                const text = await fs.readFile(filePath, 'utf8');
                const patch = extractPatchFromClipboard(text);
                if (!patch) {
                    logDiagnostic(`Downloads file ${filePath} was read, but it contained no Git-style unified diff.`);
                    return false;
                }
                await this.bus.publish(patch, filePath, freshGeneration);
                return true;
            } catch (error) {
                lastError = error;
                await delay(100 * (attempt + 1));
            }
        }
        logDiagnostic(`Could not read downloaded patch ${filePath}: ${compactError(lastError)}`);
        return false;
    }

    public dispose(): void {
        this.stop();
    }
}

class ClipboardWatcher implements vscode.Disposable {
    private watchdogTimer: NodeJS.Timeout | undefined;
    private nativeEvents: ClipboardChangeEvents | undefined;
    private lastHash: string | undefined;
    private busy = false;
    private enabled = false;
    private queued = false;
    private queuedForce = false;
    private queuedShowNoPatchMessage = false;
    private queuedAllowWhenPaused = false;
    private queuedFreshGeneration: string | undefined;

    public constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly bus: PatchDispatchBus
    ) {}

    public async start(): Promise<void> {
        if (this.enabled) {
            return;
        }
        this.enabled = true;
        // lastHash is deliberately process-local. A global hash shared by all VS Code
        // windows turns clipboard handling into a race where the first host wins.

        if (process.platform === 'win32') {
            this.nativeEvents = new ClipboardChangeEvents(
                this.context,
                () => {
                    void this.runCheck(true, false);
                },
                message => output?.appendLine(message)
            );
            this.nativeEvents.start();
        }

        this.scheduleWatchdog(100);
        updateStatus(true);
    }

    public stop(): void {
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
        this.queuedAllowWhenPaused = false;
        this.queuedFreshGeneration = undefined;
        updateStatus(false);
    }

    public async restart(): Promise<void> {
        this.stop();
        if (configuration().watchPatchSources) {
            await this.start();
        }
    }

    public isEnabled(): boolean {
        return this.enabled;
    }

    public async inspectNow(
        showNoPatchMessage: boolean,
        freshGeneration = randomUUID()
    ): Promise<void> {
        if (!vscode.workspace.isTrusted) {
            throw new Error('Automato is disabled until this workspace is trusted.');
        }
        await this.runCheck(true, showNoPatchMessage, true, freshGeneration);
    }

    private watchdogInterval(): number {
        const configured = configuration().pollIntervalMs;
        return process.platform === 'win32' ? Math.max(1500, configured) : configured;
    }

    private scheduleWatchdog(delayMs = this.watchdogInterval()): void {
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

    private rememberHash(hash: string): void {
        this.lastHash = hash;
    }

    private async runCheck(
        force: boolean,
        showNoPatchMessage: boolean,
        allowWhenPaused = false,
        freshGeneration?: string
    ): Promise<void> {
        if ((!this.enabled && !allowWhenPaused) || !vscode.workspace.isTrusted) {
            return;
        }
        if (this.busy) {
            this.queued = true;
            this.queuedForce = this.queuedForce || force;
            this.queuedShowNoPatchMessage = this.queuedShowNoPatchMessage || showNoPatchMessage;
            this.queuedAllowWhenPaused = this.queuedAllowWhenPaused || allowWhenPaused;
            this.queuedFreshGeneration = freshGeneration ?? this.queuedFreshGeneration;
            return;
        }

        this.busy = true;
        try {
            const text = await readClipboardWithRetry();
            const hash = clipboardHash(text);
            if (!force && hash === this.lastHash) {
                return;
            }

            const patch = extractPatchFromClipboard(text);
            if (!patch) {
                this.rememberHash(hash);
                if (showNoPatchMessage) {
                    await vscode.window.showInformationMessage(
                        'The clipboard does not contain a Git-style unified diff.'
                    );
                }
                return;
            }

            await this.bus.publish(patch, 'clipboard', freshGeneration);
            this.rememberHash(hash);
        } catch (error) {
            if (error instanceof ClipboardReadError) {
                output?.appendLine(error.message);
            } else {
                logDiagnostic(`Clipboard inspection failed: ${compactError(error)}`);
                await vscode.window.showErrorMessage(`Automato could not inspect the clipboard: ${compactError(error)}`);
            }
        } finally {
            this.busy = false;
            updateStatus(this.enabled);
            if (this.queued && (this.enabled || this.queuedAllowWhenPaused)) {
                const queuedForce = this.queuedForce;
                const queuedShow = this.queuedShowNoPatchMessage;
                const queuedAllowWhenPaused = this.queuedAllowWhenPaused;
                const queuedFreshGeneration = this.queuedFreshGeneration;
                this.queued = false;
                this.queuedForce = false;
                this.queuedShowNoPatchMessage = false;
                this.queuedAllowWhenPaused = false;
                this.queuedFreshGeneration = undefined;
                setTimeout(() => {
                    void this.runCheck(
                        queuedForce,
                        queuedShow,
                        queuedAllowWhenPaused,
                        queuedFreshGeneration
                    );
                }, 50);
            }
        }
    }

    public dispose(): void {
        this.stop();
    }
}

async function runCommand(action: () => Promise<void>): Promise<void> {
    try {
        await action();
    } catch (error) {
        const prefix = error instanceof PatchError ? 'Patch refused' : 'Automato';
        const report = publishDiagnostics(`ERROR 🔴 REJECTED ${prefix} — ${compactError(error)}`);
        const choice = await vscode.window.showErrorMessage(
            `${prefix}: ${compactError(error)}`,
            'Copy Diagnostics'
        );
        if (choice === 'Copy Diagnostics') {
            await copyDiagnostics(report);
        }
    }
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    output = vscode.window.createOutputChannel(OUTPUT_NAME, 'log');
    statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 35);
    statusBar.command = 'automato.toggleWatcher';
    context.subscriptions.push(output, statusBar);
    dispatchBus = new PatchDispatchBus();
    watcher = new ClipboardWatcher(context, dispatchBus);
    downloadsWatcher = new DownloadsPatchWatcher(dispatchBus, context);
    context.subscriptions.push(dispatchBus, watcher, downloadsWatcher);
    await dispatchBus.start();

    const restartSources = async (): Promise<void> => {
        await watcher?.restart();
        await downloadsWatcher?.restart();
    };

    context.subscriptions.push(
        vscode.commands.registerCommand('automato.copyActiveFile', () => runCommand(() => copyActiveFile(context))),
        vscode.commands.registerCommand('automato.rescanAll', () => runCommand(rescanAll)),
        vscode.commands.registerCommand('automato.showDiagnostics', () => copyDiagnostics()),
        vscode.commands.registerCommand('automato.toggleWatcher', () => runCommand(async () => {
            if (!watcher) {
                return;
            }
            if (watcher.isEnabled()) {
                watcher.stop();
                downloadsWatcher?.stop();
                await vscode.workspace.getConfiguration('automato').update(
                    'watchPatchSources', false, vscode.ConfigurationTarget.Global
                );
            } else {
                await watcher.start();
                await downloadsWatcher?.start();
                await vscode.workspace.getConfiguration('automato').update(
                    'watchPatchSources', true, vscode.ConfigurationTarget.Global
                );
            }
        })),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('automato')) {
                void restartSources();
            }
        })
    );

    if (configuration().watchPatchSources) {
        await watcher.start();
        await downloadsWatcher.start();
    } else {
        updateStatus(false);
        logDiagnostic('Clipboard and Downloads patch watchers are disabled by automato.watchPatchSources.');
    }
}

export function deactivate(): void {
    watcher?.dispose();
    downloadsWatcher?.dispose();
    dispatchBus?.dispose();
}
