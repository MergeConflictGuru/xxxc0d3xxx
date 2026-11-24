package local.dictationrelay;

import android.Manifest;
import android.app.Activity;
import android.content.*;
import android.content.pm.PackageManager;
import android.os.*;
import android.view.Gravity;
import android.widget.*;

/** A user-triggered foreground transition before opening a microphone on Android 14+. */
public final class WakeActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private boolean started;
    @Override public void onCreate(Bundle saved) {
        setTheme(ThemeClock.night() ? R.style.Nightcall_Night : R.style.Nightcall_Day);
        super.onCreate(saved);
        if (Build.VERSION.SDK_INT >= 27) { setShowWhenLocked(true); setTurnScreenOn(true); }
        else getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED | android.view.WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
        NightcallStyle style = new NightcallStyle(); FrameLayout screen = new FrameLayout(this); screen.setBackground(style.backdrop());
        ImageView logo = new ImageView(this); logo.setImageResource(style.night ? R.drawable.nightcall_night : R.drawable.nightcall_day);
        int size = NightcallStyle.dp(this, 128); screen.addView(logo, new FrameLayout.LayoutParams(size, size, Gravity.CENTER)); setContentView(screen);
    }
    @Override protected void onResume() {
        super.onResume();
        if (started) return; started = true;
        String action = getIntent() == null ? null : getIntent().getAction();
        boolean assist = Intent.ACTION_ASSIST.equals(action) || "android.intent.action.VOICE_ASSIST".equals(action);
        if (assist) {
            Vibrator vibrator = (Vibrator)getSystemService(Context.VIBRATOR_SERVICE);
            if (vibrator != null && vibrator.hasVibrator()) vibrator.vibrate(VibrationEffect.createOneShot(35, VibrationEffect.DEFAULT_AMPLITUDE));
        }
        handler.postDelayed(() -> {
            String kind = getIntent() == null ? null : getIntent().getStringExtra("command");
            if (kind == null && assist) kind = "toggle";
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                startActivity(new Intent(this, MainActivity.class).putExtra("startKind", kind == null ? "" : kind).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP)); finish(); return;
            }
            if (RelayService.microphoneRunning()) { RelayService.request(this, kind); finish(); return; }
            startForegroundService(new Intent(this, RelayService.class).setAction("start").putExtra("command", kind));
            waitForService(SystemClock.elapsedRealtime() + 5000);
        }, assist ? 0 : 100);
    }
    private void waitForService(long deadline) {
        if (RelayService.microphoneRunning() || SystemClock.elapsedRealtime() >= deadline) { finish(); return; }
        handler.postDelayed(() -> waitForService(deadline), 50);
    }
    @Override protected void onPause() { handler.removeCallbacksAndMessages(null); started = false; super.onPause(); }
}
