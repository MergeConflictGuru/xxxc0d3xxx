# Account and quota ownership

Conversation Accounts owns the multi-account profiles in `bridge/router-config.json` and the `auth.json` inside each configured profile home. The launcher validates those files and updates profile labels; it does not read Codex Quota Tray settings or its backup files. Profile credentials are refreshed by each account's Codex app-server when needed.

The account router reads quota directly from each profile's saved access token using the read-only `https://chatgpt.com/backend-api/wham/usage` request. It refreshes profiles every two minutes and before Auto selection when its cache is stale. This continues while Codex Quota Tray is closed. An expired token leaves the last successful quota visible and reports that the profile needs a sign-in refresh.

`sharedStore` remains the configured Codex conversation store. It lets the isolated account backends share conversation history and search data; it does not make quota or profile login state depend on the tray.

For physical file drops, DesktopFileBridge compares the Explorer source process SID with the Codex GUI process SID. Matching identities use Windows' native drag-and-drop path. Different identities keep the existing desktop-helper overlay and controlled CDP handoff. If Windows denies token inspection, the bridge keeps the cross-user fallback rather than accidentally intercepting a native drop.
