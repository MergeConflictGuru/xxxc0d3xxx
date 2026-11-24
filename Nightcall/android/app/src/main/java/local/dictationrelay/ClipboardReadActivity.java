package local.dictationrelay;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.WindowManager;
import android.widget.Toast;

/** Brief invisible activity in its own task used to read Android 10+ clipboard without surfacing MainActivity. */
public final class ClipboardReadActivity extends Activity {
    private boolean handled;
    private final Handler handler = new Handler(Looper.getMainLooper());

    @Override protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        WindowManager.LayoutParams lp = getWindow().getAttributes();
        lp.width = 1; lp.height = 1; lp.gravity = Gravity.TOP | Gravity.START; lp.dimAmount = 0f;
        lp.flags = WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE;
        getWindow().setAttributes(lp);
        overridePendingTransition(0, 0);
        handler.postDelayed(this::closeQuietly, 500);
    }

    @Override public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (!hasFocus || handled) return;
        handled = true;
        boolean force = getIntent() != null && getIntent().getBooleanExtra("force", false);
        ClipboardManager cm = (ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
        ClipData clip = cm == null ? null : cm.getPrimaryClip();
        if (clip != null && clip.getItemCount() > 0) {
            RelayService.sendClipData(this, clip, force, force ? msg -> Toast.makeText(getApplicationContext(), msg, Toast.LENGTH_SHORT).show() : null);
        } else if (force) {
            Toast.makeText(getApplicationContext(), "Clipboard is empty", Toast.LENGTH_SHORT).show();
        }
        closeQuietly();
    }

    private void closeQuietly() {
        handled = true;
        finishAndRemoveTask();
        overridePendingTransition(0, 0);
    }

    @Override protected void onDestroy() {
        handler.removeCallbacksAndMessages(null);
        super.onDestroy();
    }
}
