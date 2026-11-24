package local.dictationrelay;
import java.io.IOException;

final class OpusEncoder implements AutoCloseable {
    static final boolean AVAILABLE;
    static { boolean loaded; try { System.loadLibrary("nightcall_audio"); loaded = true; } catch (UnsatisfiedLinkError e) { loaded = false; } AVAILABLE = loaded; }
    private long handle;
    private static native long create();
    private static native byte[] encodeFrame(long handle, byte[] pcm);
    private static native void destroy(long handle);
    OpusEncoder() throws IOException { handle = create(); if (handle == 0) throw new IOException("Cannot create Opus encoder"); }
    byte[] encode(byte[] pcm) throws IOException {
        if (handle == 0 || pcm.length != 3840) throw new IOException("Opus needs a complete 20 ms frame");
        byte[] packet = encodeFrame(handle, pcm);
        if (packet == null) throw new IOException("Opus encoding failed");
        return packet;
    }
    @Override public void close() { if (handle != 0) destroy(handle); handle = 0; }
}
