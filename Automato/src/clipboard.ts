import { execFile } from 'node:child_process';
import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import { promisify } from 'node:util';
import * as vscode from 'vscode';

const execFileAsync = promisify(execFile);

export async function setWindowsFileAndTextClipboard(
    context: vscode.ExtensionContext,
    copiedFilePath: string,
    text: string
): Promise<void> {
    if (process.platform !== 'win32') {
        await vscode.env.clipboard.writeText(text);
        return;
    }

    const tempDirectory = path.dirname(copiedFilePath);
    const textPath = path.join(tempDirectory, '.automato-clipboard-text.txt');
    await fs.writeFile(textPath, text, 'utf8');

    const scriptPath = context.asAbsolutePath(path.join('scripts', 'set-multiformat-clipboard.ps1'));
    try {
        await execFileAsync(
            'powershell.exe',
            [
                '-NoLogo', '-NoProfile', '-NonInteractive', '-STA',
                '-ExecutionPolicy', 'Bypass',
                '-File', scriptPath,
                '-FilePath', copiedFilePath,
                '-TextPath', textPath
            ],
            { windowsHide: true, maxBuffer: 2 * 1024 * 1024 }
        );
    } catch (error) {
        await vscode.env.clipboard.writeText(text);
        const details = error as { stderr?: string; message?: string };
        throw new Error(
            'The text fallback was copied, but Windows rejected the file clipboard representation: ' +
            (details.stderr || details.message || String(error)).trim()
        );
    }
}
