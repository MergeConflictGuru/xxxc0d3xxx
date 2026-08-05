import { execFile } from 'node:child_process';
import { promises as fs } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

export type ChangeKind = 'modify' | 'add' | 'delete';
export type WhitespaceSetting = 'auto' | 'exact' | 'indent';
type MatchMode = 'exact' | 'indent';
type LineEnding = '\n' | '\r\n';

export class PatchError extends Error {
    public constructor(message: string) {
        super(message);
        this.name = 'PatchError';
    }
}

interface Hunk {
    headerIndex: number;
    bodyStart: number;
    bodyEnd: number;
    suffix: string;
    declaredOldStart?: number;
    oldLines: string[];
    newLines: string[];
    oldCount: number;
    newCount: number;
    matchIndex: number;
    actualOldLines?: string[];
    ignoredBodyIndexes: number[];
    syntheticPathBodyIndex?: number;
    syntheticPathOldOrdinal?: number;
    syntheticPathNewOrdinal?: number;
}

export interface FilePatch {
    oldPath?: string;
    newPath?: string;
    path: string;
    originalPath: string;
    kind: ChangeKind;
    hunks: Hunk[];
    mode: MatchMode;
    lineEnding: LineEnding;
    diffHeaderIndex?: number;
    oldPathRecordIndex: number;
    newPathRecordIndex: number;
}

export interface PreparedPatch {
    repoRoot: string;
    sourceText: string;
    correctedText: string;
    files: FilePatch[];
    hunkCount: number;
    usedEditorBuffers: string[];
}

interface LocatedHunk {
    index: number;
    actualOldLines: string[];
    oldLines: string[];
    newLines: string[];
    ignoredBodyIndexes: number[];
}

interface PathMatchScore {
    overlap: number;
    extra: number;
    suffix: number;
}

interface LocatedCandidate {
    path: string;
    mode: MatchMode;
    matches: LocatedHunk[];
    usedEditorBuffer: boolean;
    lineEnding?: LineEnding;
    pathScore: PathMatchScore;
}

const INDENT_INSENSITIVE_EXTENSIONS = new Set([
    '.c', '.h', '.cc', '.hh', '.cpp', '.hpp', '.cxx', '.hxx',
    '.cs', '.java', '.js', '.jsx', '.mjs', '.cjs', '.ts', '.tsx',
    '.go', '.rs', '.swift', '.kt', '.kts', '.scala', '.php',
    '.css', '.scss', '.less', '.html', '.htm', '.xhtml', '.vue',
    '.svelte', '.json', '.jsonc', '.xml', '.xsl', '.xslt', '.sql',
    '.dart', '.groovy', '.gradle', '.sol', '.proto'
]);

const EXACT_EXTENSIONS = new Set([
    '.py', '.pyw', '.pyi', '.yaml', '.yml', '.mk', '.mak',
    '.nim', '.coffee', '.pug', '.haml', '.slim', '.sass'
]);

const EXACT_FILENAMES = new Set(['makefile', 'gnumakefile', 'bsdmakefile']);

export function normalizeNewlines(text: string): string {
    return text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
}

function detectLineEnding(text: string): LineEnding | undefined {
    let crlf = 0;
    let lf = 0;
    for (let index = 0; index < text.length; index += 1) {
        if (text[index] !== '\n') {
            continue;
        }
        if (index > 0 && text[index - 1] === '\r') {
            crlf += 1;
        } else {
            lf += 1;
        }
    }
    if (crlf === 0 && lf === 0) {
        return undefined;
    }
    return crlf >= lf ? '\r\n' : '\n';
}

async function gitConfigValue(repoRoot: string, key: string): Promise<string | undefined> {
    try {
        const result = await execFileAsync(
            'git', ['-C', repoRoot, 'config', '--get', key],
            { windowsHide: true, maxBuffer: 1024 * 1024, encoding: 'utf8' }
        );
        const value = result.stdout.trim();
        return value || undefined;
    } catch {
        return undefined;
    }
}

async function gitEolAttribute(repoRoot: string, relativePath: string): Promise<string | undefined> {
    try {
        const result = await execFileAsync(
            'git', ['-C', repoRoot, 'check-attr', '-z', 'eol', '--', relativePath],
            { windowsHide: true, maxBuffer: 1024 * 1024, encoding: 'utf8' }
        );
        const value = result.stdout.split('\0')[2];
        return value && value !== 'unspecified' && value !== 'unset' ? value : undefined;
    } catch {
        return undefined;
    }
}

async function repositoryLineEnding(repoRoot: string, relativePath: string): Promise<LineEnding> {
    const attribute = (await gitEolAttribute(repoRoot, relativePath))?.toLowerCase();
    if (attribute === 'crlf') {
        return '\r\n';
    }
    if (attribute === 'lf') {
        return '\n';
    }

    const autoCrlf = (await gitConfigValue(repoRoot, 'core.autocrlf'))?.toLowerCase();
    if (autoCrlf === 'true') {
        return '\r\n';
    }
    if (autoCrlf === 'input') {
        return '\n';
    }

    const configuredEol = (await gitConfigValue(repoRoot, 'core.eol'))?.toLowerCase();
    if (configuredEol === 'crlf') {
        return '\r\n';
    }
    if (configuredEol === 'native') {
        return process.platform === 'win32' ? '\r\n' : '\n';
    }
    return '\n';
}

function splitLinesKeepEnds(text: string): string[] {
    return text.match(/[^\n]*\n|[^\n]+$/g) ?? [];
}

function unquoteGitPath(raw: string): string {
    if (!raw.startsWith('"')) {
        return raw.split('\t', 1)[0];
    }

    const bytes: number[] = [];
    for (let i = 1; i < raw.length; i += 1) {
        const char = raw[i];
        if (char === '"') {
            return Buffer.from(bytes).toString('utf8');
        }
        if (char !== '\\') {
            bytes.push(...Buffer.from(char, 'utf8'));
            continue;
        }

        i += 1;
        if (i >= raw.length) {
            throw new PatchError(`Invalid quoted Git path: ${raw}`);
        }
        const escaped = raw[i];
        const simple: Record<string, number> = {
            'a': 7, 'b': 8, 't': 9, 'n': 10, 'v': 11, 'f': 12,
            'r': 13, '"': 34, '\\': 92
        };
        if (simple[escaped] !== undefined) {
            bytes.push(simple[escaped]);
            continue;
        }
        if (/[0-7]/.test(escaped)) {
            let octal = escaped;
            while (octal.length < 3 && i + 1 < raw.length && /[0-7]/.test(raw[i + 1])) {
                i += 1;
                octal += raw[i];
            }
            bytes.push(Number.parseInt(octal, 8));
            continue;
        }
        bytes.push(...Buffer.from(escaped, 'utf8'));
    }
    throw new PatchError(`Unterminated quoted Git path: ${raw}`);
}

function decodeGitPath(raw: string): string | undefined {
    const decoded = unquoteGitPath(raw.trimEnd());
    if (decoded === '/dev/null') {
        return undefined;
    }
    // Git conventionally writes a/ and b/ as synthetic diff-side prefixes. They are
    // not repository directories, so always remove exactly one such prefix.
    const withoutPrefix = decoded.startsWith('a/') || decoded.startsWith('b/')
        ? decoded.slice(2)
        : decoded;
    if (!withoutPrefix) {
        throw new PatchError('Patch contains an empty file path.');
    }
    return withoutPrefix.replace(/\\/g, '/');
}

function encodeGitPath(relativePath: string, prefix: 'a/' | 'b/' | ''): string {
    const value = prefix + relativePath.replace(/\\/g, '/');
    if (!/[\s"\\]/.test(value)) {
        return value;
    }
    return `"${value.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/\t/g, '\\t')}"`;
}

function parseHunkHeader(line: string, lineNumber: number): { suffix: string; oldStart?: number } {
    const full = /^@@\s+-(\d+)(?:,\d+)?\s+\+\d+(?:,\d+)?\s+@@(.*?)(?:\n)?$/.exec(line);
    if (full) {
        return { suffix: full[2], oldStart: Number.parseInt(full[1], 10) };
    }
    const trimmed = line.trimEnd();
    if (trimmed === '@@' || trimmed === '@@ @@') {
        return { suffix: '' };
    }
    throw new PatchError(`Malformed hunk header at patch line ${lineNumber}: ${trimmed}`);
}

function parseOldSide(body: string[], patchLine: number, allowEmptyOldSide: boolean): {
    oldLines: string[];
    newLines: string[];
    oldCount: number;
    newCount: number;
} {
    const oldLines: string[] = [];
    const newLines: string[] = [];
    let oldCount = 0;
    let newCount = 0;
    let previousPrefix: string | undefined;

    for (let offset = 0; offset < body.length; offset += 1) {
        const line = body[offset];
        const currentLine = patchLine + offset;
        if (!line) {
            throw new PatchError(`Empty physical line at patch line ${currentLine}.`);
        }
        const prefix = line[0];
        if (prefix === ' ') {
            oldLines.push(line.slice(1));
            newLines.push(line.slice(1));
            oldCount += 1;
            newCount += 1;
        } else if (prefix === '-') {
            oldLines.push(line.slice(1));
            oldCount += 1;
        } else if (prefix === '+') {
            newLines.push(line.slice(1));
            newCount += 1;
        } else if (prefix === '\\') {
            if (!line.startsWith('\\ No newline at end of file')) {
                throw new PatchError(`Unknown backslash record at patch line ${currentLine}: ${line.trimEnd()}`);
            }
            if ((previousPrefix === ' ' || previousPrefix === '-') && oldLines.length > 0) {
                oldLines[oldLines.length - 1] = oldLines[oldLines.length - 1].replace(/\n$/, '');
            }
            if ((previousPrefix === ' ' || previousPrefix === '+') && newLines.length > 0) {
                newLines[newLines.length - 1] = newLines[newLines.length - 1].replace(/\n$/, '');
            }
            previousPrefix = prefix;
            continue;
        } else {
            throw new PatchError(
                `Invalid hunk record at patch line ${currentLine}; expected space, +, or -, got ${line.trimEnd()}`
            );
        }
        previousPrefix = prefix;
    }

    if (oldCount === 0 && !allowEmptyOldSide) {
        throw new PatchError(
            `A hunk near patch line ${patchLine - 1} has no old-side context. ` +
            'Automato cannot safely locate a context-free insertion in an existing file.'
        );
    }
    return { oldLines, newLines, oldCount, newCount };
}

function removeEmailTrailer(text: string): string {
    const lines = splitLinesKeepEnds(text);
    for (let i = 0; i + 1 < lines.length; i += 1) {
        if (lines[i].trimEnd() === '--' && /^\d+(?:\.\d+)+(?:\s|$)/.test(lines[i + 1].trim())) {
            return lines.slice(0, i).join('');
        }
    }
    return text;
}

function isPathPairAt(lines: string[], index: number): boolean {
    if (!lines[index]?.startsWith('--- ') || !lines[index + 1]?.startsWith('+++ ')) {
        return false;
    }
    // A genuine text-file header is immediately followed by a hunk. Requiring this
    // avoids mistaking deleted/added source lines beginning with -- and ++ for paths.
    return lines[index + 2]?.startsWith('@@') ?? false;
}

function firstPatchLine(lines: string[]): number | undefined {
    const gitStart = lines.findIndex(line => line.startsWith('diff --git '));
    const plainStart = lines.findIndex((_line, index) => isPathPairAt(lines, index));
    if (gitStart < 0) {
        return plainStart < 0 ? undefined : plainStart;
    }
    if (plainStart < 0) {
        return gitStart;
    }
    return Math.min(gitStart, plainStart);
}

function looksLikePatch(text: string): boolean {
    const lines = splitLinesKeepEnds(normalizeNewlines(text));
    return firstPatchLine(lines) !== undefined;
}

export function extractPatchFromClipboard(input: string): string | undefined {
    let text = normalizeNewlines(input).replace(/^\uFEFF/, '');
    const fenced = [...text.matchAll(/```(?:diff|patch)?\s*\n([\s\S]*?)```/gi)]
        .map(match => match[1])
        .find(candidate => looksLikePatch(candidate));
    if (fenced) {
        text = fenced;
    }

    const lines = splitLinesKeepEnds(text);
    const startLine = firstPatchLine(lines);
    if (startLine === undefined) {
        return undefined;
    }
    text = removeEmailTrailer(lines.slice(startLine).join('')).trimEnd() + '\n';
    if (!looksLikePatch(text)) {
        return undefined;
    }
    return text;
}

interface SectionDescriptor {
    start: number;
    end: number;
    oldRecord: number;
    newRecord: number;
    diffHeader?: number;
}

function findSections(lines: string[]): SectionDescriptor[] {
    const pairs: Array<{ oldRecord: number; newRecord: number }> = [];
    for (let index = 0; index < lines.length - 2; index += 1) {
        if (isPathPairAt(lines, index)) {
            pairs.push({ oldRecord: index, newRecord: index + 1 });
        }
    }
    if (pairs.length === 0) {
        throw new PatchError('No ---/+++ file header followed by a text hunk was found.');
    }

    const descriptors: SectionDescriptor[] = [];
    let previousEnd = 0;
    for (let index = 0; index < pairs.length; index += 1) {
        const pair = pairs[index];
        let diffHeader: number | undefined;
        for (let cursor = pair.oldRecord - 1; cursor >= previousEnd; cursor -= 1) {
            if (lines[cursor].startsWith('diff --git ')) {
                diffHeader = cursor;
                break;
            }
        }
        const start = diffHeader ?? pair.oldRecord;
        const nextPair = pairs[index + 1];
        let end = lines.length;
        if (nextPair) {
            let nextDiffHeader: number | undefined;
            for (let cursor = nextPair.oldRecord - 1; cursor > pair.newRecord; cursor -= 1) {
                if (lines[cursor].startsWith('diff --git ')) {
                    nextDiffHeader = cursor;
                    break;
                }
            }
            end = nextDiffHeader ?? nextPair.oldRecord;
        }
        descriptors.push({
            start,
            end,
            oldRecord: pair.oldRecord,
            newRecord: pair.newRecord,
            diffHeader
        });
        previousEnd = end;
    }
    return descriptors;
}

export function parseGitPatch(patchText: string): { lines: string[]; files: FilePatch[] } {
    const lines = splitLinesKeepEnds(normalizeNewlines(patchText));
    const sections = findSections(lines);
    const files: FilePatch[] = [];

    for (const descriptor of sections) {
        const { start, end, oldRecord, newRecord, diffHeader } = descriptor;
        const section = lines.slice(start, end);

        if (section.some(line => line.startsWith('GIT binary patch') || line.startsWith('Binary files '))) {
            throw new PatchError(`Binary patches are not supported (section starting at line ${start + 1}).`);
        }
        if (section.some(line => line.startsWith('Submodule '))) {
            throw new PatchError(`Submodule patches are not supported (section starting at line ${start + 1}).`);
        }
        if (section.some(line => line.startsWith('rename from ') || line.startsWith('rename to ') ||
            line.startsWith('copy from ') || line.startsWith('copy to '))) {
            throw new PatchError('Rename and copy patches are not supported yet; use delete plus add instead.');
        }

        const oldPath = decodeGitPath(lines[oldRecord].slice(4));
        const newPath = decodeGitPath(lines[newRecord].slice(4));
        const kind: ChangeKind = oldPath === undefined ? 'add' : newPath === undefined ? 'delete' : 'modify';
        const targetPath = newPath ?? oldPath;
        if (!targetPath) {
            throw new PatchError(`Invalid /dev/null paths near patch line ${start + 1}.`);
        }
        if (oldPath && newPath && oldPath !== newPath) {
            throw new PatchError(`Path-changing patches are not supported: ${oldPath} -> ${newPath}`);
        }

        const headerIndexes = lines
            .map((line, index) => index >= newRecord + 1 && index < end && line.startsWith('@@') ? index : -1)
            .filter(index => index >= 0);
        if (headerIndexes.length === 0) {
            throw new PatchError(`No text hunks found for ${targetPath}.`);
        }

        const hunks: Hunk[] = [];
        for (let hunkIndex = 0; hunkIndex < headerIndexes.length; hunkIndex += 1) {
            const headerIndex = headerIndexes[hunkIndex];
            const bodyStart = headerIndex + 1;
            const bodyEnd = hunkIndex + 1 < headerIndexes.length ? headerIndexes[hunkIndex + 1] : end;
            const header = parseHunkHeader(lines[headerIndex], headerIndex + 1);
            const parsed = parseOldSide(lines.slice(bodyStart, bodyEnd), bodyStart + 1, kind === 'add');
            hunks.push({
                headerIndex,
                bodyStart,
                bodyEnd,
                suffix: header.suffix,
                declaredOldStart: header.oldStart,
                oldLines: parsed.oldLines,
                newLines: parsed.newLines,
                oldCount: parsed.oldCount,
                newCount: parsed.newCount,
                matchIndex: -1,
                ignoredBodyIndexes: []
            });
        }

        // Preserve the full hunk first. Matching later tries it exactly as written before
        // trying the AI-only synthetic path header removed.
        if (kind !== 'add' && hunks.length > 0) {
            const firstHunk = hunks[0];
            for (let bodyIndex = firstHunk.bodyStart; bodyIndex < firstHunk.bodyEnd; bodyIndex += 1) {
                const record = lines[bodyIndex];
                if (record.startsWith(' ') && /\bFILE PATH\s*:/i.test(record.slice(1))) {
                    firstHunk.syntheticPathBodyIndex = bodyIndex;
                    firstHunk.syntheticPathOldOrdinal = lines
                        .slice(firstHunk.bodyStart, bodyIndex)
                        .filter(candidate => candidate.startsWith(' ') || candidate.startsWith('-')).length;
                    firstHunk.syntheticPathNewOrdinal = lines
                        .slice(firstHunk.bodyStart, bodyIndex)
                        .filter(candidate => candidate.startsWith(' ') || candidate.startsWith('+')).length;
                    break;
                }
            }
        }

        if (kind === 'add' && hunks.some(hunk => hunk.oldCount !== 0)) {
            throw new PatchError(`New-file patch for ${targetPath} unexpectedly contains old-side content.`);
        }
        files.push({
            oldPath,
            newPath,
            path: targetPath,
            originalPath: targetPath,
            kind,
            hunks,
            mode: 'exact',
            lineEnding: '\n',
            diffHeaderIndex: diffHeader,
            oldPathRecordIndex: oldRecord,
            newPathRecordIndex: newRecord
        });
    }
    return { lines, files };
}

function modeForPath(filePath: string, configured: WhitespaceSetting): MatchMode {
    if (configured === 'exact' || configured === 'indent') {
        return configured;
    }
    const extension = path.extname(filePath).toLowerCase();
    const filename = path.basename(filePath).toLowerCase();
    if (EXACT_FILENAMES.has(filename) || EXACT_EXTENSIONS.has(extension)) {
        return 'exact';
    }
    return INDENT_INSENSITIVE_EXTENSIONS.has(extension) ? 'indent' : 'exact';
}

function normalizeMatchLine(line: string, mode: MatchMode): string {
    if (mode === 'exact') {
        return line;
    }
    const hadNewline = line.endsWith('\n');
    const core = (hadNewline ? line.slice(0, -1) : line).replace(/^[ \t]+/, '').replace(/[ \t]+$/, '');
    return core + (hadNewline ? '\n' : '');
}

function findAllSubsequences(haystack: string[], needle: string[], mode: MatchMode): number[] {
    if (needle.length === 0 || needle.length > haystack.length) {
        return [];
    }
    const normalizedNeedle = needle.map(line => normalizeMatchLine(line, mode));
    const matches: number[] = [];
    for (let index = 0; index <= haystack.length - needle.length; index += 1) {
        let matched = true;
        for (let offset = 0; offset < needle.length; offset += 1) {
            if (normalizeMatchLine(haystack[index + offset], mode) !== normalizedNeedle[offset]) {
                matched = false;
                break;
            }
        }
        if (matched) {
            matches.push(index);
        }
    }
    return matches;
}

function secureTarget(repoRoot: string, relativePath: string): string {
    const normalized = relativePath.replace(/\\/g, '/');
    if (normalized === '.git' || normalized.startsWith('.git/')) {
        throw new PatchError('Patches targeting .git are refused.');
    }
    if (path.isAbsolute(relativePath)) {
        throw new PatchError(`Absolute patch path is refused: ${relativePath}`);
    }
    const target = path.resolve(repoRoot, relativePath);
    const relative = path.relative(path.resolve(repoRoot), target);
    if (relative.startsWith('..') || path.isAbsolute(relative)) {
        throw new PatchError(`Patch path escapes the repository: ${relativePath}`);
    }
    return target;
}

function mapLookup(contents: ReadonlyMap<string, string> | undefined, relativePath: string): string | undefined {
    const normalized = relativePath.replace(/\\/g, '/');
    return contents?.get(normalized) ??
        (process.platform === 'win32' ? contents?.get(normalized.toLowerCase()) : undefined);
}

async function repositoryTopLevel(directory: string): Promise<string> {
    try {
        const result = await execFileAsync(
            'git', ['-C', directory, 'rev-parse', '--show-toplevel'],
            { windowsHide: true, maxBuffer: 1024 * 1024, encoding: 'utf8' }
        );
        return path.resolve(result.stdout.trim());
    } catch (error) {
        const details = error as { stderr?: string; stdout?: string; message?: string };
        throw new PatchError(
            `Cannot find repository root:\n${(details.stderr || details.stdout || details.message || String(error)).trim()}`
        );
    }
}

async function listRepositoryFiles(repoRoot: string): Promise<string[]> {
    try {
        const result = await execFileAsync(
            'git',
            ['-C', repoRoot, 'ls-files', '--cached', '--others', '--exclude-standard', '--full-name', '-z'],
            { windowsHide: true, maxBuffer: 64 * 1024 * 1024, encoding: 'buffer' }
        );
        return result.stdout.toString('utf8').split('\0').filter(Boolean).map(file => file.replace(/\\/g, '/'));
    } catch (error) {
        const details = error as { stderr?: string; stdout?: string; message?: string };
        throw new PatchError(
            `Cannot list repository files:\n${(details.stderr || details.stdout || details.message || String(error)).trim()}`
        );
    }
}

function normalizedPathParts(filePath: string): string[] {
    const parts = filePath.replace(/\\/g, '/').split('/').filter(Boolean);
    return process.platform === 'win32' ? parts.map(part => part.toLowerCase()) : parts;
}

function commonSuffixScore(left: string[], right: string[]): number {
    let score = 0;
    while (score < left.length && score < right.length &&
        left[left.length - 1 - score] === right[right.length - 1 - score]) {
        score += 1;
    }
    return score;
}

function pathMatchScore(wantedPath: string, candidatePath: string): PathMatchScore {
    const wanted = normalizedPathParts(wantedPath);
    const candidate = normalizedPathParts(candidatePath);
    const lcs = new Array<number>(candidate.length + 1).fill(0);

    for (const wantedPart of wanted) {
        let diagonal = 0;
        for (let index = 1; index <= candidate.length; index += 1) {
            const above = lcs[index];
            lcs[index] = wantedPart === candidate[index - 1]
                ? diagonal + 1
                : Math.max(lcs[index], lcs[index - 1]);
            diagonal = above;
        }
    }

    const overlap = lcs[candidate.length];
    return {
        overlap,
        extra: candidate.length - overlap,
        suffix: commonSuffixScore(wanted, candidate)
    };
}

function comparePathScores(left: PathMatchScore, right: PathMatchScore): number {
    return left.overlap - right.overlap ||
        right.extra - left.extra ||
        left.suffix - right.suffix;
}

interface HunkVariant {
    oldLines: string[];
    newLines: string[];
    ignoredBodyIndexes: number[];
}

function sameLines(left: string[], right: string[]): boolean {
    return left.length === right.length && left.every((line, index) => line === right[index]);
}

function hunkVariant(hunk: Hunk, omitSyntheticPath: boolean): HunkVariant {
    const oldLines = [...hunk.oldLines];
    const newLines = [...hunk.newLines];
    const ignoredBodyIndexes: number[] = [];
    if (omitSyntheticPath &&
        hunk.syntheticPathBodyIndex !== undefined &&
        hunk.syntheticPathOldOrdinal !== undefined &&
        hunk.syntheticPathNewOrdinal !== undefined) {
        oldLines.splice(hunk.syntheticPathOldOrdinal, 1);
        newLines.splice(hunk.syntheticPathNewOrdinal, 1);
        ignoredBodyIndexes.push(hunk.syntheticPathBodyIndex);
    }
    return { oldLines, newLines, ignoredBodyIndexes };
}

function matchesAt(lines: string[], needle: string[], index: number, mode: MatchMode): boolean {
    if (index < 0 || index + needle.length > lines.length) {
        return false;
    }
    return needle.every((line, offset) =>
        normalizeMatchLine(lines[index + offset], mode) === normalizeMatchLine(line, mode)
    );
}

function validateLocatedOrder(located: LocatedHunk[], displayPath: string): void {
    let previousEnd = -1;
    for (let ordinal = 0; ordinal < located.length; ordinal += 1) {
        const match = located[ordinal];
        if (match.index < previousEnd) {
            throw new PatchError(`${displayPath}: hunk ${ordinal + 1} overlaps or is out of order.`);
        }
        previousEnd = match.index + match.oldLines.length;
    }
}

function locateAtDeclaredLines(
    filePatch: FilePatch,
    targetLines: string[],
    withoutInsertedFirstLine: boolean
): LocatedHunk[] | undefined {
    const syntheticHunk = filePatch.hunks.findIndex(hunk => hunk.syntheticPathBodyIndex !== undefined);
    let omitSyntheticPath = false;
    if (withoutInsertedFirstLine && syntheticHunk >= 0) {
        const header = filePatch.hunks[syntheticHunk];
        const syntheticFileLine = header.declaredOldStart !== undefined &&
            header.syntheticPathOldOrdinal !== undefined
            ? header.declaredOldStart + header.syntheticPathOldOrdinal
            : undefined;
        if (syntheticHunk !== 0 || syntheticFileLine !== 1) {
            return undefined;
        }
        omitSyntheticPath = true;
    }

    const located: LocatedHunk[] = [];
    let previousEnd = -1;
    for (let ordinal = 0; ordinal < filePatch.hunks.length; ordinal += 1) {
        const hunk = filePatch.hunks[ordinal];
        if (hunk.declaredOldStart === undefined || hunk.declaredOldStart <= 0) {
            return undefined;
        }
        const variant = hunkVariant(hunk, omitSyntheticPath);
        const insertedLineIsBeforeHunk = withoutInsertedFirstLine &&
            (syntheticHunk < 0 || ordinal > syntheticHunk);
        const matchIndex = hunk.declaredOldStart - 1 - (insertedLineIsBeforeHunk ? 1 : 0);
        if (matchIndex < previousEnd || !matchesAt(targetLines, variant.oldLines, matchIndex, 'exact')) {
            return undefined;
        }
        located.push({
            index: matchIndex,
            actualOldLines: targetLines.slice(matchIndex, matchIndex + variant.oldLines.length),
            ...variant
        });
        previousEnd = matchIndex + variant.oldLines.length;
    }
    return located;
}

function locateBySearch(
    filePatch: FilePatch,
    targetLines: string[],
    mode: MatchMode,
    displayPath: string,
    omitSyntheticPath: boolean
): LocatedHunk[] {
    const variants = filePatch.hunks.map(hunk => hunkVariant(hunk, omitSyntheticPath));
    const groups = new Map<string, number[]>();
    for (let ordinal = 0; ordinal < variants.length; ordinal += 1) {
        const key = JSON.stringify(variants[ordinal].oldLines.map(line => normalizeMatchLine(line, mode)));
        const members = groups.get(key) ?? [];
        members.push(ordinal);
        groups.set(key, members);
    }

    const located: Array<LocatedHunk | undefined> = new Array(filePatch.hunks.length);
    for (const ordinals of groups.values()) {
        const firstOrdinal = ordinals[0];
        const variant = variants[firstOrdinal];
        const matches = findAllSubsequences(targetLines, variant.oldLines, mode);
        if (matches.length === 0) {
            const qualifier = mode === 'indent' ? ' indentation-insensitive' : '';
            throw new PatchError(`${displayPath}: hunk ${firstOrdinal + 1} has no${qualifier} old-side match.`);
        }

        const replacements = ordinals.map(ordinal => variants[ordinal].newLines);
        const replacementsAreIdentical = replacements.every(lines => sameLines(lines, replacements[0]));
        if (!replacementsAreIdentical) {
            const locations = matches.slice(0, 10).map(index => index + 1).join(', ');
            throw new PatchError(
                `${displayPath}: the same old chunk matches at lines ${locations}, ` +
                'but its replacement text differs between hunks.'
            );
        }

        if (ordinals.length !== matches.length) {
            if (matches.length === 1 && ordinals.length === 1) {
                // The ordinary unique-match case.
            } else {
                const locations = matches.slice(0, 10).map(index => index + 1).join(', ');
                throw new PatchError(
                    `${displayPath}: the same old chunk matches ${matches.length} times ` +
                    `(lines ${locations}${matches.length > 10 ? ', …' : ''}), but the patch contains ` +
                    `${ordinals.length} identical replacement hunk${ordinals.length === 1 ? '' : 's'}.`
                );
            }
        }

        for (let index = 1; index < matches.length; index += 1) {
            if (matches[index] < matches[index - 1] + variant.oldLines.length) {
                throw new PatchError(`${displayPath}: repeated matches overlap, so replacing all of them is unsafe.`);
            }
        }

        for (let index = 0; index < ordinals.length; index += 1) {
            const ordinal = ordinals[index];
            const matchIndex = matches[index];
            const ownVariant = variants[ordinal];
            located[ordinal] = {
                index: matchIndex,
                actualOldLines: targetLines.slice(matchIndex, matchIndex + ownVariant.oldLines.length),
                ...ownVariant
            };
        }
    }

    const complete = located.map((match, ordinal) => {
        if (!match) {
            throw new PatchError(`Internal error: hunk ${ordinal + 1} was not located.`);
        }
        return match;
    });
    validateLocatedOrder(complete, displayPath);
    return complete;
}

function locateHunksInText(filePatch: FilePatch, targetText: string, mode: MatchMode, displayPath: string):
LocatedHunk[] {
    const targetLines = splitLinesKeepEnds(normalizeNewlines(targetText));

    // 1. Exact old-side text at every line declared by the patch wins immediately.
    const exact = locateAtDeclaredLines(filePatch, targetLines, false);
    if (exact) {
        return exact;
    }

    // 2. Retry as the same patch against the original file before Automato prepended
    // its one-line AI path header. If the header is present in hunk context, omit it;
    // otherwise every declared hunk position is simply one line too large.
    const headerAdjusted = locateAtDeclaredLines(filePatch, targetLines, true);
    if (headerAdjusted) {
        return headerAdjusted;
    }

    // 3. Search by content. A repeated chunk is safe only when the patch contains one
    // hunk per occurrence and every hunk has exactly the same replacement text.
    let fullError: PatchError | undefined;
    try {
        return locateBySearch(filePatch, targetLines, mode, displayPath, false);
    } catch (error) {
        if (!(error instanceof PatchError)) {
            throw error;
        }
        fullError = error;
    }

    if (filePatch.hunks.some(hunk => hunk.syntheticPathBodyIndex !== undefined)) {
        return locateBySearch(filePatch, targetLines, mode, displayPath, true);
    }
    throw fullError;
}

async function candidateText(
    repoRoot: string,
    relativePath: string,
    editorContents?: ReadonlyMap<string, string>
): Promise<{ text: string; usedEditorBuffer: boolean } | undefined> {
    const editorText = mapLookup(editorContents, relativePath);
    if (editorText !== undefined) {
        return { text: editorText, usedEditorBuffer: true };
    }
    try {
        return { text: await fs.readFile(secureTarget(repoRoot, relativePath), 'utf8'), usedEditorBuffer: false };
    } catch (error) {
        if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
            return undefined;
        }
        throw new PatchError(`Cannot read ${relativePath} as UTF-8 text: ${String(error)}`);
    }
}

async function locateFilePatch(
    repoRoot: string,
    filePatch: FilePatch,
    configuredMode: WhitespaceSetting,
    editorContents?: ReadonlyMap<string, string>,
    repositoryFiles?: string[]
): Promise<boolean> {
    if (filePatch.kind === 'add') {
        const target = secureTarget(repoRoot, filePatch.path);
        try {
            await fs.access(target);
            throw new PatchError(`Cannot create ${filePatch.path}: the file already exists.`);
        } catch (error) {
            if (error instanceof PatchError) {
                throw error;
            }
            const code = (error as NodeJS.ErrnoException).code;
            if (code !== 'ENOENT') {
                throw error;
            }
        }
        if (filePatch.hunks.length !== 1) {
            throw new PatchError(`New-file patch for ${filePatch.path} must contain exactly one hunk.`);
        }
        filePatch.mode = modeForPath(filePatch.path, configuredMode);
        filePatch.lineEnding = await repositoryLineEnding(repoRoot, filePatch.path);
        filePatch.hunks[0].matchIndex = 0;
        filePatch.hunks[0].actualOldLines = [];
        return false;
    }

    const wantedPath = filePatch.path.replace(/\\/g, '/');
    const wantedBasename = path.posix.basename(wantedPath);
    const allFiles = repositoryFiles ?? await listRepositoryFiles(repoRoot);
    const candidates = new Set<string>();
    for (const file of allFiles) {
        const sameName = process.platform === 'win32'
            ? path.posix.basename(file).toLowerCase() === wantedBasename.toLowerCase()
            : path.posix.basename(file) === wantedBasename;
        if (sameName) {
            candidates.add(file);
        }
    }
    const viable: LocatedCandidate[] = [];
    let exactFailure: PatchError | undefined;
    for (const candidate of candidates) {
        const source = await candidateText(repoRoot, candidate, editorContents);
        if (!source) {
            continue;
        }
        const mode = modeForPath(candidate, configuredMode);
        try {
            viable.push({
                path: candidate,
                mode,
                matches: locateHunksInText(filePatch, source.text, mode, candidate),
                usedEditorBuffer: source.usedEditorBuffer,
                lineEnding: detectLineEnding(source.text),
                pathScore: pathMatchScore(wantedPath, candidate)
            });
        } catch (error) {
            if (candidate === wantedPath && error instanceof PatchError) {
                exactFailure = error;
            }
        }
    }

    if (viable.length === 0) {
        if (exactFailure) {
            throw exactFailure;
        }
        const alternatives = [...candidates].filter(candidate => candidate !== wantedPath).slice(0, 8);
        const tried = alternatives.length > 0 ? ` Candidates with that basename: ${alternatives.join(', ')}.` : '';
        throw new PatchError(`No project file matching ${wantedPath} contains all patch hunks uniquely.${tried}`);
    }

    let bestScore = viable[0].pathScore;
    for (const candidate of viable.slice(1)) {
        if (comparePathScores(candidate.pathScore, bestScore) > 0) {
            bestScore = candidate.pathScore;
        }
    }
    const best = viable.filter(candidate => comparePathScores(candidate.pathScore, bestScore) === 0);
    if (best.length !== 1) {
        throw new PatchError(
            `Patch path ${wantedPath} is ambiguous. These equally good files all match the hunks: ` +
            `${best.map(candidate => candidate.path).join(', ')}.`
        );
    }

    const chosen = best[0];
    filePatch.path = chosen.path;
    filePatch.mode = chosen.mode;
    filePatch.lineEnding = chosen.lineEnding ?? await repositoryLineEnding(repoRoot, chosen.path);
    if (filePatch.kind === 'modify') {
        filePatch.oldPath = chosen.path;
        filePatch.newPath = chosen.path;
    } else {
        filePatch.oldPath = chosen.path;
        filePatch.newPath = undefined;
    }
    for (let index = 0; index < filePatch.hunks.length; index += 1) {
        const hunk = filePatch.hunks[index];
        const match = chosen.matches[index];
        hunk.matchIndex = match.index;
        hunk.actualOldLines = match.actualOldLines;
        hunk.oldLines = match.oldLines;
        hunk.newLines = match.newLines;
        hunk.oldCount = match.oldLines.length;
        hunk.newCount = match.newLines.length;
        hunk.ignoredBodyIndexes = match.ignoredBodyIndexes;
    }
    return chosen.usedEditorBuffer;
}

function rewritePatch(lines: string[], files: FilePatch[]): string {
    const corrected = [...lines];
    for (const filePatch of files) {
        const newlineFor = (index: number): string => lines[index].endsWith('\n') ? '\n' : '';
        if (filePatch.diffHeaderIndex !== undefined) {
            corrected[filePatch.diffHeaderIndex] =
                `diff --git ${encodeGitPath(filePatch.path, 'a/')} ${encodeGitPath(filePatch.path, 'b/')}` +
                newlineFor(filePatch.diffHeaderIndex);
        }
        corrected[filePatch.oldPathRecordIndex] = filePatch.kind === 'add'
            ? `--- /dev/null${newlineFor(filePatch.oldPathRecordIndex)}`
            : `--- ${encodeGitPath(filePatch.path, 'a/')}${newlineFor(filePatch.oldPathRecordIndex)}`;
        corrected[filePatch.newPathRecordIndex] = filePatch.kind === 'delete'
            ? `+++ /dev/null${newlineFor(filePatch.newPathRecordIndex)}`
            : `+++ ${encodeGitPath(filePatch.path, 'b/')}${newlineFor(filePatch.newPathRecordIndex)}`;

        let cumulativeDelta = 0;
        for (const hunk of filePatch.hunks) {
            const actualOldLines = hunk.actualOldLines;
            if (!actualOldLines) {
                throw new PatchError('Internal error: hunk was not located.');
            }

            let oldStart: number;
            let newStart: number;
            if (filePatch.kind === 'add') {
                oldStart = 0;
                newStart = 1;
            } else {
                oldStart = hunk.matchIndex + 1;
                const logicalNewStart = oldStart + cumulativeDelta;
                newStart = hunk.newCount === 0 ? Math.max(0, logicalNewStart - 1) : logicalNewStart;
            }

            const newline = lines[hunk.headerIndex].endsWith('\n') ? '\n' : '';
            corrected[hunk.headerIndex] =
                `@@ -${oldStart},${hunk.oldCount} +${newStart},${hunk.newCount} @@${hunk.suffix}${newline}`;

            for (const ignoredIndex of hunk.ignoredBodyIndexes) {
                corrected[ignoredIndex] = '';
            }

            let oldCursor = 0;
            for (let bodyIndex = hunk.bodyStart; bodyIndex < hunk.bodyEnd; bodyIndex += 1) {
                if (hunk.ignoredBodyIndexes.includes(bodyIndex)) {
                    continue;
                }
                const line = lines[bodyIndex];
                if (!line) {
                    continue;
                }
                const prefix = line[0];
                if (prefix === ' ' || prefix === '-') {
                    const actual = actualOldLines[oldCursor];
                    const hasLogicalNewline = actual.endsWith('\n');
                    const content = hasLogicalNewline ? actual.slice(0, -1) : actual;
                    corrected[bodyIndex] = prefix + content + (hasLogicalNewline ? filePatch.lineEnding : '\n');
                    oldCursor += 1;
                } else if (prefix === '+') {
                    const hasNoNewlineMarker = lines[bodyIndex + 1]?.startsWith('\\ No newline at end of file') ?? false;
                    const content = line.endsWith('\n') ? line.slice(0, -1) : line;
                    corrected[bodyIndex] = content + (hasNoNewlineMarker ? '\n' : filePatch.lineEnding);
                }
            }
            cumulativeDelta += hunk.newCount - hunk.oldCount;
        }
    }
    return corrected.join('');
}

export async function preparePatch(
    patchText: string,
    repoRoot: string,
    whitespaceSetting: WhitespaceSetting,
    editorContents?: ReadonlyMap<string, string>
): Promise<PreparedPatch> {
    const parsed = parseGitPatch(patchText);
    const repositoryRoot = await repositoryTopLevel(repoRoot);
    const usedEditorBuffers: string[] = [];
    const repositoryFiles = await listRepositoryFiles(repositoryRoot);
    for (const filePatch of parsed.files) {
        const usedEditor = await locateFilePatch(
            repositoryRoot,
            filePatch,
            whitespaceSetting,
            editorContents,
            repositoryFiles
        );
        if (usedEditor && filePatch.kind !== 'add') {
            usedEditorBuffers.push(filePatch.path);
        }
    }
    return {
        repoRoot: repositoryRoot,
        sourceText: patchText,
        correctedText: rewritePatch(parsed.lines, parsed.files),
        files: parsed.files,
        hunkCount: parsed.files.reduce((sum, file) => sum + file.hunks.length, 0),
        usedEditorBuffers
    };
}

async function writeTemporaryPatch(text: string): Promise<string> {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'automato-patch-'));
    const patchPath = path.join(directory, 'repaired.patch');
    await fs.writeFile(patchPath, text, { encoding: 'utf8' });
    return patchPath;
}

async function removeTemporaryPatch(patchPath: string): Promise<void> {
    await fs.rm(path.dirname(patchPath), { recursive: true, force: true });
}

export async function checkPreparedPatch(prepared: PreparedPatch, stageChanges: boolean): Promise<void> {
    const patchPath = await writeTemporaryPatch(prepared.correctedText);
    try {
        const args = ['-C', prepared.repoRoot, 'apply', '--unidiff-zero'];
        if (stageChanges) {
            args.push('--index');
        }
        args.push('--check', patchPath);
        await execFileAsync('git', args, { windowsHide: true, maxBuffer: 16 * 1024 * 1024 });
    } catch (error) {
        const details = error as { stderr?: string; stdout?: string; message?: string };
        throw new PatchError(
            `git apply --check rejected the repaired patch:\n${(details.stderr || details.stdout || details.message || String(error)).trim()}`
        );
    } finally {
        await removeTemporaryPatch(patchPath);
    }
}

export async function applyPreparedPatch(prepared: PreparedPatch, stageChanges: boolean): Promise<void> {
    const patchPath = await writeTemporaryPatch(prepared.correctedText);
    try {
        const base = ['-C', prepared.repoRoot, 'apply', '--unidiff-zero', '--whitespace=nowarn'];
        if (stageChanges) {
            base.push('--index');
        }
        await execFileAsync('git', [...base, '--check', patchPath], {
            windowsHide: true,
            maxBuffer: 16 * 1024 * 1024
        });
        await execFileAsync('git', [...base, patchPath], {
            windowsHide: true,
            maxBuffer: 16 * 1024 * 1024
        });
        if (stageChanges) {
            const paths = [...new Set(prepared.files.map(file => file.path))];
            await execFileAsync('git', ['-C', prepared.repoRoot, 'add', '--all', '--', ...paths], {
                windowsHide: true,
                maxBuffer: 16 * 1024 * 1024
            });
        }
    } catch (error) {
        const details = error as { stderr?: string; stdout?: string; message?: string };
        throw new PatchError(
            `git apply failed:\n${(details.stderr || details.stdout || details.message || String(error)).trim()}`
        );
    } finally {
        await removeTemporaryPatch(patchPath);
    }
}

export function summarizeFiles(files: FilePatch[]): string {
    return files.map(file => {
        const verb = file.kind === 'add' ? 'Add' : file.kind === 'delete' ? 'Delete' : 'Modify';
        const resolved = file.path !== file.originalPath ? ` (resolved from ${file.originalPath})` : '';
        return `${verb}: ${file.path}${resolved}`;
    }).join('\n');
}
