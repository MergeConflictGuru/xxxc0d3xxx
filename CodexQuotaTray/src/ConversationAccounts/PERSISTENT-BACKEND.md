# Persistent multi-account backend

The modified launcher starts or reuses one independent native app-server. Its
short-lived ProtocolBridge clients attach over an authenticated loopback WebSocket.
UI EOF disconnects its client only; it does not close the server. Ordinary stock
launches have no CA_PERSISTENT_RECORD and retain their original behavior.

The backend owns phone enrollment, shared native chat sessions, account pins,
Auto selection, quota refresh, and inference forwarding. It reuses an existing
enabled enrollment's configured profile, or remoteReceiver if explicitly configured,
or the main profile as a fallback. It uses that profile's login for enrollment;
model requests still use per-turn Auto/pin selection. It does not depend on the tray.

## Launcher commands

    C:\_coding\ConversationAccounts\ConversationAccounts.exe
    C:\_coding\ConversationAccounts\ConversationAccounts.exe --server-only
    C:\_coding\ConversationAccounts\ConversationAccounts.exe --server-status
    C:\_coding\ConversationAccounts\ConversationAccounts.exe --stop-server

No arguments starts/reuses the server and opens the UI. --server-only starts/reuses
it without opening Electron. Status displays an informational dialog. Explicit stop
stops the backend and running work; ordinary window closure never does that.
The equivalent Python entry point is bridge/launch.py with the same switches.
--stop on launch.py stops only the verified desktop process, leaving the backend.

The first transition must close the legacy MU UI, which still owns its receiver.
The normal launcher performs that transition; --server-only refuses first startup
while the legacy UI is still active, avoiding duplicate enrollment connections.
Subsequent UI and server-only launches reuse the independent receiver.

## Lifecycle and security

- Authenticated initialize + config/read checks identify a reusable server.
- Separate startup/server locks serialize concurrent launches.
- Native WebSocket and inference endpoints bind only to loopback and require
  random capabilities; the local control API also requires its random capability.
- .app_tmp/persistent files are restricted to the current Windows user and SYSTEM.
- Windows job ownership cleans native children up when the supervisor exits.
- No Windows service, scheduled task, updater, or cross-account Windows relay is added.
- Closing UI stdin only closes its connection. UI remoteControl/disable requests
  read actual server status instead: shared server lifetime belongs to the launcher.
  Use --stop-server to stop it explicitly.
- Only the modified launch environment enables the relay. Installed app files and
  global environment settings are untouched.

Diagnostics are .app_tmp/persistent/supervisor.log and native.log. Do not publish
server.json or native-token: those contain local connection capabilities.

## Verified

21 routing/account/native transport tests passed. The native lifecycle test starts
an isolated detached supervisor with no real credentials or phone enrollment,
verifies authenticated reuse and concurrent access to the same native chat, closes
UI clients and the actual Python router/stdio bridge, confirms server survival,
and exercises authenticated explicit shutdown. No model turn or real phone
pairing is performed by that test. The launcher executable builds successfully.

Actual Electron attachment with a live phone, phone trust prompts, model execution,
and desktop-only tool behavior after Electron closes still need acceptance testing.
Native CLI tools remain available; Electron-provided IPC capabilities cannot be
assumed available while the UI is absent. If the backend exits unexpectedly, a
new launcher invocation re-creates it; this is not a Windows boot auto-start service.
