package local.dictationrelay;

final class CameraGeometry {
    static int rotation(int sensorDegrees, int displayDegrees, boolean front) {
        return front ? (360 - (sensorDegrees + displayDegrees) % 360) % 360 : (sensorDegrees - displayDegrees + 360) % 360;
    }
    static int[] fit(int frameWidth, int frameHeight, int rotation, int width, int height) {
        if (rotation % 180 != 0) { int swap = frameWidth; frameWidth = frameHeight; frameHeight = swap; }
        double scale = Math.min((double)width / frameWidth, (double)height / frameHeight);
        return new int[] { Math.max(1, (int)Math.round(frameWidth * scale)), Math.max(1, (int)Math.round(frameHeight * scale)) };
    }
}
