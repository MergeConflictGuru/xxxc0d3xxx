Run "Start desktop file bridge.lnk" from Explorer as your usual desktop Windows user (Jakub), then restart ConversationAccounts.

The helper stays in the tray. Account controls gain "Attach from desktop". Click it and drop files into the helper window, or click that window to choose files. The helper copies attachments into desktop-file-bridge/dropped with read permission for the other local authenticated user, then the account controller sends the staged files to Codex's existing file-drop handler. Originals are untouched. Staged files remain until you remove them; do not remove them while a message needs its attachments.

Right-clicking a local image uses "Open in Explorer" through the desktop-user helper. Supported sources are file: and app://fs/@fs/ image URLs and CSS background images, including the avatar overlay. Other native folder links are not replaced.

This is a companion drop window, not a bypass of Windows cross-user OLE dragging. The helper must run in the same desktop session as Codex, under the user owning Explorer. No passwords are stored. The IPC only exposes ping, reveal, and user-approved file picking/dropping.

Validation: Release build succeeds; Python compile and JS syntax pass; named-pipe ping/identity tested in a temporary instance. Cross-user Explorer reveal and a real file drop still need testing with the helper running as the desktop user.
