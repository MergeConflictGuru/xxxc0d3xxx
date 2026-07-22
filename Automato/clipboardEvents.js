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
exports.ClipboardChangeEvents = void 0;
const node_child_process_1 = require("node:child_process");
const path = __importStar(require("node:path"));
/**
 * Uses WM_CLIPBOARDUPDATE on Windows so identical clipboard text copied twice is
 * still observed. The extension keeps a polling watchdog as a fallback.
 */
class ClipboardChangeEvents {
    context;
    onChange;
    log;
    child;
    restartTimer;
    debounceTimer;
    active = false;
    stdoutBuffer = '';
    constructor(context, onChange, log) {
        this.context = context;
        this.onChange = onChange;
        this.log = log;
    }
    start() {
        if (this.active || process.platform !== 'win32') {
            return;
        }
        this.active = true;
        this.launch();
    }
    launch() {
        if (!this.active || this.child) {
            return;
        }
        const script = this.context.asAbsolutePath(path.join('scripts', 'watch-clipboard-changes.ps1'));
        const child = (0, node_child_process_1.spawn)('powershell.exe', [
            '-NoLogo', '-NoProfile', '-NonInteractive', '-STA',
            '-ExecutionPolicy', 'Bypass', '-File', script
        ], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
        this.child = child;
        this.stdoutBuffer = '';
        child.stdout?.setEncoding('utf8');
        child.stdout?.on('data', (chunk) => {
            this.stdoutBuffer += chunk;
            let newline;
            while ((newline = this.stdoutBuffer.indexOf('\n')) >= 0) {
                const line = this.stdoutBuffer.slice(0, newline).trim();
                this.stdoutBuffer = this.stdoutBuffer.slice(newline + 1);
                if (line === 'CHANGE') {
                    this.queueChange();
                }
            }
        });
        child.stderr?.setEncoding('utf8');
        child.stderr?.on('data', (chunk) => {
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
            this.log(`Windows clipboard listener exited (${signal ?? code ?? 'unknown'}); ` +
                'the polling watchdog remains active.');
            this.restartTimer = setTimeout(() => {
                this.restartTimer = undefined;
                this.launch();
            }, 1500);
        });
    }
    queueChange() {
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
    stop() {
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
    dispose() {
        this.stop();
    }
}
exports.ClipboardChangeEvents = ClipboardChangeEvents;
//# sourceMappingURL=clipboardEvents.js.map