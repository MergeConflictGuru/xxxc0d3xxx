package local.dictationrelay;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.os.*;
import android.provider.Settings;
import android.view.*;
import android.widget.*;

public final class MainActivity extends Activity {
    static volatile boolean visible;
    private TextView status, state;
    private CheckBox keep;
    private Button start, dictation, enter, update;
    private boolean displayedNight;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private VolumeKeys keys;
    private String pendingPair;
    private String requestedCommand;
    private Pairings pairings;
    private final Runnable refresh = new Runnable() {
        public void run() {
            if (displayedNight != ThemeClock.night()) buildScreen();
            boolean connected = RelayService.status.startsWith("Connected to ");
            status.setText(RelayService.instance == null && pairings.hasAny() && !RelayService.pairingResult && RelayService.status.startsWith("Idle") ? "Paired · microphone asleep" : RelayService.status);
            String current = RelayService.dictation;
            state.setText("recording".equals(current) ? "Listening. Finish here or on your PC." : "transcribing".equals(current) ? "Turning your words into text…" : "Start dictation to wake the microphone.");
            start.setText(RelayService.microphoneRunning() ? "Stop phone microphone" : "Start microphone · 10 minutes");
            keep.setChecked(RelayService.keepRunning(MainActivity.this));
            dictation.setText("recording".equals(current) ? "Finish & send" : "ready".equals(current) ? "Paste & send" : "error".equals(current) ? "Retry dictation" : "Start dictation");
            dictation.setEnabled(pairings.hasAny() && !"transcribing".equals(current)); dictation.setAlpha(dictation.isEnabled() ? 1f : .45f);
            enter.setEnabled("recording".equals(current) || "ready".equals(current)); enter.setAlpha(enter.isEnabled() ? 1f : .45f);
            if (RelayService.updateReady) {
                update.setVisibility(View.VISIBLE); update.setEnabled(true); update.setAlpha(1f);
                update.setText("Update ready · Tap to install");
            } else if (RelayService.updateProgress >= 0) {
                update.setVisibility(View.VISIBLE); update.setEnabled(false); update.setAlpha(0.75f);
                update.setText("Downloading update… " + RelayService.updateProgress + "%");
            } else if (connected && RelayService.availableUpdateVersion > 0) {
                update.setVisibility(View.VISIBLE); update.setEnabled(true); update.setAlpha(1f);
                String name = RelayService.availableUpdateName;
                update.setText("New version available" + (name.isEmpty() ? "" : " (" + name + ")") + " · Tap to update");
            } else {
                update.setVisibility(View.GONE);
            }
            if (!RelayService.updateError.isEmpty()) state.setText(RelayService.updateError);
            handler.postDelayed(this, 500);
        }
    };
    @Override public void onCreate(Bundle saved) {
        setTheme(ThemeClock.night() ? R.style.Nightcall_Night : R.style.Nightcall_Day);
        super.onCreate(saved); keys = new VolumeKeys(this); pairings = new Pairings(this); ThemeClock.apply(this); ThemeClock.schedule(this); buildScreen();
        handleIntent(getIntent());
    }
    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleIntent(intent);
    }
    private void handleIntent(Intent intent) {
        if (intent == null) return;
        if (Intent.ACTION_SEND.equals(intent.getAction())) {
            RelayService.sendSharedIntent(this, intent, msg -> Toast.makeText(this, msg, Toast.LENGTH_SHORT).show());
            if (pairings.hasAny()) ensureRunning();
            return;
        }
        if (intent.getData() != null) pendingPair = intent.getData().toString();
        if (pendingPair != null) ensureRunning();
        else if (intent.hasExtra("startKind")) { requestedCommand = intent.getStringExtra("startKind"); ensureRunning(); }
        else if (RelayService.keepRunning(this)) ensureRunning();
    }
    private void buildScreen() {
        NightcallStyle style = new NightcallStyle(); displayedNight = style.night;
        setTheme(style.night ? R.style.Nightcall_Night : R.style.Nightcall_Day); style.window(this);
        LinearLayout panel = new LinearLayout(this); panel.setOrientation(LinearLayout.VERTICAL);
        int pad = NightcallStyle.dp(this, 24); panel.setPadding(pad, pad, pad, pad); panel.addView(style.header(this));
        LinearLayout primary = new LinearLayout(this);
        dictation = style.button(this, "Start dictation", true, () -> { requestedCommand = "toggle"; ensureRunning(); });
        LinearLayout.LayoutParams mainAction = new LinearLayout.LayoutParams(0, -2, 1); mainAction.rightMargin = NightcallStyle.dp(this, 8); mainAction.bottomMargin = NightcallStyle.dp(this, 10);
        primary.addView(dictation, mainAction);
        enter = style.button(this, "Paste only", false, () -> { requestedCommand = "finish"; ensureRunning(); });
        enter.setContentDescription("Finish and paste without Enter"); enter.setEnabled(false); enter.setTextSize(13);
        LinearLayout.LayoutParams enterAction = new LinearLayout.LayoutParams(NightcallStyle.dp(this, 96), -2); enterAction.bottomMargin = NightcallStyle.dp(this, 10); primary.addView(enter, enterAction); panel.addView(primary);
        update = style.button(this, "Update app", true, RelayService::startUpdate);
        update.setVisibility(View.GONE);
        LinearLayout.LayoutParams updateLayout = new LinearLayout.LayoutParams(-1, -2);
        updateLayout.bottomMargin = NightcallStyle.dp(this, 10);
        panel.addView(update, updateLayout);
        LinearLayout connection = style.card(this);
        status = style.text(this, RelayService.status, 17, false); connection.addView(status);
        state = style.text(this, "Your next thought is one tap away.", 13, true); connection.addView(state); panel.addView(connection);
        LinearLayout microphone = style.card(this);
        start = style.button(this, "Start phone microphone", false, () -> {
            if (RelayService.instance != null) { RelayService.setKeepRunning(this, false); stopService(new Intent(this, RelayService.class)); }
            else { requestedCommand = null; ensureRunning(); }
        }); microphone.addView(start);
        keep = new CheckBox(this); keep.setText("Keep microphone running"); keep.setTextSize(14); keep.setTextColor(style.ink);
        keep.setChecked(RelayService.keepRunning(this)); keep.setButtonTintList(new ColorStateList(new int[][] { new int[] { android.R.attr.state_checked }, new int[] {} }, new int[] { style.accent, style.muted }));
        keep.setOnClickListener(v -> RelayService.setKeepRunning(this, keep.isChecked())); microphone.addView(keep);
        TextView micHint = style.text(this, "Off while idle. Sleeps after ten idle minutes. Dictation from either device restarts the timer. Keep running for calls or other Windows apps.", 12, true); microphone.addView(micHint); panel.addView(microphone);
        TextView controlsHint = style.text(this, "Finish & send pastes and presses Enter. Paste only inserts text without Enter.\n\nPress both volume buttons to start. While recording, a short press finishes and sends. Hold both for one second to finish without Enter: a brief vibration means you can let go. Screen-off button delivery depends on your phone.", 12, true);
        controlsHint.setPadding(0, 0, 0, NightcallStyle.dp(this, 24)); panel.addView(controlsHint);
        panel.addView(style.button(this, "Send clipboard to PC", false, () -> {
            ClipboardManager cm = (ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
            ClipData clip = cm == null ? null : cm.getPrimaryClip();
            RelayService.sendClipData(this, clip, true, msg -> Toast.makeText(this, msg, Toast.LENGTH_SHORT).show());
            if (RelayService.instance == null && pairings.hasAny()) ensureRunning();
        }));
        panel.addView(style.button(this, "Scan pairing QR", false, () -> {
            if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) requestPermissions(new String[] { Manifest.permission.CAMERA }, 2);
            else startActivityForResult(new Intent(this, ScanActivity.class), 10);
        }));
        panel.addView(style.button(this, "Digital assistant hotkey", false, this::openAssistantSettings));
        panel.addView(style.button(this, "Volume-button shortcut", false, this::openShortcutSettings));
        panel.addView(style.button(this, "Copy connection trace", false, () -> { ((ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("Nightcall trace", RelayService.traceText())); Toast.makeText(this, "Copied connection trace", Toast.LENGTH_SHORT).show(); }));
        panel.addView(style.button(this, "Forget paired computers", false, () -> new AlertDialog.Builder(this).setTitle("Forget the line?").setMessage("Remove all paired computers from this phone?").setPositiveButton("Forget", (d, w) -> {
            stopService(new Intent(this, RelayService.class)); new Pairings(this).forgetAll();
        }).setNegativeButton("Keep them", null).show()));
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(true); scroll.setFitsSystemWindows(true); scroll.setBackground(style.backdrop()); scroll.addView(panel); setContentView(scroll);
    }
    private void openAssistantSettings() {
        Runnable open = () -> {
            try { startActivity(new Intent(Settings.ACTION_VOICE_INPUT_SETTINGS)); }
            catch (Exception e) { startActivity(new Intent(Settings.ACTION_MANAGE_DEFAULT_APPS_SETTINGS)); }
        };
        if (AssistantService.active) { open.run(); return; }
        new AlertDialog.Builder(this).setTitle("Use the assistant hotkey")
            .setMessage("Choose Nightcall as your Default digital assistant app.\n\nOnce set, holding the Power button (or your phone's assistant shortcut) starts or finishes dictation with a short vibration — even when the screen is off or locked.")
            .setPositiveButton("Assistant settings", (d, w) -> open.run())
            .setNegativeButton("Not now", null).show();
    }
    private void openShortcutSettings() {
        if (VolumeShortcut.active) { startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)); return; }
        new AlertDialog.Builder(this).setTitle("Enable the volume shortcut")
            .setMessage("If Nightcall is grayed out, open App info, tap the three-dot menu, and choose Allow restricted settings. Then return to Accessibility and enable Nightcall volume shortcut.\n\nThis permission is optional: it lets both volume buttons start or finish dictation outside the app.")
            .setPositiveButton("Accessibility", (d, w) -> startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)))
            .setNeutralButton("App info", (d, w) -> startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:" + getPackageName()))))
            .setNegativeButton("Not now", null).show();
    }
    private void ensureRunning() {
        if (pendingPair != null) {
            Intent pair = new Intent(this, RelayService.class).setAction("pair").putExtra("pair", pendingPair); pendingPair = null;
            startForegroundService(pair); return;
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            if (Build.VERSION.SDK_INT >= 33) requestPermissions(new String[] { Manifest.permission.RECORD_AUDIO, Manifest.permission.POST_NOTIFICATIONS }, 1);
            else requestPermissions(new String[] { Manifest.permission.RECORD_AUDIO }, 1);
            return;
        }
        try { RelayService.request(this, requestedCommand); requestedCommand = null; } catch (Exception e) { new AlertDialog.Builder(this).setMessage(e.getMessage()).setPositiveButton("OK", null).show(); }
    }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request == 1 && checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) ensureRunning();
        if (request == 2 && checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) startActivityForResult(new Intent(this, ScanActivity.class), 10);
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == 10 && result == RESULT_OK && data != null) { pendingPair = data.getStringExtra("pair"); ensureRunning(); }
    }
    @Override public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus && RelayService.instance != null) {
            ClipboardManager cm = (ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
            ClipData clip = cm == null ? null : cm.getPrimaryClip();
            if (clip != null) RelayService.sendClipData(this, clip, false, null);
        }
    }
    @Override public boolean dispatchKeyEvent(KeyEvent event) {
        if (keys.handle(event)) return true;
        return super.dispatchKeyEvent(event);
    }
    @Override protected void onResume() { super.onResume(); visible = true; ThemeClock.apply(this); handler.post(refresh); }
    @Override protected void onPause() { visible = false; handler.removeCallbacks(refresh); keys.reset(); super.onPause(); }
}
