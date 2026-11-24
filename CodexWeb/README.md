# Codex Web

A local browser UI for Codex app-server. VS Code Dark colors, conversations on the left, streaming chat in the main panel, account quota in the sidebar, and code changes in an optional panel.

## Start

Requires Node.js 22+ and an installed, signed-in Codex CLI. Uses your existing Codex login; no API key or separate subscription is required by this UI. No npm dependencies to install.

```powershell
.\CodexWeb\start.ps1
```

The launcher opens the browser. Keep its terminal open. Ctrl+C stops the bridge and its own app-server; plain-server work started through this UI stops with it. Closing the browser tab does not stop a turn. Reopen the printed launch URL to reconnect. The default address is localhost port 4783. The launch URL contains a local access capability; keep it private.

To run without a terminal window, use `.\CodexWeb\start.ps1 -Background`. To stop that instance, run `.\CodexWeb\start.ps1 -Stop`. Starting again reopens an already-running instance on the selected port. Runtime connection records and logs are stored under `.app_tmp/codex-web`; these records contain the local access capability.

```powershell
.\CodexWeb\start.ps1 -Port 4784 -WorkingDirectory C:\_coding\music-tools
```

For a terminal-only launch, use `-NoBrowser`. You can also run `npm start` inside `CodexWeb` and open the URL printed in the terminal. `CODEX_WEB_BINARY` overrides the executable. `CODEX_WEB_COMMAND` accepts a JSON array for a compatible stdio app-server command. `CODEX_HOME` is inherited normally.

## Use

- Select a conversation to view history; load earlier messages and additional conversations as needed. Browsing uses read-only history APIs; a conversation is resumed only when you send a message. Search queries the app-server's stored conversation titles. The selected conversation stays open across page reloads.
- Send with Enter; use Shift+Enter for a new line. During a running turn, Send becomes Steer. Stop interrupts the selected turn. Model, reasoning effort, and speed selectors are populated from the server catalog; they apply when starting a turn.
- Attach files with +, drag and drop, or paste images. Maximum 20 MB per file. Images are sent as native local-image inputs. Other attachments are stored locally and their paths are provided to Codex so it can read them with file tools. The selected model must support image input. The agent's sandbox must permit reading the upload folder.
- Uploaded files are retained under the repository's `.app_tmp/codex-web/uploads` so older messages keep their attachments. Remove them manually when no longer needed; removal breaks old local references.
- Open Changes to inspect file changes and live unified diffs. Tool output and reasoning are collapsed by default. Approvals and questions appear above the conversation when Codex requests them.
- The sidebar shows actual account quota snapshots and reset times. Missing quota data is reported as unavailable. Percentages are remaining quota, not token/context use.
- Use the appearance button beside CODEX to choose VS Code Dark, terminal green, light, or a custom text color. Appearance stays in this browser.

## Existing multi-account server (optional)

Start the existing Conversation Accounts persistent backend using its launcher, then:

```powershell
.\CodexWeb\start.ps1 -RouterRoot C:\_coding\ConversationAccounts
```

This starts/reuses the existing persistent backend and attaches its authenticated stdio relay; it does not create another account router. The sidebar shows all router accounts and quota. Use Conversation account to choose Auto or pin a conversation to an account; switching is disabled during a turn. The bridge keeps control tokens server-side. Closing the web bridge disconnects its relay without stopping the shared backend.

The installed ConversationAccounts environment is configured with `webUiLauncher` pointing to this project's `start.ps1`. Normal ConversationAccounts startup silently starts/reuses the web server with `-Background -NoBrowser` alongside its desktop, on the same persistent router. It never opens a browser. InvisibleLauncher remains a generic launcher; the integration belongs to ConversationAccounts. The existing command with `--exe "C:\_coding\ConversationAccounts\ConversationAccounts.exe"` requires no extra arguments.

Without `-RouterRoot`, the UI starts its own app-server and reads conversations from its configured Codex home. It can continue saved chats but cannot subscribe to or steer turns owned by a separate desktop app-server process. Use the same persistent backend when multiple clients need to share live turns. Desktop-only tool integrations may be unavailable without the desktop UI.

## Verification

```powershell
npm --prefix CodexWeb test
```

The integration suite uses a fake app-server, never real credentials or model calls. Localhost binding, capability-cookie authentication, origin/host checks, API method restrictions, upload limits, opaque asset URLs, request forwarding, events, steering, and approvals are exercised. Protocol source: [official Codex App Server documentation](https://learn.chatgpt.com/docs/app-server); wire shapes were checked against schemas generated by the locally installed Codex binary.
