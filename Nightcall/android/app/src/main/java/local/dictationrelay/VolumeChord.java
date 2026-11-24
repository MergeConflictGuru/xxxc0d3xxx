package local.dictationrelay;

final class VolumeChord {
    static final long HOLD_MS = 1000;
    enum Action { NONE, TOGGLE, SUBMIT, PASTE, UP, DOWN }
    private boolean up, down, chord, emitted, recordingChord;
    private long started;
    void reset() { up = down = chord = emitted = recordingChord = false; }
    long holdDeadline() { return chord && recordingChord && !emitted && up && down ? started + HOLD_MS : -1; }
    Action tick(long now) {
        long deadline = holdDeadline();
        if (deadline >= 0 && now >= deadline) { emitted = true; return Action.PASTE; }
        return Action.NONE;
    }
    Action key(boolean upKey, boolean pressed, long now, boolean recording) {
        if (pressed) {
            if (upKey) up = true; else down = true;
            if (up && down && !chord) {
                chord = true; started = now; recordingChord = recording;
                if (!recording) { emitted = true; return Action.TOGGLE; }
            }
            return tick(now);
        }
        Action result = Action.NONE;
        if (chord) {
            if (!emitted) { result = now - started >= HOLD_MS ? Action.PASTE : Action.SUBMIT; emitted = true; }
        } else if (upKey ? up : down) result = upKey ? Action.UP : Action.DOWN;
        if (upKey) up = false; else down = false;
        if (!up && !down) reset();
        return result;
    }
}
