import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';

export interface CommentSyntax {
    line?: string;
    block?: [string, string];
}

function stripJsonComments(input: string): string {
    let output = '';
    let inString = false;
    let escaped = false;
    let lineComment = false;
    let blockComment = false;

    for (let index = 0; index < input.length; index += 1) {
        const char = input[index];
        const next = input[index + 1];

        if (lineComment) {
            if (char === '\n') {
                lineComment = false;
                output += '\n';
            } else {
                output += ' ';
            }
            continue;
        }
        if (blockComment) {
            if (char === '*' && next === '/') {
                output += '  ';
                index += 1;
                blockComment = false;
            } else {
                output += char === '\n' ? '\n' : ' ';
            }
            continue;
        }
        if (inString) {
            output += char;
            if (escaped) {
                escaped = false;
            } else if (char === '\\') {
                escaped = true;
            } else if (char === '"') {
                inString = false;
            }
            continue;
        }
        if (char === '"') {
            inString = true;
            output += char;
            continue;
        }
        if (char === '/' && next === '/') {
            output += '  ';
            index += 1;
            lineComment = true;
            continue;
        }
        if (char === '/' && next === '*') {
            output += '  ';
            index += 1;
            blockComment = true;
            continue;
        }
        output += char;
    }
    return output;
}

function removeTrailingCommas(input: string): string {
    let output = '';
    let inString = false;
    let escaped = false;
    for (let index = 0; index < input.length; index += 1) {
        const char = input[index];
        if (inString) {
            output += char;
            if (escaped) {
                escaped = false;
            } else if (char === '\\') {
                escaped = true;
            } else if (char === '"') {
                inString = false;
            }
            continue;
        }
        if (char === '"') {
            inString = true;
            output += char;
            continue;
        }
        if (char === ',') {
            let lookahead = index + 1;
            while (lookahead < input.length && /\s/.test(input[lookahead])) {
                lookahead += 1;
            }
            if (input[lookahead] === '}' || input[lookahead] === ']') {
                continue;
            }
        }
        output += char;
    }
    return output;
}

function parseJsonc(input: string): unknown {
    return JSON.parse(removeTrailingCommas(stripJsonComments(input)));
}

export async function findCommentSyntax(languageId: string): Promise<CommentSyntax | undefined> {
    for (const extension of vscode.extensions.all) {
        const languages = extension.packageJSON?.contributes?.languages as Array<{
            id?: string;
            configuration?: string;
        }> | undefined;
        if (!Array.isArray(languages)) {
            continue;
        }
        for (const language of languages) {
            if (language.id !== languageId || typeof language.configuration !== 'string') {
                continue;
            }
            try {
                const configPath = path.resolve(extension.extensionPath, language.configuration);
                const parsed = parseJsonc(await fs.readFile(configPath, 'utf8')) as {
                    comments?: { lineComment?: string; blockComment?: [string, string] };
                };
                const comments = parsed.comments;
                if (comments?.lineComment || comments?.blockComment) {
                    return {
                        line: comments.lineComment,
                        block: comments.blockComment
                    };
                }
            } catch {
                // Another extension may contribute the same language configuration.
            }
        }
    }
    return undefined;
}

export async function makePathHeader(languageId: string, relativePath: string): Promise<{
    header?: string;
    usedComment: boolean;
}> {
    const text = `FILE PATH: ${relativePath}`;
    const syntax = await findCommentSyntax(languageId);
    if (syntax?.line) {
        return { header: `${syntax.line} ${text}`, usedComment: true };
    }
    if (syntax?.block) {
        return { header: `${syntax.block[0]} ${text} ${syntax.block[1]}`, usedComment: true };
    }
    return { usedComment: false };
}
