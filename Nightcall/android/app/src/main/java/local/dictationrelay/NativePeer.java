package local.dictationrelay;

import android.content.Context;
import java.io.*;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.cert.*;
import java.util.*;
import java.util.concurrent.*;
import org.json.*;
import org.java_websocket.client.WebSocketClient;
import org.java_websocket.handshake.ServerHandshake;
import org.java_websocket.drafts.Draft_6455;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.SSLParameters;

/** Small native ICE/DTLS transport: no WebView and no media/video engine. */
final class NativePeer implements AutoCloseable {
    static final boolean AVAILABLE;
    static { boolean loaded; try { System.loadLibrary("nightcall_net"); loaded = true; } catch (UnsatisfiedLinkError e) { loaded = false; } AVAILABLE = loaded; }
    private final java.util.List<WebSocketClient> sockets = new java.util.ArrayList<>();
    private final ScheduledExecutorService signaling = Executors.newSingleThreadScheduledExecutor();
    private volatile long handle;
    private final ExecutorService nativeCalls = Executors.newSingleThreadExecutor();
    private final java.util.concurrent.atomic.AtomicBoolean stopped = new java.util.concurrent.atomic.AtomicBoolean();
    private final String pin;
    private volatile String signalingError = "Signaling server did not register this phone";
    private final CountDownLatch[] servers;
    private final ConcurrentHashMap<String, CompletableFuture<Link>> pending = new ConcurrentHashMap<>();
    private final ConcurrentHashMap<String, Link> links = new ConcurrentHashMap<>();
    private native long nativeCreate(String id, String ca, String urls);
    private static native int nativeSetIce(long handle, String iceJson);
    private static native int nativeSignal(long handle, int server, String message);
    private static native int nativeConnect(long handle, int server, String remote, String session);
    private static native int nativeAccept(long handle, String session, String sdp);
    private static native int nativeSend(long handle, String session, byte[] packet);
    private static native void nativeClose(long handle, String session);
    private static native void nativeDestroy(long handle);
    NativePeer(Context context, String phoneId, JSONObject pc) throws Exception {
        if (!AVAILABLE) throw new IOException("Native direct transport is missing");
        pin = pc.getString("pin");
        JSONArray configured = pc.optJSONArray("servers");
        if (configured == null || configured.length() == 0) configured = new JSONArray().put("wss://0.peerjs.com/peerjs");
        if (configured.length() > 4) throw new IOException("Too many signaling servers");
        servers = new CountDownLatch[configured.length()]; JSONArray urls = new JSONArray();
        String id = "nightcall-phone-" + phoneId + "-" + UUID.randomUUID().toString().substring(0,8), token = UUID.randomUUID().toString();
        for (int i = 0; i < servers.length; i++) {
            servers[i] = new CountDownLatch(1); String endpoint = configured.getString(i);
            if (!"wss".equals(new URI(endpoint).getScheme())) throw new IOException("Signaling must use WSS");
            urls.put(endpoint + (endpoint.contains("?") ? "&" : "?") + "key=peerjs&id=" + id + "&token=" + token + "&version=1.5.5");
        }
        handle = nativeCreate(id, "", urls.toString());
        if (handle == 0) throw new IOException("Cannot start direct transport");
        JSONArray ice = pc.optJSONArray("ice");
        if (ice != null && ice.length() > 0) nativeSetIce(handle, ice.toString());
        for (int i = 0; i < urls.length(); i++) {
            final int serverIndex = i;
            WebSocketClient client = new WebSocketClient(new URI(urls.getString(i)), new Draft_6455(Collections.emptyList(), 131072)) {
                @Override protected void onSetSSLParameters(SSLParameters parameters) { super.onSetSSLParameters(parameters); parameters.setEndpointIdentificationAlgorithm("HTTPS"); }
                @Override public void onOpen(ServerHandshake handshake) { RelayService.trace("ws-open"); }
                @Override public void onMessage(String message) { feedSignal(serverIndex, message); }
                @Override public void onClose(int code, String reason, boolean remote) { RelayService.trace("ws-close " + code + " " + reason); feedSignal(serverIndex, "{\"type\":\"ERROR\"}"); }
                @Override public void onError(Exception error) { signalingError = error.getClass().getSimpleName() + ": " + error.getMessage(); RelayService.trace("ws-err " + signalingError); }
            };
            client.setConnectionLostTimeout(15); sockets.add(client); client.connect();
        }
        signaling.scheduleWithFixedDelay(() -> {
            if (stopped.get()) return;
            for (WebSocketClient socket : sockets) {
                try { if (socket.isOpen()) socket.send("{\"type\":\"HEARTBEAT\"}"); else if (socket.isClosed()) socket.reconnect(); } catch (RuntimeException ignored) { }
            }
        }, 5, 5, TimeUnit.SECONDS);
    }
    private void feedSignal(int server, String message) {
        if (stopped.get() || message.length() > 131072) return;
        try { nativeCalls.execute(() -> { if (handle != 0) nativeSignal(handle, server, message); }); } catch (RejectedExecutionException ignored) { }
    }
    Link connect(String pcId) throws Exception {
        Exception last = new IOException("No signaling service is reachable");
        for (int i = 0; i < servers.length && !stopped.get(); i++) {
            String session = UUID.randomUUID().toString().replace("-", ""); CompletableFuture<Link> result = new CompletableFuture<>();
            try {
                if (!servers[i].await(5, TimeUnit.SECONDS)) { last = new IOException(signalingError); continue; }
                if (stopped.get()) throw new IOException("Direct transport stopped");
                pending.put(session, result);
                final int serverIndex = i;
                RelayService.trace("offer-start"); if (nativeCalls.submit(() -> handle == 0 ? -1 : nativeConnect(handle, serverIndex, "nightcall-pc-" + pcId, session)).get(3, TimeUnit.SECONDS) != 0) throw new IOException("Signaling service not ready");
                return result.get(10, TimeUnit.SECONDS);
            } catch (Exception e) { last = e; closeSession(session); }
        }
        throw new IOException("No direct internet path could be established", last);
    }
    @SuppressWarnings("unused") private void onNativeEvent(String kind, String session, String body, byte[] packet) {
        try {
            JSONObject message = new JSONObject(body);
            if ("registered".equals(kind)) { RelayService.trace("peerjs-registered"); int i = message.getInt("server"); if (i >= 0 && i < servers.length) servers[i].countDown(); }
            else if ("signal-send".equals(kind)) { int index = message.getInt("server"); if (index >= 0 && index < sockets.size() && sockets.get(index).isOpen()) sockets.get(index).send(message.getString("message")); }
            else if ("verify".equals(kind)) {
                String sdp = message.getJSONObject("sdp").getString("sdp");
                byte[] certificate = android.util.Base64.decode(message.getString("certificate"), android.util.Base64.DEFAULT);
                byte[] actual = MessageDigest.getInstance("SHA-256").digest(certificate);
                byte[] expected = new byte[32]; if (pin.length() != 64) throw new IOException("Invalid PC fingerprint");
                for (int i = 0; i < 32; i++) expected[i] = (byte)Integer.parseInt(pin.substring(i*2, i*2+2),16);
                X509Certificate cert = (X509Certificate)CertificateFactory.getInstance("X.509").generateCertificate(new ByteArrayInputStream(certificate));
                Signature verifier = Signature.getInstance("SHA256withRSA"); verifier.initVerify(cert.getPublicKey());
                verifier.update(("nightcall-direct-v1\n" + session + "\n" + sdp).getBytes(StandardCharsets.UTF_8));
                if (!MessageDigest.isEqual(expected, actual) || !verifier.verify(android.util.Base64.decode(message.getString("signature"), android.util.Base64.DEFAULT))) throw new IOException("PC identity verification failed");
                RelayService.trace("answer-verified"); if (!stopped.get()) nativeCalls.execute(() -> { if (handle != 0 && nativeAccept(handle, session, sdp) != 0) { RelayService.trace("nativeAccept-fail"); closeSession(session); } });
            }
            else if ("open".equals(kind)) {
                CompletableFuture<Link> waiter = pending.remove(session);
                if (waiter == null) { closeSession(session); return; }
                RelayService.trace("ice-open"); Link link = new Link(session); links.put(session, link); waiter.complete(link);
            }
            else if ("packet".equals(kind)) { Link link = links.get(session); if (link != null) link.feed(packet); }
            else if ("closed".equals(kind) || "error".equals(kind) && !session.isEmpty()) { RelayService.trace("ice-" + kind + " " + body); closeSession(session); }
        } catch (Exception e) { closeSession(session); }
    }
    private void closeSession(String session) {
        Link link = links.remove(session); if (link != null) link.end();
        CompletableFuture<Link> waiter = pending.remove(session); if (waiter != null) waiter.completeExceptionally(new IOException("Direct connection closed"));
        if (!stopped.get()) { try { nativeCalls.execute(() -> { if (handle != 0) nativeClose(handle, session); }); } catch (RejectedExecutionException ignored) { } }
    }
    @Override public void close() {
        if (!stopped.compareAndSet(false,true)) return;
        signaling.shutdownNow(); for (CountDownLatch ready : servers) ready.countDown(); for (WebSocketClient socket : sockets) socket.closeConnection(1000,"Session stopped");
        nativeCalls.execute(() -> { long old = handle; handle = 0; if (old != 0) nativeDestroy(old); }); nativeCalls.shutdown();
        for (Link link : links.values()) link.end(); links.clear();
        for (CompletableFuture<Link> p : pending.values()) p.completeExceptionally(new IOException("Direct transport stopped")); pending.clear();
    }
    final class Link implements Closeable {
        private final String session;
        private final ArrayBlockingQueue<byte[]> queue = new ArrayBlockingQueue<>(1024);
        private volatile boolean closed;
        private byte[] current = new byte[0]; private int at;
        Link(String session) { this.session = session; }
        void feed(byte[] packet) { if (packet == null || packet.length < 1 || packet.length > 8192 || !queue.offer(packet)) close(); }
        void end() { closed = true; queue.clear(); queue.offer(new byte[0]); }
        final InputStream input = new InputStream() {
            @Override public int read() throws IOException { byte[] one = new byte[1]; return read(one,0,1) == -1 ? -1 : one[0] & 255; }
            @Override public int read(byte[] out, int offset, int count) throws IOException {
                if (count == 0) return 0;
                while (at == current.length) {
                    if (closed) return -1;
                    try { current = queue.poll(15,TimeUnit.SECONDS); at = 0; }
                    catch (InterruptedException e) { Thread.currentThread().interrupt(); throw new IOException(e); }
                    if (current == null) throw new java.net.SocketTimeoutException("Direct connection heartbeat timed out");
                    if (current.length == 0) return -1;
                }
                int n = Math.min(count,current.length-at); System.arraycopy(current,at,out,offset,n); at += n; return n;
            }
        };
        final OutputStream output = new OutputStream() {
            @Override public void write(int value) throws IOException { write(new byte[]{(byte)value},0,1); }
            @Override public void write(byte[] bytes,int offset,int count) throws IOException {
                if (closed) throw new IOException("Direct connection closed"); byte[] packet = Arrays.copyOfRange(bytes,offset,offset+count);
                try {
                    int sent = -1;
                    for (int attempt = 0; attempt < 5 && !closed && !stopped.get(); attempt++) {
                        sent = nativeCalls.submit(() -> handle == 0 ? -1 : nativeSend(handle,session,packet)).get(3,TimeUnit.SECONDS);
                        if (sent == 0) break;
                        Thread.sleep(40);
                    }
                    if (sent != 0) throw new IOException("Direct connection stopped or congested");
                } catch (InterruptedException e) { Thread.currentThread().interrupt(); throw new IOException(e); } catch (ExecutionException | TimeoutException | RejectedExecutionException e) { throw new IOException(e); }
            }
        };
        @Override public void close() { closeSession(session); }
    }
}
