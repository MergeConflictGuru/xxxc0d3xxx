# Nightcall

## Day and night

Nightcall uses the ivory/pink/turquoise phone by day (07:00–18:59 local time) and the dark pink/cyan phone by night (19:00–06:59). Both apps use matching palettes, cards, buttons and artwork. The Windows tray and window/taskbar icons update while the app runs. The Explorer icon embedded in the executable is the daytime icon. Android switches between two launcher aliases without stopping microphone capture. Inexact background scheduling and launcher caches can delay the home-screen refresh; opening Nightcall or leaving its microphone service running also refreshes the appearance. No location or exact-alarm permission is requested.

The project, C# namespace, project file, executable, app label and download are now **Nightcall**. The Android package ID and pairing protocol remain compatible with the earlier test build, and saved PC pairings migrate into the Nightcall app-data folder without changing device identity. The old runtime remains in the sibling `DictationOverlay/publish` folder only while that version is still in use.

## Android 2.2: on-demand microphone

Opening the phone UI no longer starts audio capture. Pairing runs briefly without opening the microphone, then sleeps. **Start dictation** or the volume-button chord starts a session, reconnects, and sends the requested dictation command once. A session stops five minutes after the latest start/dictation command; idle means no AudioRecord, relay sockets, discovery thread or wake lock. **Keep microphone running** explicitly disables the timeout for calls and other Windows audio-input apps. Stopping the microphone clears that option.

The accessibility listener stays managed by Android independently of the relay. It explicitly registers key filtering, and the foreground Activity also handles any volume events that were not consumed by accessibility. The volume handler no longer ignores events when the microphone service is stopped. A cold background start briefly opens Nightcall's wake Activity before creating the microphone foreground service, following Android's while-in-use permission rules. Force-stop and OEM screen-off key policies still need device-specific verification. No diagnostic status UI was added.

A genuine PC-to-phone push service is **not implemented**. Fully stopped phones have no local listener to receive a LAN wake command. FCM would require a Firebase project and sender configuration, and Android can require notification interaction before microphone capture. While the phone is idle, PC dictation continues to use the Windows input fallback.

## Version 2: unified phone microphone and dictation

The Windows executable is **Nightcall.exe**. Phone audio, recording overlay, transcription, paste, pairing, and **Start with Windows** all live in that one app. The Android companion is **Nightcall.apk** (Android 8.0 or later).

The ready-to-test build is in `publish`. Exit the old tray app once, then run `publish/Nightcall.exe`. The build copies existing shortcut and login settings. Double-click the tray icon for the connection window.

1. Click **Install Android app** on the PC and scan that QR with the phone camera. Both devices must be on the same LAN. Download and open the APK; Android may ask you to allow installation from your browser.
2. Open the phone app and allow its microphone permission. On the PC, click **Pair a phone**, then use **Scan pairing QR** inside the phone app. The pairing QR is single-use and expires after two minutes.
3. The phone reconnects automatically from then on. No name, model, IP address, or port needs to be entered. Multiple phones can be paired; the first connection owns the microphone until it disconnects. Another phone cannot interrupt it.
4. Use the PC shortcut or the phone's **Start / finish dictation** control. Either side can finish a recording started by the other. If no phone is connected at the start, dictation uses the normal Windows microphone input, including an existing third-party virtual microphone. If the default is Nightcall's own output cable, it looks for another active input so it does not record its empty output.
5. Optional: enable the phone's accessibility shortcut to press both volume buttons together outside the app. In the foreground, no accessibility permission is required. The notification also has dictation and Stop controls. The microphone service continues with the screen off; locked-screen key delivery varies by device. Open the app again after a phone restart or force-stop.

If Android grays out accessibility for this sideloaded APK, open **Settings → Apps → Nightcall → ⋮ → Allow restricted settings**, confirm, then return to **Accessibility → Installed apps → Nightcall volume shortcut**. Nightcall's shortcut button provides App info and Accessibility links. This permission is optional for microphone streaming and on-screen dictation.

Android 2.1.1 removes the forced-landscape scanner, compensates for sensor/display rotation, and fits the preview without stretching. Five camera-geometry tests cover front/back rotations, portrait, landscape, square and tablet viewports. Pairing was confirmed on the user's phone; the corrected preview still needs a phone-side check.

The system-wide virtual microphone currently uses an **already installed VB-Audio Virtual Cable**. Audio goes only to that cable, never to speakers. Select **CABLE Output** as the microphone in other Windows apps. This build does not install or redistribute a driver and does not use AudioRelay's process. Without a cable, phone dictation still works. Recordings stay in memory. If the phone disconnects during dictation, the received portion is finished and transcribed; the next recording uses the available input. Phone mute applies to both outputs.

### Connection and pairing

The PC advertises its persistent ID over UDP 47832 every two seconds, including after Wi-Fi changes. The phone connects on TCP 47831 using TLS with the PC certificate fingerprint pinned from the QR. Pairing exchanges a separate random phone credential over that connection. PC pairings are encrypted with Windows DPAPI; phone pairings use Android Keystore AES-GCM. Names are display labels. OpenAI credentials remain on the PC.

Handshake and read deadlines release stalled connections after eight seconds. The phone closes stalled writes, retries discovery sockets and remembered addresses, and prefers newly advertised addresses after DHCP changes. Remote commands have sequence numbers; they are never queued for replay after reconnection.

The APK endpoint is local HTTP on TCP 47830 (or an available port if occupied). It serves only the bundled APK at an unguessable URL. Pairing, audio, and controls use TLS. Windows Firewall must allow incoming TCP from the local subnet to this executable; a one-time administrator-approved exception may be required. `--serve-install path/to/install-qr.png` serves the APK for one hour. Normal usage does not need that distribution mode.

### Build and verification

```powershell
.\build.ps1          # Windows package
.\build.ps1 -Android # Windows + sideloadable APK; needs JDK 17+ and ANDROID_HOME
.\test.ps1           # Real TLS, pairing, reconnect, timeout, state, revocation, download tests
.\test.ps1 -Audio    # Also test native capture and the installed cable's actual audio path
```

The APK uses the development machine's Android debug signing key for immediate testing. Keep that key for updates. Camera scanning, real Wi-Fi transitions, and screen-off shortcuts still require a physical phone test; no phone was connected. The available emulator could not start. Windows input fallback was tested with the existing AudioRelay virtual microphone, and the real cable render/capture path passed. No physical microphone was connected. The final checks cover 26 PC relay/audio/clock/download cases plus three Android clock tests; Android lint passes with warnings.

Pairing UI preview: `Nightcall.exe --render-relay-preview path/to/preview.png`. Use a development scratch directory; the preview deletes its isolated pairing state afterwards.

## Existing dictation behavior

Run `publish/Nightcall.exe`. It stays in the notification area.

- **Ctrl + Alt + Space** starts recording. Press the same shortcut again to transcribe and paste into the original text field.
- The overlay shows microphone level history and elapsed time. Cancel discards the recording.
- **Escape** cancels recording or transcription and dismisses the overlay. It is registered only while the overlay is visible, so other applications keep their normal Escape behavior when dictation is idle.
- Right-click its tray icon and choose **Settings** to select a shortcut, an existing Codex `auth.json`, or an optional language hint. Leave language blank for automatic detection, including mixed Czech and English.
- If you change foreground windows during transcription, the transcript stays in the overlay. You can copy it or explicitly paste into the original window.
- Failed transcription retains audio in memory for Retry. Cancel or Exit discards it. Recording stops automatically after five minutes.

Neither the ChatGPT desktop UI nor the multi-account server needs to run. Transcription uses the selected existing ChatGPT/Codex login, not API billing. Those credentials remain in the selected login file and are read afresh per request. No recordings or transcripts are logged or saved. Audio is uploaded to OpenAI's service. Startup is optional; the local relay listener is part of version 2.

The current transcription endpoint is internal to ChatGPT, not a supported public API, and can change. Expired credentials require signing in to Codex again. The tool does not refresh or overwrite your credentials. It uses Windows' built-in WinHTTP transport, with redirects disabled and a fixed HTTPS destination.

Clipboard contents are snapshotted before paste and restored after one second if no other app changed the clipboard. Standard editable text fields must support Ctrl+V. Password fields, elevated applications, games, and custom editors may reject synthetic paste; when Windows reports a failure the text remains available to copy. Finish & send pastes and presses Enter by default. Paste only inserts text without Enter. A short volume-button chord finishes and sends; holding both for one second finishes without Enter immediately at the threshold, with a brief vibration. Releasing afterward does not trigger again. Screen-off key delivery depends on the phone.

Install Android app shows a separate URL and QR for every active IPv4 interface, including a Windows hotspot. Choose the network shared with the phone. Standard camera download QRs contain a single URL; Nightcall pairing QRs already contain alternative PC addresses.

Settings are saved beside the executable (`settings.json`). The default login is the current Windows user's `.codex/auth.json`; paths and account names are not hardcoded. Run it as the desktop user whose applications should receive dictation, and select another accessible login file in Settings if necessary.

## Build and checks

Requires the .NET 10 Windows Desktop runtime (already installed on the development PC). Build with the .NET 10 SDK:

```powershell
dotnet build -c Release -p:PlatformTarget=x64 -o publish
```

Diagnostic commands do not record the microphone:

```powershell
publish/Nightcall.exe --transcribe-file path/to/sample.wav
publish/Nightcall.exe --render-preview preview.png
publish/Nightcall.exe --smoke-test
publish/Nightcall.exe --test-paste path/to/sample.wav
```

The last command opens its own temporary editor, transcribes the provided sample, pastes into that editor, reports whether the text matched, then exits. Normal operation records only after the shortcut is pressed.


Version 2.5 renews the Android session for ten minutes on phone commands and PC dictation state changes. Recording and transcription keep the session active; completion starts a fresh ten-minute idle period. Idle heartbeats do not extend it. Keep running remains the explicit override.

The first dictation action while disconnected is queued as an explicit start for up to thirty seconds. It switches an open PC recording to fresh phone audio while retaining the paste destination. Repeated start requests leave active phone recordings running. This reconnect behavior does not change Android screen-off shortcut delivery or wake a stopped phone from the PC.

Run `test-resume.ps1` for desktop recording-handoff checks without opening audio hardware or contacting the transcription service.


The permanent executable is `Nightcall/publish/Nightcall.exe`. `build.ps1` updates this location without launching it and removes intermediate Nightcall apphosts. Do not create versioned executable staging folders.
