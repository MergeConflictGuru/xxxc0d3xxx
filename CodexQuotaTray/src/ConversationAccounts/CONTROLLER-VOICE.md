# Controller voice controls

The launcher's existing runtime attachment loads `controller-voice.js`. It does not modify the installed Electron package, install a background input service, or create another control endpoint.

For the first connected controller with Chromium's **standard** mapping:

| Button | Action |
| --- | --- |
| Start / Options | Toggle live voice conversation |
| Back / Share | Toggle composer dictation; stopping leaves the transcript in the composer |
| Bottom face button: Cross / A | Send the draft; if dictating, stop and wait for the final transcript first |
| Right face button: Circle / B | Cancel dictation/transcription and clear the unsent draft text |

Only the focused, visible app window accepts actions. Focus changes, chat changes, controller reconnection, and button combinations require releasing the mapped buttons before another action. Holding a button never repeats its action. Open dialogs block controller actions. Backgrounding the app, navigating to another chat, typing, or clicking cancels a pending automatic send. The controller does not cancel an agent response or delete a saved message.

The controls invoke the current composer's existing named buttons. They support the English labels in the currently installed desktop version and refuse unavailable actions rather than guessing. Unknown gamepad mappings are ignored. No native keyboard keys are synthesized and gamepad input is not globally intercepted.

Chromium may hide a gamepad until its first physical button press. The controller must be awake and recognized by Windows as a gamepad. The activation press is ignored if it is already held when the controller appears; release and press again to invoke an action.

For local diagnostics, `window.conversationControllerVoice.status()` reports controller discovery, focus, mapped actions, and available composer controls, without returning draft text. `dispose()` removes the integration from that page.

Verification:

```powershell
node --test --test-isolation=none test_controller_voice.cjs
node --check controller-voice.js
```

These tests cover routing and focus safeguards, dictation completion before sending, cancellation, disabled controls, and preventing accidental activation of the ordinary Stop-generation button. Hardware input and real audio still require a connected controller and a real button press.
