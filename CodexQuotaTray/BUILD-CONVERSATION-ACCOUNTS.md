# Conversation Accounts: install and recreate

## Users: run one EXE

Run `release/ConversationAccounts.exe`. This is the complete self-contained release;
it contains the routing bridge, desktop file/drop helper, hidden launch helper,
private Python runtime and dependencies, and required .NET runtimes.
No build tools, Python installation, .NET installation, or terminal commands are
needed on the destination PC.

If the environment is missing, a setup dialog asks for:

1. The environment folder (default: the current user's Local AppData).
2. The installed Codex desktop to base it on. Registered Store packages are detected;
   Browse also accepts a desktop installation containing `resources/codex.exe`.
3. The Codex home containing your sign-in and chat history (default: your `.codex`).

Install normal Codex and sign in once first. A completely new machine has no
account credentials or local history to recover. On first setup, valid saved
`auth.json` and `auth.json.*` logins from the selected home are imported into private
profiles. Recreate preserves existing profiles and does not rediscover previously
removed accounts. Source credentials and the original Codex app are not changed.

The release EXE remembers the chosen destination. It also places a copy of itself
inside the environment, so either copy can be used afterward. If the environment
is deleted, running the retained release EXE opens setup again. Keep that EXE
outside the environment if you want to be able to recreate it after deleting the
whole folder.

To open setup explicitly:

```powershell
.\ConversationAccounts.exe --setup
# --recreate is an alias for the same setup dialog
```

Recreate repairs bundled app files and preserves account profiles, routes, chat
history, desktop state, and other data files. Existing configuration is backed up
under `.app_tmp/setup-backups`. Close the running MU desktop and stop its persistent
server before replacing files used by a running installation; setup never kills
unrelated Codex processes or discards active chats. If a file is locked, close the
instance and retry the same setup.

## Normal operation

- No arguments: reuse/start the persistent server and open the desktop UI.
- `--server-only`: reuse/start the remote server without the UI.
- `--server-status`: report server health.
- `--stop`: stop the verified MU desktop window.
- `--stop-server`: explicitly stop the persistent server.

The desktop file/drop bridge starts with the desktop when no helper is already
available in the current session. Server-only mode does not require the helper.
Closing the UI leaves the persistent remote server running. No service, updater,
scheduled task, or Windows startup registration is installed.

The app uses the Windows identity that started it. Account names and development
machine paths are not hardcoded. For a deliberately separate Windows identity,
InvisibleLauncher remains an optional outer launcher; its saved-user behavior is
not required for an ordinary single-Windows-user setup.

## Developers only: build the distributable from source

The canonical source is `src/ConversationAccounts`, not the removed
`bin/multi-account-src`. Building a release requires Windows, .NET 10 SDK, 64-bit
Python 3.12, and websockets 15.0.1 in the build interpreter:

```powershell
python src/ConversationAccounts/build_release.py
```

This publishes self-contained helpers, assembles the private Python runtime,
deduplicates payload files by SHA-256, and embeds the archive into the self-contained
single-file launcher. Output: `release/ConversationAccounts.exe`.
The developer needs build tools; people installing that resulting EXE do not.
Installed Codex binaries and real login files are never bundled.

Payload extraction verifies file hashes and rejects paths outside the chosen
folder. The environment ACL is restricted to its installing Windows user and
SYSTEM because it contains login profiles and local control capabilities.

Clean setup/recreate regression check (uses only fake accounts and a fake desktop,
and never starts a model or remote receiver):

```powershell
.elease\ConversationAccounts.exe --self-test-setup <new-empty-test-path>
```

The test verifies bundled Python imports, the self-contained stdio bridge, clean
configuration creation, repaired files, and preservation of existing account and
history data. It writes `result.json` in the test directory.
