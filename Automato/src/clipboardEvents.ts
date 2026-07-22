import { ChildProcess, spawn } from 'node:child_process';
import * as path from 'node:path';
import * as vscode from 'vscode';

/**
 * Uses WM_CLIPBOARDUPDATE on Windows so identical clipboard text copied twice is
 * still observed. The extension keeps a polling watchdog as a fallback.
 */
export class ClipboardChangeEvents implements vscode.Disposable {
    private child: ChildProcess | undefined;
    private restartTimer: NodeJS.Timeout | undefined;
    private debounceTimer: NodeJS.Timeout | undefined;
    private active = false;
    private stdoutBuffer = '';

    public constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly onChange: () => void,
        private readonly log: (message: string) => void
    ) {}

    public start(): void {
        if (this.active || process.platform !== 'win32') {
            return;
        }
        this.active = true;
        this.launch();
    }

    private launch(): void {
        if (!this.active || this.child) {
            return;
        }

        const script = this.context.asAbsolutePath(
            path.join('scripts', 'watch-clipboard-changes.ps1')
        );
        const child = spawn(
            'powershell.exe',
            [
                '-NoLogo', '-NoProfile', '-NonInteractive', '-STA',
                '-ExecutionPolicy', 'Bypass', '-File', script
            ],
            { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] }
        );
        this.child = child;
        this.stdoutBuffer = '';

        child.stdout?.setEncoding('utf8');
        child.stdout?.on('data', (chunk: string) => {
            this.stdoutBuffer += chunk;
            let newline: number;
            while ((newline = this.stdoutBuffer.indexOf('\n')) >= 0) {
                const line = this.stdoutBuffer.slice(0, newline).trim();
                this.stdoutBuffer = this.stdoutBuffer.slice(newline + 1);
                if (line === 'CHANGE') {
                    this.queueChange();
                }
            }
        });

        child.stderr?.setEncoding('utf8');
        child.stderr?.on('data', (chunk: string) => {
            const message = chunk.trim();
            if (message) {
                this.log(`Windows clipboard listener: ${message}`);
            }
        });

        child.on('error', error => {
            this.log(`Windows clipboard listener could not start: ${error.message}`);
        });

        child.on('exit', (code, signal) => {
            if (this.child === child) {
                this.child = undefined;
            }
            if (!this.active) {
                return;
            }
            this.log(
                `Windows clipboard listener exited (${signal ?? code ?? 'unknown'}); ` +
                'the polling watchdog remains active.'
            );
            this.restartTimer = setTimeout(() => {
                this.restartTimer = undefined;
                this.launch();
            }, 1500);
        });
    }

    private queueChange(): void {
        if (this.debounceTimer) {
            clearTimeout(this.debounceTimer);
        }
        // A single copy operation can publish multiple formats in quick succession.
        this.debounceTimer = setTimeout(() => {
            this.debounceTimer = undefined;
            if (this.active) {
                this.onChange();
            }
        }, 75);
    }

    public stop(): void {
        this.active = false;
        if (this.restartTimer) {
            clearTimeout(this.restartTimer);
            this.restartTimer = undefined;
        }
        if (this.debounceTimer) {
            clearTimeout(this.debounceTimer);
            this.debounceTimer = undefined;
        }
        const child = this.child;
        this.child = undefined;
        child?.kill();
    }

    public dispose(): void {
        this.stop();
    }
}
