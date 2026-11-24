package local.dictationrelay;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.AccessibilityServiceInfo;
import android.content.Context;
import android.media.AudioManager;
import android.os.*;
import android.view.KeyEvent;
import android.view.accessibility.AccessibilityEvent;

public final class VolumeShortcut extends AccessibilityService {
    static volatile boolean active;
    private VolumeKeys keys;
    @Override protected void onServiceConnected() {
        AccessibilityServiceInfo info = getServiceInfo();
        info.flags |= AccessibilityServiceInfo.FLAG_REQUEST_FILTER_KEY_EVENTS;
        info.eventTypes = AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED;
        setServiceInfo(info); keys = new VolumeKeys(this); active = true;
    }
    @Override protected boolean onKeyEvent(KeyEvent event) { return keys != null && keys.handle(event); }
    @Override public void onAccessibilityEvent(AccessibilityEvent event) { }
    @Override public void onInterrupt() { if (keys != null) keys.reset(); }
    @Override public void onDestroy() { if (keys != null) keys.reset(); active = false; super.onDestroy(); }
}

final class VolumeKeys {
    private final AudioManager audio;
    private final Context context;
    private final VolumeChord chord = new VolumeChord();
    VolumeKeys(Context context) { this.context = context; audio = (AudioManager)context.getSystemService(Context.AUDIO_SERVICE); }
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable held = () -> dispatch(chord.tick(SystemClock.uptimeMillis()));
    void reset() { handler.removeCallbacks(held); chord.reset(); }
    private void dispatch(VolumeChord.Action action) {
        if (action == VolumeChord.Action.TOGGLE) RelayService.request(context, "toggle");
        else if (action == VolumeChord.Action.SUBMIT) RelayService.request(context, "finishSend");
        else if (action == VolumeChord.Action.PASTE) {
            RelayService.request(context, "finish");
            Vibrator vibrator = (Vibrator)context.getSystemService(Context.VIBRATOR_SERVICE);
            if (vibrator != null && vibrator.hasVibrator()) vibrator.vibrate(VibrationEffect.createOneShot(40, VibrationEffect.DEFAULT_AMPLITUDE));
        }
        else if (action == VolumeChord.Action.UP || action == VolumeChord.Action.DOWN)
            audio.adjustSuggestedStreamVolume(action == VolumeChord.Action.UP ? AudioManager.ADJUST_RAISE : AudioManager.ADJUST_LOWER, AudioManager.USE_DEFAULT_STREAM_TYPE, AudioManager.FLAG_SHOW_UI);
    }
    boolean handle(KeyEvent event) {
        int code = event.getKeyCode();
        if (code != KeyEvent.KEYCODE_VOLUME_UP && code != KeyEvent.KEYCODE_VOLUME_DOWN) return false;
        if (event.isCanceled()) { reset(); return true; }
        VolumeChord.Action action = chord.key(code == KeyEvent.KEYCODE_VOLUME_UP, event.getAction() == KeyEvent.ACTION_DOWN, event.getEventTime(), "recording".equals(RelayService.dictation));
        handler.removeCallbacks(held);
        dispatch(action);
        long deadline = chord.holdDeadline();
        if (deadline >= 0) handler.postAtTime(held, deadline);
        return true;
    }
}
