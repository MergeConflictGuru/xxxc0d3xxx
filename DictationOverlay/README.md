# Dictation Overlay

Run `publish/DictationOverlay.exe`. It stays in the notification area.

- **Ctrl + Alt + Space** starts recording. Press the same shortcut again to transcribe and paste into the original text field.
- The overlay shows microphone level history and elapsed time. Cancel discards the recording.
- **Escape** cancels recording or transcription and dismisses the overlay. It is registered only while the overlay is visible, so other applications keep their normal Escape behavior when dictation is idle.
- Right-click its tray icon and choose **Settings** to select a shortcut, an existing Codex `auth.json`, or an optional language hint. Leave language blank for automatic detection, including mixed Czech and English.
- If you change foreground windows during transcription, the transcript stays in the overlay. You can copy it or explicitly paste into the original window.
- Failed transcription retains audio in memory for Retry. Cancel or Exit discards it. Recording stops automatically after five minutes.

This is a separate Windows app. Neither the ChatGPT desktop UI nor the multi-account server needs to run. It uses the selected existing ChatGPT/Codex login, not API billing. Credentials remain in the selected login file and are read afresh per request. No credentials, recordings, or transcripts are logged or saved by this app. Audio is uploaded to OpenAI's service, whose retention rules still apply. No startup task, updater, or local network listener is installed.

The current transcription endpoint is internal to ChatGPT, not a supported public API, and can change. Expired credentials require signing in to Codex again. The tool does not refresh or overwrite your credentials. It uses Windows' built-in WinHTTP transport, with redirects disabled and a fixed HTTPS destination.

Clipboard contents are snapshotted before paste and restored after one second if no other app changed the clipboard. Standard editable text fields must support Ctrl+V. Password fields, elevated applications, games, and custom editors may reject synthetic paste; when Windows reports a failure the text remains available to copy. The app does not press Enter or send the message.

Settings are saved beside the executable (`settings.json`). The default login is the current Windows user's `.codex/auth.json`; paths and account names are not hardcoded. Run it as the desktop user whose applications should receive dictation, and select another accessible login file in Settings if necessary.

## Build and checks

Requires the .NET 10 Windows Desktop runtime (already installed on the development PC). Build with the .NET 10 SDK:

```powershell
dotnet build -c Release -p:PlatformTarget=x64 -o publish
```

Diagnostic commands do not record the microphone:

```powershell
publish/DictationOverlay.exe --transcribe-file path/to/sample.wav
publish/DictationOverlay.exe --render-preview preview.png
publish/DictationOverlay.exe --smoke-test
publish/DictationOverlay.exe --test-paste path/to/sample.wav
```

The last command opens its own temporary editor, transcribes the provided sample, pastes into that editor, reports whether the text matched, then exits. Normal operation records only after the shortcut is pressed.
