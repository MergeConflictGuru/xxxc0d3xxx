import * as vscode from 'vscode';
import { execFile } from 'child_process';
import * as path from 'path';

/**
 * Executes a Git command safely using execFile to avoid shell-injection/escaping issues.
 */
function runGit(args: string[], cwd: string): Promise<string> {
    return new Promise((resolve, reject) => {
        execFile('git', args, { cwd, maxBuffer: 15 * 1024 * 1024 }, (error, stdout, stderr) => {
            if (error) {
                reject(stderr || error.message);
            } else {
                resolve(stdout);
            }
        });
    });
}

/**
 * Virtual Document Provider to render git file contents on-the-fly.
 * Eliminates the need for creating/deleting temporary files on the user's disk.
 */
class GitPickaxeContentProvider implements vscode.TextDocumentContentProvider {
    public static readonly scheme = 'git-pickaxe';

    provideTextDocumentContent(uri: vscode.Uri, token: vscode.CancellationToken): Thenable<string> {
        // Parse arguments out of the custom URI format
        // URI structure: git-pickaxe://<commit>/<filePath>?gitRoot=<gitRootBase64>
        const commit = uri.authority; 
        const filePath = uri.path.replace(/^\//, ''); // Strip leading slash
        
        const queryParams = new URLSearchParams(uri.query);
        const encodedGitRoot = queryParams.get('gitRoot');
        const gitRoot = encodedGitRoot ? Buffer.from(encodedGitRoot, 'base64').toString('utf8') : '';

        return new Promise((resolve) => {
            if (!gitRoot) {
                resolve('');
                return;
            }

            // Fetch the content of the file at the specific revision
            // If the revision is invalid (e.g. parent of root commit commit^), it returns empty
            runGit(['show', `${commit}:${filePath}`], gitRoot)
                .then(content => resolve(content))
                .catch(() => resolve('')); // Return empty on error (e.g. file did not exist in parent commit)
        });
    }
}

export function activate(context: vscode.ExtensionContext) {
    // Register the on-the-fly document provider
    const provider = new GitPickaxeContentProvider();
    context.subscriptions.push(
        vscode.workspace.registerTextDocumentContentProvider(GitPickaxeContentProvider.scheme, provider)
    );

    // Register our primary interactive command
    const searchCommand = vscode.commands.registerCommand('git-pickaxe-diff.search', async () => {
        await executePickaxeSearch();
    });

    context.subscriptions.push(searchCommand);
}

/**
 * Main command sequence: search, pick commit, pick file, open native VS Code diff.
 */
async function executePickaxeSearch() {
    // 1. Confirm workspace folder is open
    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (!workspaceFolders || workspaceFolders.length === 0) {
        vscode.window.showErrorMessage('No workspace folder open. Please open a Git repository.');
        return;
    }
    const workspaceRoot = workspaceFolders[0].uri.fsPath;

    // 2. Locate actual Git repository root (supporting sub-folders)
    let gitRoot: string;
    try {
        gitRoot = (await runGit(['rev-parse', '--show-toplevel'], workspaceRoot)).trim();
    } catch (e) {
        vscode.window.showErrorMessage('The current directory is not part of a Git repository.');
        return;
    }

    // 3. Prompt user for Pickaxe search string
    const search = await vscode.window.showInputBox({
        prompt: 'Search string in git history (Pickaxe -S)',
        placeHolder: 'Enter term, function name, API key, etc...',
        validateInput: (value) => value.trim().length === 0 ? 'Search query cannot be empty' : null
    });

    if (!search) {
        return; // User canceled
    }

    // 4. Fetch commits changing occurrence count of the searched string
    try {
        const logOutput = await runGit(
            ['log', '--all', `-S${search}`, '--pretty=format:%H\t%ad\t%s', '--date=short'], 
            gitRoot
        );

        if (!logOutput.trim()) {
            vscode.window.showInformationMessage(`No matching commits found for string: "${search}"`);
            return;
        }

        const lines = logOutput.trim().split('\n');
        const commitItems = lines.map(line => {
            const [hash, date, subject] = line.split('\t');
            return {
                label: subject,
                description: `${date} (${hash.substring(0, 8)})`,
                detail: hash,
                hash: hash
            };
        });

        // 5. Let the user choose a matching commit
        const selectedCommit = await vscode.window.showQuickPick(commitItems, {
            placeHolder: `Select a commit that contains: "${search}"`,
            matchOnDescription: true,
            matchOnDetail: true
        });

        if (!selectedCommit) {
            return; // User canceled
        }

        const commit = selectedCommit.hash;

        // 6. Fetch matching files inside the selected commit that match the pickaxe query
        const filesOutput = await runGit(
            ['diff-tree', '-r', '--no-commit-id', '--name-only', `-S${search}`, '--root', commit], 
            gitRoot
        );
        const files = filesOutput.trim().split('\n').filter(f => f.trim().length > 0);

        if (files.length === 0) {
            vscode.window.showWarningMessage('No matching files found inside this commit.');
            return;
        }

        // 7. Select file (auto-select if only 1 file matches)
        let selectedFile: string;
        if (files.length === 1) {
            selectedFile = files[0];
        } else {
            const fileItems = files.map(f => ({ label: f }));
            const selectedChoice = await vscode.window.showQuickPick(fileItems, {
                placeHolder: `Select file containing "${search}" changes`
            });
            if (!selectedChoice) {
                return; // User canceled
            }
            selectedFile = selectedChoice.label;
        }

        // 8. Open side-by-side diff using Virtual Documents
        const encodedGitRoot = Buffer.from(gitRoot).toString('base64');
        const leftUri = vscode.Uri.parse(
            `${GitPickaxeContentProvider.scheme}://${commit}^/${selectedFile}?gitRoot=${encodedGitRoot}`
        );
        const rightUri = vscode.Uri.parse(
            `${GitPickaxeContentProvider.scheme}://${commit}/${selectedFile}?gitRoot=${encodedGitRoot}`
        );
        
        // Formulate a clean tab label
        const fileName = path.basename(selectedFile);
        const shortHash = commit.substring(0, 8);
        const title = `${fileName} (${shortHash}^ ↔ ${shortHash})`;

        // Trigger native VS Code visual diff view
        await vscode.commands.executeCommand('vscode.diff', leftUri, rightUri, title);

    } catch (error: any) {
        vscode.window.showErrorMessage(`Git Pickaxe Command Failed: ${error}`);
    }
}

export function deactivate() {
    // Clean up if any global assets were registered (none required for current design)
}
