# Installed desktop launcher

ConversationAccounts.exe still invokes bridge/launch.py. Set desktopMode to
installed-runtime in bridge/router-config.json to use the installed OpenAI.Codex
Store package. The package executable is rediscovered from its manifest on every
launch. No executable, ASAR, or installed resource is patched or replaced.

Both startup stages use InvisibleLauncher.exe --hidden-powershell, built from the shared InvisibleLauncher source, to create console children without a console window. launch_installed.ps1 enters the package context. Windows drops its caller's
environment, so it transfers just the launch settings through a temporary file
with a current-user/SYSTEM-only ACL. The child reads and deletes this file before
starting the app. The control token never enters the renderer.

runtime_attach.py uses Python websockets (tested with 15.0.1) to attach to a random
loopback debugging port. It creates the selector API with a CDP binding, accepts
requests only from the app's default execution context, and forwards the three
allowed operations to the launch-specific authenticated router endpoint. It
reinjects on navigation and exits when that particular app process exits. The
existing desktop UI profile remains desktop-data/multi-account.

The child updates old copied-runtime tool paths in global/profile config.toml
files to the discovered installed path. First versions are backed up in
settings-backups/runtime-injection. Subsequent installed-version paths are tracked
in bridge/runtime-paths.json and updated at the next launch.

With retireCopiedDesktop=true, once the visible account selector and router
accounts are verified, the controller invokes retire_desktop_copy.ps1. That script
checks the exact package desktop-app directory, rejects a junction, requires
installed-runtime process metadata, and defers if any process still uses the old
copy. Current installation files are never a cleanup target.

Verified in a separate installed-app window: four router accounts, visible
selector, pointer hit testing, changing/restoring account selection, and selector
plus account state after reload. No inference message was sent. The existing
quota/history bugs are not claimed resolved by this launch change.

Source for the bridge changes is this directory. Copy launch.py,
launch_installed.ps1, launch_installed_child.ps1, runtime_attach.py,
retire_desktop_copy.ps1 and router.py into the package's bridge directory. Keep
the existing accounts-ui.js, ProtocolBridge, account config and data. Python needs
websockets installed. No desktop-app copy is needed for this mode.

## DevTools

The injector runs devtools_attach.py alongside account controls. The DevTools
button and Ctrl+Shift+I / F12 open a local Electron inspector target directly
through CDP Target.createTarget. No DesktopFileBridge or external browser is
required. The existing random loopback port is reused. Deploy devtools_attach.py,
devtools-ui.js and runtime_attach.py together. Controls are restored after reloads.
