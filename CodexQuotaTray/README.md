# Codex Quota Tray

Launch settings use a full command in one box: quote the executable path when it
contains spaces, then append its arguments. Existing split `CliPath` and
`CliArguments` settings migrate automatically to `CliCommand`, preserving nested
quoted commands such as `runas`. The desktop launch command remains independent.

Shift+left-click on the tray icon runs `AlternateLaunchCommand`; configure it from
the tray menu's **Configure alternate launch…** item. If unconfigured, Shift-click
opens that configuration dialog. The alternate command is generic: it does not
switch accounts, stop Codex, or change the normal desktop or CLI launch commands.
For example, a PowerShell command can run CodexWeb's `start.ps1` with
`-RouterRoot <environment> -Background` to start/reuse the routed web server and
open its UI. Use `-NoBrowser` when only the server should start.

Native Windows app for saving Codex logins, reading each account's quota, and switching the desktop app from the system tray. Requires Windows and .NET 10 Desktop Runtime. Codex's bundled CLI and Microsoft Store desktop installation are detected automatically; custom installations can be selected in Settings.

Build and launch:

```powershell
dotnet build CodexQuotaTray/CodexQuotaTray.csproj -c Release
& .\CodexQuotaTray\bin\Release\net10.0-windows\CodexQuotaTray.exe
```

For a portable folder using an installed .NET Desktop Runtime:

```powershell
dotnet publish CodexQuotaTray/CodexQuotaTray.csproj -c Release --no-self-contained -o CodexQuotaTray/publish
```

To build and apply updates while the switcher is running, use `powershell -File CodexQuotaTray/update.ps1`. It builds in a separate staging folder, stops only this workspace's switcher instances, creates the destination directory explicitly, updates the published launch folder and deletes other copies named `CodexQuotaTray.exe`, and restarts the switcher through the Windows shell. It never restarts the Codex desktop.

Click the parchment-scroll tray icon to show a compact, borderless popup above the bottom-right taskbar area. The popup contains account rows and a timeline time scale, with no table labels or toolbar. Clicking away or pressing Escape hides it. The logged-in account has a rainbow gradient highlight. Middle-click the table to switch to the eight-day view. Select an account and press **Delete** to remove it from the tray and delete its saved backup; a persistent exclusion prevents discovery from adding it again. Removing the active login leaves the live `auth.json` in place. Use **Save current login** to explicitly add that login back. Right-click an account row to switch the saved login and run the configured Codex CLI command without relaunching the GUI. Right-click the tray icon for every other action. Use **Quit** to exit. Settings offers **Start with Windows**; startup opens in the tray. Launch with `--tray` for the same behavior.

Ctrl+Shift+Q opens the popup if Windows allows the shortcut to be registered; an existing binding is never replaced. Its availability is listed beside **Show accounts** in the tray menu. Use Up/Down to select a row and Enter to switch to that account. Actions requested during refresh are queued and run after the current account read finishes. Tab toggles the six-hour and eight-day timelines.

For keyboard-only CLI launch: press **Ctrl+Shift+Q**, choose an account with **Up/Down**, then press the **Menu/context-menu key** or **Shift+F10**. This performs the same action as right-clicking that account: switches its login and opens the configured CLI command. **Enter** switches and opens the desktop GUI; **Escape** closes the popup. **Win+Q** is Windows Search, not the tray shortcut.

- **%5h / %wk**: percentage remaining, matched by 300-minute and 10,080-minute windows. Unknown values show a dash. Hover for the last successful read, stale status, and any error.
- **#r**: available earned reset credits, as reported by Codex. The app only reads this count; it does not redeem resets.
- **Ordering**: remaining quota percentage divided by minutes until reset, highest first. The six-hour view uses the 5h quota; the eight-day view uses weekly quota. Missing or passed resets go last. The active account keeps its rainbow highlight wherever it ranks. Ordering updates every 30 seconds, retaining keyboard selection.
- **Timeline**: vertical green and purple lines show the next five-hour and weekly reset times. The default view shows the next six hours with local times above thin hourly grid lines. The eight-day view has daily grid lines and finer six-hour divisions. Hover for exact local dates. Passed timestamps are marked pending refresh rather than inventing new quota values.
- **Save current login**: captures the logged-in account and writes `auth.json.<email>` beside the real `auth.json`. Startup and refresh also capture the current account. Log in to another account through Codex and refresh to add it. Existing email-suffixed backups are discovered automatically.
- **Double-click an account**: saves the current login, stops processes matching the configured Codex desktop executable (named `ChatGPT.exe` in the Store package), atomically replaces and verifies `auth.json`, then launches Codex and waits up to 15 seconds for its process to appear. If launching fails, it restores the previous login and attempts recovery. Save ongoing work before switching. The program never kills the desktop's process tree, which may include the switcher, and does not kill unrelated CLI sessions.
- **Right-click an account row**: stops the desktop if needed, switches the saved login, then launches only the configured CLI executable with the configured arguments and Codex home. It does not relaunch the GUI. The default executable is `<Codex home>\plugins\.plugin-appserver\codex.exe`; default arguments are `--no-daemon resume --all`. Both settings can be changed in Settings.
- **Kill Codex / Restart Codex**: explicit desktop process controls.

Account switching stops and restarts Codex without any pre-restart chat discovery or recovery waits. After startup, a background operation discovers chats interrupted or quota-failed within the last 30 minutes and continues them inside their original GUI conversations. Recovery never holds the switcher busy queue, so another kill/restart remains available. Completed, old, active, and superseded turns are skipped. Settings includes an enabled-by-default recovery checkbox.

Recovery uses the installed desktop app-tools named-pipe bridge, an internal interface that can change with desktop updates. It discovers a live bridge owned by ChatGPT.exe and calls list_threads, read_thread, and send_message_to_thread. RecoveryControllerThreadId identifies the existing chat where recovery was authorized, for app-tool attribution; it defaults to the original account-switcher chat. The listing covers pinned chats and the 50 most recent other chats. Failed recovery is logged independently and never rolls back a successful login switch. Dispatch attempts are saved before sending in chat-recovery-attempts.json so an ambiguous timeout cannot duplicate a continuation. A later quota failure produces a new turn and is eligible again. Old attempts expire after two days. When the switcher runs under a different Windows user from Codex home, app-tool calls run through a short-lived helper under the Codex-home user using runas /savecred, matching InvisibleLauncher. Credentials are not read or stored by recovery. The helper accepts only chat listing, inspection, and continuation calls. Recovery diagnostics are also written to chat-recovery.log beside the executable so they are accessible from either Windows account.

Run with --check-chat-recovery for read-only discovery; it writes chat-recovery-diagnostic.json beside the executable without switching accounts or sending messages.

Quota makes one read-only HTTPS GET to `https://chatgpt.com/backend-api/wham/usage` using the saved access token and account ID. This is the endpoint used by [Codex's own usage client](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client/rate_limit_resets.rs); it is an internal endpoint and its schema may change. Quota reads start no CLI or app-server processes, initialize no plugins, create no temporary folders, and never refresh or rewrite authentication. Expired access tokens leave quota cached until Codex renews that account's login. Redirects are disabled. Full authentication backups remain available for switching. Accounts sharing an email but using different workspace IDs get separate backups.

A file watcher captures changes to the live `auth.json` after a 200 ms debounce, updates the active row, and preserves refreshed credentials in the correct email-suffixed backup. Store-installed desktop paths are rediscovered before stopping or starting the desktop, so Codex updates cannot leave a stale version path in process controls. Unexpected errors are recorded, without tokens, in `%LOCALAPPDATA%\CodexQuotaTray\operations.log`.

Backups contain the same sensitive credentials as `auth.json`; keep the Codex home private. Metadata and quota cache are stored at `%LOCALAPPDATA%\CodexQuotaTray\settings.json`, without tokens. This app requires file-backed ChatGPT authentication; API-key-only or OS credential-store-only logins do not have these account backups. An expired/revoked account must be signed in again in Codex. Existing Codex CLI sessions may keep their original account until restarted.

Verification:

```powershell
& .\CodexQuotaTray\publish\CodexQuotaTray.exe --self-test
Get-Content .\CodexQuotaTray\publish\test-results.txt
& .\CodexQuotaTray\publish\CodexQuotaTray.exe --demo
```

Self-tests use fake credentials in a temporary directory and a fake JSON-RPC subprocess. Demo mode displays sample accounts and disables credential/process mutations. No live account switching is performed by tests.

The desktop executable setting accepts `publish\InvisibleLauncher.exe <WindowsUsername>`. With no `--exe`, it launches the normal installed Codex/ChatGPT application as that Windows user. `--exe <path>` explicitly selects another executable. InvisibleLauncher does not discover ConversationAccounts or start dictation. ConversationAccounts.exe owns its backend, configured web UI, and dictation startup. Console helpers stay hidden and GUI executables use a normal window. Launcher diagnostics are in `InvisibleLauncher.error.log` and `InvisibleLauncher.child.log`.

Automatic settings and quota-cache saves are best effort: an unwritable shared `data` folder shows a status warning while account switching, quota reads, and launching continue. Authentication backup/write/identity failures still abort switching. Explicit Settings and account-removal saves still report failures. `update.ps1` keeps only `publish\CodexQuotaTray.exe`; build staging lives in `.agents_tmp` and is removed after publishing.

InvisibleLauncher handoff files live in the invoking user's `%LOCALAPPDATA%\InvisibleLauncher\.app_tmp\relay`, with a unique protected directory granting access only to that user, the requested target user, and SYSTEM. Launching no longer requires write access to the executable directory. Windows `runas /savecred` credentials are per invoking Windows user; credentials saved by Jakub are not available to Lord Midious. If a first launch needs authentication, run `runas /savecred /user:aiaiai "cmd /c exit"` once in a visible terminal under that invoking user, then retry the launcher.
