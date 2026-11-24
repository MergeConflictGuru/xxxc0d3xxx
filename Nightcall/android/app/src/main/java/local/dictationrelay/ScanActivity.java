package local.dictationrelay;

import android.app.Activity;
import android.content.Intent;
import android.hardware.Camera;
import android.graphics.ImageFormat;
import android.os.Bundle;
import android.view.*;
import android.widget.*;
import com.google.zxing.*;
import com.google.zxing.common.HybridBinarizer;
import java.util.*;

@SuppressWarnings("deprecation")
public final class ScanActivity extends Activity implements SurfaceHolder.Callback, Camera.PreviewCallback {
    private Camera camera;
    private Camera.CameraInfo cameraInfo;
    private FrameLayout layout;
    private SurfaceView preview;
    private TextView hint;
    private int frameWidth, frameHeight, displayRotation = -1;
    private boolean resumed;
    private final MultiFormatReader decoder = new MultiFormatReader();
    private boolean found;
    @Override public void onCreate(Bundle saved) {
        setTheme(ThemeClock.night() ? R.style.Nightcall_Night : R.style.Nightcall_Day);
        super.onCreate(saved); NightcallStyle style = new NightcallStyle(); style.window(this);
        Map<DecodeHintType,Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.POSSIBLE_FORMATS, Collections.singletonList(BarcodeFormat.QR_CODE)); hints.put(DecodeHintType.TRY_HARDER, true); decoder.setHints(hints);
        layout = new FrameLayout(this); layout.setBackgroundColor(android.graphics.Color.BLACK);
        preview = new SurfaceView(this); preview.getHolder().addCallback(this); layout.addView(preview);
        layout.addOnLayoutChangeListener((v, l, t, r, b, oldL, oldT, oldR, oldB) -> fitPreview());
        preview.setOnClickListener(v -> focus());
        hint = new TextView(this); hint.setText("NIGHTCALL  /  Scan the PC's Pair a phone QR · tap the picture to focus"); hint.setTextColor(style.ink); hint.setBackground(style.round(this, style.panel, style.line, 12)); hint.setPadding(24, 18, 24, 18);
        layout.addView(hint, new FrameLayout.LayoutParams(-1, -2, Gravity.TOP));
        Button cancel = style.button(this, "Cancel", true, this::finish); layout.addView(cancel, new FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM | Gravity.END)); setContentView(layout);
    }
    @Override public void surfaceCreated(SurfaceHolder holder) {
        open(holder);
    }
    private void open(SurfaceHolder holder) {
        if (!resumed || camera != null || !holder.getSurface().isValid()) return;
        try {
            int id = 0; cameraInfo = new Camera.CameraInfo();
            for (int i = 0; i < Camera.getNumberOfCameras(); i++) { Camera.getCameraInfo(i, cameraInfo); if (cameraInfo.facing == Camera.CameraInfo.CAMERA_FACING_BACK) { id = i; break; } }
            Camera.getCameraInfo(id, cameraInfo); camera = Camera.open(id); Camera.Parameters p = camera.getParameters();
            List<String> modes = p.getSupportedFocusModes();
            if (modes != null && modes.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE)) p.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
            else if (modes != null && modes.contains(Camera.Parameters.FOCUS_MODE_AUTO)) p.setFocusMode(Camera.Parameters.FOCUS_MODE_AUTO);
            List<Camera.Size> sizes = p.getSupportedPreviewSizes();
            Camera.Size size = sizes.stream().filter(s -> s.width <= 1280).max(Comparator.comparingInt(s -> s.width * s.height)).orElse(sizes.get(0));
            frameWidth = size.width; frameHeight = size.height;
            p.setPreviewSize(frameWidth, frameHeight); p.setPreviewFormat(ImageFormat.NV21); camera.setParameters(p);
            fitPreview(); camera.setPreviewDisplay(holder); camera.setPreviewCallback(this); camera.startPreview();
            if (Camera.Parameters.FOCUS_MODE_AUTO.equals(p.getFocusMode())) focus();
        } catch (Exception e) { Toast.makeText(this, "Cannot open camera: " + e.getMessage(), Toast.LENGTH_LONG).show(); finish(); }
    }
    @Override public void onPreviewFrame(byte[] data, Camera camera) {
        if (found || !resumed || camera != this.camera || frameWidth == 0) return;
        try {
            LuminanceSource source = new PlanarYUVLuminanceSource(data, frameWidth, frameHeight, 0, 0, frameWidth, frameHeight, false);
            String text = decoder.decodeWithState(new BinaryBitmap(new HybridBinarizer(source))).getText();
            if (!text.startsWith("dictationrelay://pair?")) { hint.setText("That is not a pairing QR. On the PC, choose Pair a phone and scan that code."); return; }
            found = true; setResult(RESULT_OK, new Intent().putExtra("pair", text)); finish();
        } catch (ReaderException ignored) { } finally { decoder.reset(); }
    }
    private void fitPreview() {
        if (camera == null || frameWidth == 0 || layout.getWidth() == 0 || layout.getHeight() == 0) return;
        int rotation = CameraGeometry.rotation(cameraInfo.orientation, getWindowManager().getDefaultDisplay().getRotation() * 90, cameraInfo.facing == Camera.CameraInfo.CAMERA_FACING_FRONT);
        if (rotation != displayRotation) { camera.setDisplayOrientation(rotation); displayRotation = rotation; }
        int[] size = CameraGeometry.fit(frameWidth, frameHeight, rotation, layout.getWidth(), layout.getHeight());
        FrameLayout.LayoutParams current = (FrameLayout.LayoutParams)preview.getLayoutParams();
        if (current.width != size[0] || current.height != size[1]) preview.setLayoutParams(new FrameLayout.LayoutParams(size[0], size[1], Gravity.CENTER));
    }
    private void focus() { if (camera != null) try { camera.autoFocus((ok, c) -> { }); } catch (RuntimeException ignored) { } }
    @Override public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) { open(holder); fitPreview(); }
    @Override public void surfaceDestroyed(SurfaceHolder holder) { release(); }
    @Override protected void onResume() { super.onResume(); resumed = true; open(preview.getHolder()); }
    @Override protected void onPause() { resumed = false; release(); super.onPause(); }
    private void release() { if (camera != null) { camera.setPreviewCallback(null); camera.stopPreview(); camera.release(); camera = null; } frameWidth = frameHeight = 0; displayRotation = -1; }
}
