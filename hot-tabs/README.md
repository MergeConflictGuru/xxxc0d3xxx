# Hot Tabs

Hot Tabs reorders the clean text-file tabs in the active VS Code editor group by recent Git edit activity, then pins the hottest tabs.

Default behavior:

- Runs automatically once after VS Code startup.
- Runs automatically every 120 minutes after that.
- Can also be run manually from the Command Palette.
- Looks at the last 90 days of Git history.
- Scores files by Git line churn: additions + deletions from `git log --numstat`.
- Reorders only the active editor group.
- Does nothing when the active group is already in the desired order with the desired pinned tabs.
- Updates pin state without closing tabs when only pinning is out of date.
- Pins the top 5 tabs.
- Skips dirty tabs so VS Code does not prompt or risk editor state.
- Skips non-text tabs such as terminals, webviews, and notebooks.
- Automatic runs are silent and only write to the `Hot Tabs` output channel.

## Run manually

1. Open a Git-backed workspace in VS Code.
2. Open some files as tabs.
3. Run `Hot Tabs: Reorder and Pin by Git Edit History` from the Command Palette.

## Settings

```json
{
  "hotTabs.days": 90,
  "hotTabs.pinCount": 5,
  "hotTabs.includeZeroScoreTabs": true,
  "hotTabs.runOnStartup": true,
  "hotTabs.automaticRunIntervalMinutes": 120
}
```

Set the interval to `0` to disable recurring automatic runs:

```json
{
  "hotTabs.runOnStartup": true,
  "hotTabs.automaticRunIntervalMinutes": 0
}
```

For manual-only behavior:

```json
{
  "hotTabs.runOnStartup": false,
  "hotTabs.automaticRunIntervalMinutes": 0
}
```

## Development

```bash
npm install
npm run compile
```

Then press `F5` in VS Code to launch an Extension Development Host.

## Notes

VS Code exposes tab inspection and closing through the public `window.tabGroups` API, but it does not provide a simple public API for arbitrarily moving tabs. The extension avoids closing anything when the current order already matches the desired order. When a reorder is actually needed, it uses the close/reopen workaround, then pins the configured top N with built-in editor commands.
