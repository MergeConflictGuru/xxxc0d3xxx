package local.dictationrelay;

import java.io.*;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;

final class Wire {
    static final int PORT = 47831, DISCOVERY = 47832, JSON = 1, AUDIO = 2, OPUS = 3, APK = 4, CLIP = 5;
    private final DataInputStream in;
    private final DataOutputStream out;
    static final class Frame {
        final int type;
        final JSONObject json;
        final byte[] data;
        Frame(int type, JSONObject json, byte[] data) { this.type = type; this.json = json; this.data = data; }
    }
    Wire(InputStream input, OutputStream output) { in = new DataInputStream(input); out = new DataOutputStream(new BufferedOutputStream(output, 8192)); }
    synchronized void send(JSONObject message) throws IOException { write(JSON, message.toString().getBytes(StandardCharsets.UTF_8), 0, -1); }
    synchronized void audio(byte[] bytes, int count) throws IOException { write(AUDIO, bytes, 0, count); }
    synchronized void opus(byte[] packet) throws IOException { write(OPUS, packet, 0, packet.length); }
    synchronized void clip(byte[] data, int offset, int count) throws IOException { write(CLIP, data, offset, count); }
    private void write(int type, byte[] data, int offset, int count) throws IOException {
        if (count < 0) count = data.length - offset;
        if (count > 4095) throw new IOException("Packet too large");
        out.writeInt(count + 1); out.writeByte(type); out.write(data, offset, count); out.flush();
    }
    Frame readFrame() throws Exception {
        int length = in.readInt();
        if (length < 2 || length > 4096) throw new IOException("Invalid relay packet");
        int type = in.readUnsignedByte();
        if (type != JSON && type != APK && type != CLIP) throw new IOException("Invalid relay packet");
        byte[] data = new byte[length - 1]; in.readFully(data);
        return type == JSON ? new Frame(type, new JSONObject(new String(data, StandardCharsets.UTF_8)), null) : new Frame(type, null, data);
    }
    JSONObject read() throws Exception {
        Frame frame = readFrame();
        if (frame.type != JSON) throw new IOException("Invalid relay packet");
        return frame.json;
    }
    static JSONObject message(String kind) {
        try { return new JSONObject().put("kind", kind); } catch (Exception e) { throw new IllegalStateException(e); }
    }
}
