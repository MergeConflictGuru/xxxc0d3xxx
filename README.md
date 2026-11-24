# Local VS Code extensions

[Nightcall](Nightcall/README.md) is the standalone Windows and Android voice relay, with a virtual microphone, shared dictation controls, and automatic day/night artwork.

[CodexWeb](CodexWeb/README.md) is a local browser client for Codex app-server with streaming chat, attachments, model controls, quota, and code diffs. Run `.\CodexWeb\start.ps1 -Background` to open it without a terminal window.

The standalone Windows tray app [CodexQuotaTray](CodexQuotaTray/README.md) displays per-account Codex quota and reset timelines, saves account logins, and switches the Codex desktop app between them.

This repository contains four independently packaged VS Code extensions:

- `Automato`
- `context-copy-search`
- `hot-tabs`
- `pickaxe` (`git-pickaxe-diff`)

Each extension keeps its own `package.json`, lockfile, dependencies, and VSIX output. Open this
repository root in VS Code; the root build tasks expose one package task per extension plus an
aggregate task.

## Commands

```powershell
npm run install:all
npm run test:all
npm run package:all
```

`package:all` produces one ignored `.vsix` file inside each extension directory. Individual
extensions can be built with `npm run package:<name>` from this repository root. In VS Code,
run `Tasks: Run Task` and choose an individual `VSIX:` task; the default build task packages all.
