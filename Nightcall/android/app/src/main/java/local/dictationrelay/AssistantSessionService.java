package local.dictationrelay;

import android.Manifest;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.service.voice.VoiceInteractionSession;
import android.service.voice.VoiceInteractionSessionService;

public final class AssistantSessionService extends VoiceInteractionSessionService {
    @Override public VoiceInteractionSession onNewSession(Bundle args) {
        return new Session(this);
    }

    private static final class Session extends VoiceInteractionSession {
        private final Handler handler = new Handler(Looper.getMainLooper());

        Session(Context context) { super(context); }

        @Override public void onCreate() {
            super.onCreate();
            setUiEnabled(false);
        }

        @Override public void onShow(Bundle args, int showFlags) {
            super.onShow(args, showFlags);
            Context ctx = getContext();
            Vibrator vibrator = (Vibrator)ctx.getSystemService(Context.VIBRATOR_SERVICE);
            if (vibrator != null && vibrator.hasVibrator())
                vibrator.vibrate(VibrationEffect.createOneShot(35, VibrationEffect.DEFAULT_AMPLITUDE));

            if (ctx.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                try {
                    startAssistantActivity(new Intent(ctx, MainActivity.class)
                        .putExtra("startKind", "toggle")
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP));
                } catch (Exception ignored) { }
                finish();
                return;
            }

            if (RelayService.microphoneRunning()) {
                RelayService.request(ctx, "toggle");
                finish();
                return;
            }

            try {
                ctx.startForegroundService(new Intent(ctx, RelayService.class).setAction("start").putExtra("command", "toggle"));
                waitForService(SystemClock.elapsedRealtime() + 1500);
            } catch (RuntimeException e) {
                try {
                    startAssistantActivity(new Intent(ctx, WakeActivity.class)
                        .putExtra("command", "toggle")
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
                } catch (Exception ignored) { }
                finish();
            }
        }

        private void waitForService(long deadline) {
            if (RelayService.microphoneRunning() || SystemClock.elapsedRealtime() >= deadline) {
                finish();
                return;
            }
            handler.postDelayed(() -> waitForService(deadline), 40);
        }

        @Override public void onDestroy() {
            handler.removeCallbacksAndMessages(null);
            super.onDestroy();
        }
    }
}
