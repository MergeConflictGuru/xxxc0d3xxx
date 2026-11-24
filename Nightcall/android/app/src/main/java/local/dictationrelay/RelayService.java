package local.dictationrelay;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.media.*;
import android.net.wifi.WifiManager;
import android.os.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.cert.X509Certificate;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;
import javax.net.ssl.*;
import org.json.*;

public final class RelayService extends Service {
    static volatile RelayService instance;
    static volatile String status = "Idle · microphone off", dictation = "idle";
    static volatile boolean muted;
    static volatile boolean pairingResult;
    static volatile long availableUpdateVersion, availableUpdateSize;
    static volatile String availableUpdateName = "", availableUpdateSha256 = "", updateError = "";
    static volatile int updateProgress = -1;
    static volatile boolean updateReady;
    private static final java.util.ArrayDeque<String> traceRing = new java.util.ArrayDeque<>();
    private static final java.text.SimpleDateFormat traceFmt = new java.text.SimpleDateFormat("HH:mm:ss.SSS", java.util.Locale.US);
    static void trace(String msg) { synchronized (traceRing) { traceRing.addLast(traceFmt.format(new java.util.Date()) + " " + msg); while (traceRing.size() > 60) traceRing.removeFirst(); } }
    static String traceText() { synchronized (traceRing) { StringBuilder sb = new StringBuilder(); for (String s : traceRing) sb.append(s).append('\n'); return sb.length() == 0 ? "(no trace yet)" : sb.toString(); } }
    private volatile boolean running;
    private volatile SSLSocket socket;
    private volatile NativePeer.Link directSocket;
    private volatile NativePeer internetPeer;
    private volatile Wire wire;
    private volatile AudioRecord recorder;
    private volatile DatagramSocket discovery;
    private final ConcurrentHashMap<String, String> discovered = new ConcurrentHashMap<>();
    private final Object discoveryLock = new Object();
    private final ExecutorService controls = Executors.newSingleThreadExecutor();
    private final AtomicLong sequence = new AtomicLong();
    private PowerManager.WakeLock wake;
    private WifiManager.MulticastLock multicast;
    private Pairings pairings;
    private volatile long lastWrite;
    private volatile String pairingUri;
    private Thread connectionThread;
    private final SessionLease lease = new SessionLease();
    private volatile boolean pairingOnly;
    private String pendingCommand;
    private long pendingUntil;
    private static final int MAX_CLIP_BYTES = 10 * 1024 * 1024;
    private static volatile String lastClipHash = "";
    private static final Object pendingClipLock = new Object();
    private static String pendingClipMime;
    private static byte[] pendingClipData;
    private static long pendingClipUntil;
    private static volatile long suppressClipReadUntil;
    private ClipboardManager.OnPrimaryClipChangedListener clipListener;
    private volatile java.lang.Process clipLogcat;
    private volatile long lastOverlayRead;

    static boolean keepRunning(Context context) { return context.getSharedPreferences("session", MODE_PRIVATE).getBoolean("keep", false); }
    static boolean microphoneRunning() { RelayService service = instance; return service != null && !service.pairingOnly; }
    static void setKeepRunning(Context context, boolean value) {
        context.getSharedPreferences("session", MODE_PRIVATE).edit().putBoolean("keep", value).apply();
        RelayService service = instance;
        if (service != null) service.lease.keep(value, SystemClock.elapsedRealtime());
        else if (value) request(context, null);
    }
    static void request(Context context, String kind) {
        RelayService service = instance;
        if (service != null && !service.pairingOnly) { service.requestCommand(kind); return; }
        if (context instanceof Activity || MainActivity.visible) {
            context.startForegroundService(new Intent(context, RelayService.class).setAction("start").putExtra("command", kind)); return;
        }
        context.startActivity(new Intent(context, WakeActivity.class).putExtra("command", kind).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
    }

    @Override public void onCreate() {
        super.onCreate(); pairings = new Pairings(this); ThemeClock.apply(this); ThemeClock.schedule(this);
        getSystemService(NotificationManager.class).createNotificationChannel(new NotificationChannel("relay", "Phone microphone", NotificationManager.IMPORTANCE_LOW));
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && "stop".equals(intent.getAction())) { stopSelf(); return START_NOT_STICKY; }
        boolean pair = intent != null && intent.hasExtra("pair");
        if (!pair && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) { status = "Microphone permission is needed"; stopSelf(); return START_NOT_STICKY; }
        pairingOnly = pair;
        lease.keep(!pair && keepRunning(this), SystemClock.elapsedRealtime());
        if (pair) { pairingResult = false; update("Pairing with PC…"); if (running) closeConnection(); pairingUri = intent.getStringExtra("pair"); synchronized (discoveryLock) { discoveryLock.notifyAll(); } }
        else pairingResult = false;
        if (running && !pair && Build.VERSION.SDK_INT >= 30) {
            try { startForeground(1, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE); }
            catch (RuntimeException e) { status = "Open Nightcall to start the microphone"; stopSelf(); return START_NOT_STICKY; }
        }
        if (!running) {
            try {
                if (Build.VERSION.SDK_INT >= 30) startForeground(1, notification(), pair ? ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE : ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
                else if (Build.VERSION.SDK_INT >= 29 && pair) startForeground(1, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
                else startForeground(1, notification());
                wake = ((PowerManager)getSystemService(POWER_SERVICE)).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Nightcall:microphone"); wake.setReferenceCounted(false);
                multicast = ((WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE)).createMulticastLock("Nightcall:discovery"); multicast.acquire();
                running = true; instance = this;
                new Thread(this::discover, "RelayDiscovery").start();
                connectionThread = new Thread(this::connectLoop, "RelayConnection"); connectionThread.start();
                new Thread(this::watchWrites, "RelayWatchdog").start();
                startClipboardWatch();
            } catch (Exception e) { status = "Could not start: " + e.getMessage(); stopSelf(); return START_NOT_STICKY; }
        }
        if (!pair) requestCommand(intent == null ? null : "toggle".equals(intent.getAction()) ? "toggle" : intent.getStringExtra("command"));
        return START_NOT_STICKY;
    }
    private Notification notification() {
        Intent open = new Intent(this, MainActivity.class);
        PendingIntent view = PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent toggle = PendingIntent.getService(this, 1, new Intent(this, RelayService.class).setAction("toggle"), PendingIntent.FLAG_IMMUTABLE);
        PendingIntent sendClip = PendingIntent.getActivity(this, 3, new Intent(this, ClipboardReadActivity.class).putExtra("force", true).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_MULTIPLE_TASK | Intent.FLAG_ACTIVITY_NO_ANIMATION), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent stop = PendingIntent.getService(this, 2, new Intent(this, RelayService.class).setAction("stop"), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, "relay").setSmallIcon(R.drawable.ic_notification).setLargeIcon(android.graphics.BitmapFactory.decodeResource(getResources(), ThemeClock.night() ? R.drawable.nightcall_night : R.drawable.nightcall_day)).setColor(NightcallStyle.color("#FF53B4")).setContentTitle("Nightcall")
            .setContentText(status + ("idle".equals(dictation) ? "" : " · " + dictation)).setOngoing(true).setContentIntent(view)
            .addAction(new Notification.Action.Builder(null, "Dictation", toggle).build())
            .addAction(new Notification.Action.Builder(null, "Send clipboard", sendClip).build())
            .addAction(new Notification.Action.Builder(null, "Stop", stop).build()).build();
    }
    private void update(String text) { status = text; if (running) getSystemService(NotificationManager.class).notify(1, notification()); }
    private void discover() {
        while (running) {
            try (DatagramSocket udp = new DatagramSocket(null)) {
                udp.setReuseAddress(true); udp.bind(new InetSocketAddress(Wire.DISCOVERY)); udp.setSoTimeout(3000); discovery = udp;
                byte[] bytes = new byte[2048];
                while (running) {
                    DatagramPacket packet = new DatagramPacket(bytes, bytes.length);
                    try {
                        udp.receive(packet); JSONObject data = new JSONObject(new String(bytes, 0, packet.getLength(), StandardCharsets.UTF_8));
                        if ("dictation-relay".equals(data.optString("app")) && data.optInt("v") == 1 && data.optInt("port") == Wire.PORT) { String host = packet.getAddress().getHostAddress(); if (isUsableHost(host)) { discovered.put(data.getString("id"), host); synchronized (discoveryLock) { discoveryLock.notifyAll(); } if (wire == null) { NativePeer peer = internetPeer; if (peer != null) peer.close(); } } }
                    } catch (SocketTimeoutException ignored) { }
                    catch (JSONException ignored) { }
                }
            } catch (Exception e) { if (running) pause(2000); }
        }
    }
    private void connectLoop() {
        while (running) {
            try {
                String pending = pairingUri;
                if (pending != null) {
                    pairingUri = null;
                    try { pair(pending); }
                    catch (Exception e) { update("Pairing failed: " + reason(e)); }
                    finally { pairingResult = true; if (pairingOnly) stopSelf(); }
                    if (pairingOnly) return;
                }
                JSONArray computers = pairings.all();
                if (computers.length() == 0) { update("Scan the PC pairing QR to connect"); pause(1500); continue; }
                update("Looking for a paired PC…");
                for (int i = 0; i < computers.length() && running && pairingUri == null; i++) connectPc(computers.getJSONObject(i));
                if (running && pairingUri == null) waitDiscovery(1500);
            } catch (Exception e) { update("Connection needs attention: " + e.getMessage()); pause(3000); }
        }
    }
    private boolean onWifi() {
        try {
            android.net.ConnectivityManager cm = (android.net.ConnectivityManager)getSystemService(CONNECTIVITY_SERVICE);
            android.net.Network active = cm == null ? null : cm.getActiveNetwork();
            android.net.NetworkCapabilities caps = active == null ? null : cm.getNetworkCapabilities(active);
            return caps == null || caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_ETHERNET);
        } catch (Exception e) { return true; }
    }
    private static final class Winner {
        final String host; final java.io.Closeable transport; final Wire wire; final NativePeer peer;
        Winner(String host, java.io.Closeable transport, Wire wire, NativePeer peer) { this.host = host; this.transport = transport; this.wire = wire; this.peer = peer; }
    }
    private void connectPc(JSONObject pc) throws Exception {
        String id = pc.getString("id"), name = pc.optString("name", "PC");
        String fresh = discovered.get(id);
        boolean wifi = onWifi(); trace("connectPc wifi=" + wifi + " fresh=" + (fresh != null) + " hosts=" + pc.getJSONArray("hosts").length() + " ice=" + (pc.optJSONArray("ice") == null ? 0 : pc.optJSONArray("ice").length()));
        if (wifi && isUsableHost(fresh)) {
            try { trace("wifi-fresh-try " + fresh); stream(pc, fresh); return; } catch (Exception e) { trace("wifi-fresh-err " + reason(e)); discovered.remove(id, fresh); }
        }
        CompletableFuture<Winner> winner = new CompletableFuture<>();
        java.util.concurrent.atomic.AtomicReference<Exception> lastError = new java.util.concurrent.atomic.AtomicReference<>();
        java.util.concurrent.atomic.AtomicReference<SSLSocket> wifiProbe = new java.util.concurrent.atomic.AtomicReference<>();
        java.util.concurrent.atomic.AtomicReference<NativePeer> netProbe = new java.util.concurrent.atomic.AtomicReference<>();
        CompletableFuture<Void> wifiTask = wifi ? CompletableFuture.runAsync(() -> {
            try {
                String tried = null;
                JSONArray hosts = pc.getJSONArray("hosts");
                for (int h = 0; h < hosts.length() && running && pairingUri == null && !winner.isDone(); h++) {
                    String adv = discovered.get(id);
                    if (isUsableHost(adv) && !adv.equals(tried)) {
                        tried = adv;
                        try {
                            SSLSocket s = connect(pc, adv); wifiProbe.set(s);
                            if (winner.complete(new Winner(adv, s, new Wire(s.getInputStream(), s.getOutputStream()), null))) return;
                            s.close(); return;
                        } catch (Exception e) { lastError.set(e); discovered.remove(id, adv); }
                    }
                    String host = hosts.getString(h);
                    if (!isUsableHost(host) || host.equals(tried)) continue;
                    try {
                        SSLSocket s = connect(pc, host); wifiProbe.set(s);
                        if (winner.complete(new Winner(host, s, new Wire(s.getInputStream(), s.getOutputStream()), null))) return;
                        s.close(); return;
                    } catch (Exception e) { lastError.set(e); }
                }
                if (running && pairingUri == null && !winner.isDone() && !isUsableHost(discovered.get(id))) waitDiscovery(1000);
                String late = discovered.get(id);
                if (running && pairingUri == null && !winner.isDone() && isUsableHost(late) && !late.equals(tried)) {
                    try {
                        SSLSocket s = connect(pc, late); wifiProbe.set(s);
                        if (winner.complete(new Winner(late, s, new Wire(s.getInputStream(), s.getOutputStream()), null))) return;
                        s.close();
                    } catch (Exception e) { lastError.set(e); discovered.remove(id, late); }
                }
            } catch (Exception e) { lastError.set(e); }
        }) : CompletableFuture.completedFuture(null);
        CompletableFuture<Void> netTask = NativePeer.AVAILABLE ? CompletableFuture.runAsync(() -> {
            NativePeer peer = null;
            try {
                trace("net-start"); peer = new NativePeer(this, pairings.phoneId(), pc);
                netProbe.set(peer); internetPeer = peer;
                if (winner.isDone() || !running || pairingUri != null) { peer.close(); return; }
                NativePeer.Link link = peer.connect(id);
                trace("net-win"); if (winner.complete(new Winner(null, link, new Wire(link.input, link.output), peer))) return;
                link.close(); peer.close();
            } catch (Exception e) {
                trace("net-err " + reason(e)); lastError.set(e);
                if (peer != null) peer.close();
            }
        }) : CompletableFuture.completedFuture(null);
        CompletableFuture.allOf(wifiTask, netTask).thenRun(() -> winner.complete(null));
        Winner chosen = null;
        try { chosen = winner.get(); } catch (Exception ignored) { }
        if (chosen == null) {
            NativePeer p = netProbe.getAndSet(null); if (p != null) p.close();
            SSLSocket s = wifiProbe.getAndSet(null); if (s != null) try { s.close(); } catch (Exception ignored) { }
            internetPeer = null; socket = null;
            Exception err = lastError.get();
            if (running && err != null) update("Waiting for " + name + " · " + reason(err));
            return;
        }
        if (chosen.host != null) {
            NativePeer p = netProbe.getAndSet(null); if (p != null) p.close();
            internetPeer = null;
            socket = (SSLSocket)chosen.transport;
        } else {
            SSLSocket s = wifiProbe.getAndSet(null); if (s != null) try { s.close(); } catch (Exception ignored) { }
            socket = null;
            directSocket = (NativePeer.Link)chosen.transport;
            internetPeer = chosen.peer;
        }
        trace("winner=" + (chosen.host == null ? "internet" : "wifi:" + chosen.host)); try { runSession(pc, chosen.host, chosen.wire, chosen.transport); } catch (Exception e) { trace("session-err " + reason(e)); throw e; }
        finally {
            wire = null; socket = null; directSocket = null; internetPeer = null;
            try { chosen.transport.close(); } catch (Exception ignored) { }
            if (chosen.peer != null) chosen.peer.close();
        }
    }
    private void waitDiscovery(long ms) {
        synchronized (discoveryLock) {
            if (!running || pairingUri != null) return;
            try { discoveryLock.wait(ms); } catch (InterruptedException ignored) { }
        }
    }
    private SSLSocket connect(JSONObject pc, String host) throws Exception {
        byte[] expected = hex(pc.getString("pin"));
        SSLContext tls = SSLContext.getInstance("TLS");
        tls.init(null, new TrustManager[] { new X509TrustManager() {
            public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            public void checkClientTrusted(X509Certificate[] chain, String type) throws java.security.cert.CertificateException { throw new java.security.cert.CertificateException("Not a client trust manager"); }
            public void checkServerTrusted(X509Certificate[] chain, String type) throws java.security.cert.CertificateException {
                try { if (chain.length == 0 || !MessageDigest.isEqual(expected, MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded()))) throw new java.security.cert.CertificateException("PC identity changed; pair again"); }
                catch (GeneralSecurityException e) { throw new java.security.cert.CertificateException(e); }
            }
        } }, new SecureRandom());
        SSLSocket s = (SSLSocket)tls.getSocketFactory().createSocket(); socket = s;
        try {
            s.connect(new InetSocketAddress(host, pc.getInt("port")), 700); s.setTcpNoDelay(true); s.setSoTimeout(8000); s.startHandshake(); return s;
        } catch (Exception e) { s.close(); if (socket == s) socket = null; throw e; }
    }
    private void pair(String uri) throws Exception {
        android.net.Uri link = android.net.Uri.parse(uri);
        if (!"dictationrelay".equals(link.getScheme()) || !"pair".equals(link.getHost())) throw new Exception("Not a Nightcall pairing QR code");
        JSONObject pc = new JSONObject(new String(android.util.Base64.decode(link.getQueryParameter("data"), android.util.Base64.DEFAULT), StandardCharsets.UTF_8));
        if (pc.getInt("v") != 1 || pc.getInt("port") != Wire.PORT || hex(pc.getString("pin")).length != 32) throw new Exception("Unsupported pairing code");
        JSONArray hosts = pc.getJSONArray("hosts"); Exception last = new Exception("PC has no reachable Wi-Fi address");
        for (int i = 0; i < hosts.length() && running; i++) {
            update("Pairing over Wi-Fi (" + (i+1) + "/" + hosts.length() + ")…");
            String host = hosts.getString(i); if (!isUsableHost(host)) continue; update("Pairing over Wi-Fi (" + (i+1) + "/" + hosts.length() + ")…");
            try (SSLSocket s = connect(pc, host)) {
                Wire w = new Wire(s.getInputStream(), s.getOutputStream());
                w.send(Wire.message("pair").put("id", pairings.phoneId()).put("name", Build.MANUFACTURER + " " + Build.MODEL).put("token", pc.getString("token")));
                JSONObject response = w.read(); if (!"paired".equals(response.optString("kind"))) throw new Exception("Pairing was rejected");
                pc.remove("token"); pc.put("secret", response.getString("secret")); rememberHost(pc, hosts.getString(i));
                pairings.save(pc); update("Paired with " + pc.optString("name", "PC")); return;
            } catch (Exception e) { last = e; }
            finally { socket = null; }
        }
        if (NativePeer.AVAILABLE && running) {
            update("Pairing through direct discovery…");
            try (NativePeer peer = new NativePeer(this, pairings.phoneId(), pc); NativePeer.Link linkSocket = peer.connect(pc.getString("id"))) {
                directSocket = linkSocket;
                Wire w = new Wire(linkSocket.input, linkSocket.output);
                w.send(Wire.message("pair").put("id", pairings.phoneId()).put("name", Build.MANUFACTURER + " " + Build.MODEL).put("token", pc.getString("token")));
                JSONObject response = w.read(); if (!"paired".equals(response.optString("kind"))) throw new Exception("Pairing was rejected");
                pc.remove("token"); pc.put("secret", response.getString("secret")); pairings.save(pc); update("Paired with " + pc.optString("name", "PC")); return;
            } catch (Exception e) { last = e; }
            finally { directSocket = null; }
        }
        throw new Exception("Check connectivity and use a fresh code. " + reason(last));
    }
    private void rememberHost(JSONObject pc, String host) throws Exception {
        JSONArray previous = pc.getJSONArray("hosts"), next = new JSONArray().put(host);
        for (int i = 0; i < previous.length(); i++) { String item = previous.getString(i); if (!host.equals(item) && isUsableHost(item)) next.put(item); }
        pc.put("hosts", next);
    }
    private void stream(JSONObject pc, String host) throws Exception {
        try (SSLSocket s = connect(pc, host)) { runSession(pc, host, new Wire(s.getInputStream(), s.getOutputStream()), s); }
        finally { wire = null; socket = null; }
    }
    private void streamInternet(JSONObject pc) throws Exception {
        try (NativePeer peer = new NativePeer(this, pairings.phoneId(), pc)) {
            internetPeer = peer;
            try (NativePeer.Link s = peer.connect(pc.getString("id"))) { directSocket = s; runSession(pc, null, new Wire(s.input, s.output), s); }
        } finally { wire = null; directSocket = null; internetPeer = null; }
    }
    private void runSession(JSONObject pc, String host, Wire w, java.io.Closeable s) throws Exception {
            w.send(Wire.message("auth").put("id", pairings.phoneId()).put("secret", pc.getString("secret")));
            JSONObject ready = w.read(); if (!"ready".equals(ready.optString("kind"))) return;
            JSONArray nextServers = ready.optJSONArray("servers");
            if (nextServers != null && nextServers.length() > 0 && nextServers.length() <= 4 && !nextServers.toString().equals(pc.optString("servers"))) {
                for (int i = 0; i < nextServers.length(); i++) if (!"wss".equals(new java.net.URI(nextServers.getString(i)).getScheme())) throw new Exception("Invalid discovery server");
                pc.put("servers", nextServers); pairings.save(pc);
            }
            JSONArray nextIce = ready.optJSONArray("ice");
            if (nextIce != null && nextIce.length() <= 4 && !nextIce.toString().equals(pc.optString("ice"))) {
                pc.put("ice", nextIce); pairings.save(pc);
            }
            long pcApk = ready.optLong("apkVersion", 0);
            if (pcApk > installedVersionCode()) {
                availableUpdateVersion = pcApk;
                availableUpdateName = ready.optString("apkVersionName", "v" + pcApk);
                availableUpdateSize = ready.optLong("apkSize", 0);
                availableUpdateSha256 = ready.optString("apkSha256", "");
            } else {
                availableUpdateVersion = 0; availableUpdateName = ""; availableUpdateSize = 0; availableUpdateSha256 = "";
            }
            updateError = ""; updateProgress = -1;
            wire = w; dictation = "idle"; lease.state("idle", SystemClock.elapsedRealtime()); lastWrite = SystemClock.elapsedRealtime();
            trace("session-ready " + (host == null ? "internet" : "wifi")); update("Connected to " + pc.optString("name", "PC") + (host == null ? " - direct internet" : " - Wi-Fi"));
            if (host != null && !host.equals(pc.getJSONArray("hosts").optString(0))) { rememberHost(pc, host); pairings.save(pc); } else if (!pc.getString("id").equals(pairings.all().optJSONObject(0) == null ? "" : pairings.all().optJSONObject(0).optString("id"))) { pairings.save(pc); }
            boolean useOpus = OpusEncoder.AVAILABLE && "opus".equals(ready.optString("codec"));
            Thread audio = new Thread(() -> capture(w, s, useOpus), "RelayAudio"); audio.start();
            String queued;
            synchronized (this) { queued = SystemClock.elapsedRealtime() < pendingUntil ? pendingCommand : null; pendingCommand = null; }
            if (queued != null) sendCommand(queued, w); String t = traceText(); if (t.length() > 3000) t = t.substring(t.length() - 3000); try { w.send(Wire.message("trace").put("text", t)); } catch (Exception ignored) { }
            flushPendingClipboard();
            java.io.File partFile = new java.io.File(getCacheDir(), "update.apk.part");
            java.io.File shaFile = new java.io.File(getCacheDir(), "update.apk.sha");
            java.io.FileOutputStream apkOut = null;
            long apkBytes = 0;
            int ackBatch = 0;
            java.io.ByteArrayOutputStream clipOut = null;
            String clipMime = "", clipSha = "";
            long clipSize = 0;
            try {
                while (running && pairingUri == null) {
                    Wire.Frame frame = w.readFrame();
                    if (frame.type == Wire.CLIP) {
                        if (clipOut != null && clipOut.size() + frame.data.length <= MAX_CLIP_BYTES) clipOut.write(frame.data);
                        else clipOut = null;
                        continue;
                    }
                    if (frame.type == Wire.APK) {
                        if (apkOut == null) {
                            long offset = resumeOffset(partFile, shaFile, availableUpdateSha256, availableUpdateSize);
                            apkOut = new java.io.FileOutputStream(partFile, offset > 0);
                            apkBytes = offset;
                            ackBatch = 0;
                            writeText(shaFile, availableUpdateSha256);
                        }
                        apkOut.write(frame.data);
                        apkBytes += frame.data.length;
                        if (availableUpdateSize > 0) updateProgress = (int)Math.min(99, apkBytes * 100 / availableUpdateSize);
                        if (++ackBatch >= 4) {
                            int count = ackBatch; ackBatch = 0;
                            w.send(Wire.message("apkAck").put("count", count));
                        }
                        continue;
                    }
                    JSONObject message = frame.json;
                    String kind = message.optString("kind");
                    if ("state".equals(kind)) {
                        String next = message.optString("state", "idle");
                        lease.state(next, SystemClock.elapsedRealtime());
                        boolean nextMute = message.optBoolean("muted");
                        if (!next.equals(dictation) || nextMute != muted) { dictation = next; muted = nextMute; update(status); }
                    } else if ("apkMeta".equals(kind)) {
                        if (apkOut != null) { apkOut.close(); apkOut = null; }
                        long nextSize = message.optLong("size", 0);
                        String nextSha = message.optString("sha256", "");
                        long startOffset = message.optLong("offset", 0);
                        if (nextSize > 0) availableUpdateSize = nextSize;
                        if (!nextSha.isEmpty()) availableUpdateSha256 = nextSha;
                        if (startOffset == 0) { partFile.delete(); shaFile.delete(); }
                        apkOut = new java.io.FileOutputStream(partFile, startOffset > 0);
                        apkBytes = startOffset;
                        ackBatch = 0;
                        writeText(shaFile, availableUpdateSha256);
                    } else if ("apkDone".equals(kind)) {
                        if (apkOut != null) { apkOut.close(); apkOut = null; }
                        String expectedSha = !availableUpdateSha256.isEmpty() ? availableUpdateSha256 : message.optString("sha256", "");
                        String actualSha = sha256File(partFile);
                        java.io.File finalFile = new java.io.File(getCacheDir(), "update.apk");
                        if (partFile.length() > 0 && (availableUpdateSize <= 0 || partFile.length() == availableUpdateSize) && expectedSha.equalsIgnoreCase(actualSha)) {
                            if (finalFile.exists()) finalFile.delete();
                            if (!partFile.renameTo(finalFile)) throw new java.io.IOException("Cannot stage update");
                            shaFile.delete();
                            updateProgress = -1;
                            updateReady = true;
                            if (MainActivity.visible) installApk();
                        } else {
                            partFile.delete(); shaFile.delete();
                            updateProgress = -1;
                            updateError = "Update verification failed";
                        }
                    } else if ("apkError".equals(kind)) {
                        if (apkOut != null) { apkOut.close(); apkOut = null; }
                        partFile.delete(); shaFile.delete();
                        updateProgress = -1;
                        updateError = message.optString("message", "Update failed");
                    } else if ("clipStart".equals(kind)) {
                        clipMime = message.optString("mime", "");
                        clipSize = message.optLong("size", 0);
                        clipSha = message.optString("sha256", "");
                        clipOut = (clipSize > 0 && clipSize <= MAX_CLIP_BYTES && !clipMime.isEmpty())
                            ? new java.io.ByteArrayOutputStream((int)Math.min(clipSize, 65536)) : null;
                    } else if ("clipDone".equals(kind)) {
                        if (clipOut != null) {
                            byte[] clipData = clipOut.toByteArray();
                            clipOut = null;
                            String expectedSha = !clipSha.isEmpty() ? clipSha : message.optString("sha256", "");
                            if (clipData.length == clipSize && expectedSha.equalsIgnoreCase(sha256Bytes(clipData))) {
                                applyRemoteClipboard(clipMime, clipData, expectedSha);
                            }
                        }
                    }
                }
            } finally {
                if (apkOut != null) try { apkOut.close(); } catch (Exception ignored) { }
                updateProgress = -1;
                wire = null; s.close(); AudioRecord record = recorder; if (record != null) try { record.stop(); } catch (Exception ignored) { }
                audio.join(2000); dictation = "idle"; lease.state("idle", SystemClock.elapsedRealtime());
            }
    }
    private void capture(Wire w, java.io.Closeable s, boolean useOpus) {
        AudioRecord record = null; OpusEncoder encoder = null;
        try {
            if (useOpus) encoder = new OpusEncoder();
            int rate = useOpus ? 48000 : 16000;
            int channels = useOpus ? 2 : 1;
            int mask = channels == 2 ? AudioFormat.CHANNEL_IN_STEREO : AudioFormat.CHANNEL_IN_MONO;
            int bytesPerFrame = rate / 50 * channels * 2;
            int size = Math.max(bytesPerFrame * 4, AudioRecord.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT));
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) throw new SecurityException("Microphone permission was revoked");
            try { record = new AudioRecord(useOpus ? MediaRecorder.AudioSource.MIC : MediaRecorder.AudioSource.VOICE_RECOGNITION, rate, mask, AudioFormat.ENCODING_PCM_16BIT, size); } catch (IllegalArgumentException ignored) { }
            if ((record == null || record.getState() != AudioRecord.STATE_INITIALIZED) && channels == 2) {
                if (record != null) record.release(); channels = 1; bytesPerFrame = rate / 50 * 2;
                size = Math.max(bytesPerFrame * 4, AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT));
                try { record = new AudioRecord(MediaRecorder.AudioSource.MIC, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, size); } catch (IllegalArgumentException ignored) { record = null; }
            }
            if (useOpus && (record == null || record.getState() != AudioRecord.STATE_INITIALIZED)) {
                if (record != null) record.release(); rate = 16000; channels = 1; bytesPerFrame = 640;
                size = Math.max(bytesPerFrame * 4, AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT));
                record = new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, size);
            }
            if (record == null || record.getState() != AudioRecord.STATE_INITIALIZED) throw new Exception("Cannot open the phone microphone");
            recorder = record; record.startRecording(); byte[] data = new byte[bytesPerFrame];
            wake.acquire(60_000);
            while (running && wire == w) {
                int count = 0;
                while (count < data.length && running && wire == w) {
                    int read = record.read(data, count, data.length - count, AudioRecord.READ_BLOCKING);
                    if (read <= 0) throw new Exception("Phone microphone stopped");
                    count += read;
                }
                if (count != data.length) break;
                // Muting is enforced on the PC as well; preserve heartbeat packets when muted.
                if (muted) java.util.Arrays.fill(data, (byte)0);
                if (encoder != null) {
                    byte[] stereo = data;
                    if (channels == 1) {
                        stereo = new byte[3840]; int sourceSamples = data.length / 2;
                        for (int frame = 0; frame < 960; frame++) {
                            int index = frame * rate / 48000, phase = frame * rate % 48000;
                            int a = (short)((data[index*2] & 255) | data[index*2+1] << 8);
                            int next = Math.min(index+1, sourceSamples-1), b = (short)((data[next*2] & 255) | data[next*2+1] << 8);
                            short value = (short)(a + (b-a) * phase / 48000);
                            stereo[frame*4] = stereo[frame*4+2] = (byte)value; stereo[frame*4+1] = stereo[frame*4+3] = (byte)(value >> 8);
                        }
                    }
                    w.opus(encoder.encode(stereo));
                } else w.audio(data, count); lastWrite = SystemClock.elapsedRealtime();
            }
        } catch (Exception e) { if (running && wire == w) update("Microphone unavailable; reconnecting…"); }
        finally { if (encoder != null) encoder.close(); if (record != null) { try { record.stop(); } catch (Exception ignored) { } record.release(); } recorder = null; if (wake.isHeld()) wake.release(); try { s.close(); } catch (Exception ignored) { } }
    }
    private void watchWrites() {
        int ticks = 0; boolean night = ThemeClock.night();
        while (running) {
            if (lease.expired(SystemClock.elapsedRealtime())) { stopSelf(); return; }
            if (wire != null && SystemClock.elapsedRealtime() - lastWrite > 15000) closeConnection();
            if (ticks % 30 == 0 && recorder != null) wake.acquire(60_000);
            if (ticks++ % 30 == 0 && night != ThemeClock.night()) { night = ThemeClock.night(); ThemeClock.apply(this); ThemeClock.schedule(this); update(status); }
            pause(1000);
        }
    }
    static void startUpdate() {
        RelayService service = instance;
        if (service == null) return;
        if (updateReady && new java.io.File(service.getCacheDir(), "update.apk").exists()) { service.installApk(); return; }
        if (service.wire == null || updateProgress >= 0) return;
        updateError = ""; updateReady = false;
        long offset = resumeOffset(new java.io.File(service.getCacheDir(), "update.apk.part"), new java.io.File(service.getCacheDir(), "update.apk.sha"), availableUpdateSha256, availableUpdateSize);
        updateProgress = availableUpdateSize > 0 && offset > 0 ? (int)Math.min(99, offset * 100 / availableUpdateSize) : 0;
        Wire current = service.wire;
        service.controls.execute(() -> {
            try { if (service.wire == current) current.send(Wire.message("getApk").put("offset", offset).put("sha256", availableUpdateSha256)); }
            catch (Exception e) { updateProgress = -1; updateError = "Update request failed"; }
        });
    }
    private static long resumeOffset(java.io.File partFile, java.io.File shaFile, String expectedSha, long expectedSize) {
        if (!partFile.exists() || !shaFile.exists() || expectedSha == null || expectedSha.isEmpty()) return 0;
        try {
            String saved = new String(java.nio.file.Files.readAllBytes(shaFile.toPath()), StandardCharsets.UTF_8).trim();
            long len = partFile.length();
            if (expectedSha.equalsIgnoreCase(saved) && len > 0 && (expectedSize <= 0 || len < expectedSize)) return len;
        } catch (Exception ignored) { }
        partFile.delete(); shaFile.delete();
        return 0;
    }
    private static void writeText(java.io.File file, String text) {
        try (java.io.FileOutputStream out = new java.io.FileOutputStream(file, false)) { out.write((text == null ? "" : text).getBytes(StandardCharsets.UTF_8)); }
        catch (Exception ignored) { }
    }
    private static String sha256File(java.io.File file) {
        try (java.io.FileInputStream in = new java.io.FileInputStream(file)) {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] buf = new byte[8192]; int n;
            while ((n = in.read(buf)) > 0) md.update(buf, 0, n);
            return toHex(md.digest());
        } catch (Exception e) { return ""; }
    }
    private long installedVersionCode() {
        try {
            android.content.pm.PackageInfo info = getPackageManager().getPackageInfo(getPackageName(), 0);
            return Build.VERSION.SDK_INT >= 28 ? info.getLongVersionCode() : info.versionCode;
        } catch (Exception e) { return 0; }
    }
    private void installApk() {
        Intent install = new Intent(Intent.ACTION_VIEW)
            .setDataAndType(android.net.Uri.parse("content://local.dictationrelay.apk/update.apk"), "application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
        startActivity(install);
    }
    private static String toHex(byte[] bytes) {
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) sb.append(String.format("%02x", b & 255));
        return sb.toString();
    }
    private static String sha256Bytes(byte[] data) {
        try { return toHex(MessageDigest.getInstance("SHA-256").digest(data)); }
        catch (Exception e) { return ""; }
    }
    private void applyRemoteClipboard(String mime, byte[] data, String sha) {
        lastClipHash = sha;
        suppressClipReadUntil = SystemClock.elapsedRealtime() + 1500;
        lease.touch(SystemClock.elapsedRealtime());
        if (mime.startsWith("image/")) {
            try {
                java.io.File tmp = new java.io.File(getCacheDir(), "clip.png.tmp");
                java.io.File dst = new java.io.File(getCacheDir(), "clip.png");
                try (java.io.FileOutputStream out = new java.io.FileOutputStream(tmp, false)) { out.write(data); }
                if (dst.exists()) dst.delete();
                if (!tmp.renameTo(dst)) return;
                new Handler(Looper.getMainLooper()).post(() -> {
                    try {
                        ClipboardManager cm = (ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
                        if (cm != null) cm.setPrimaryClip(ClipData.newUri(getContentResolver(), "Nightcall", android.net.Uri.parse("content://local.dictationrelay.apk/clip.png")));
                    } catch (Exception ignored) { }
                });
            } catch (Exception ignored) { }
        } else if ("text/plain".equals(mime)) {
            String text = new String(data, StandardCharsets.UTF_8);
            new Handler(Looper.getMainLooper()).post(() -> {
                try {
                    ClipboardManager cm = (ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
                    if (cm != null) cm.setPrimaryClip(ClipData.newPlainText("Nightcall", text));
                } catch (Exception ignored) { }
            });
        }
    }
    private void flushPendingClipboard() {
        String mime; byte[] data;
        synchronized (pendingClipLock) {
            if (pendingClipData == null || SystemClock.elapsedRealtime() >= pendingClipUntil) {
                pendingClipMime = null; pendingClipData = null; return;
            }
            mime = pendingClipMime; data = pendingClipData;
            pendingClipMime = null; pendingClipData = null;
        }
        sendClipboard(mime, data);
    }
    static void sendClipboard(String mime, byte[] data) {
        if (mime == null || mime.isEmpty() || data == null || data.length == 0 || data.length > MAX_CLIP_BYTES) return;
        String sha = sha256Bytes(data);
        lastClipHash = sha;
        RelayService service = instance;
        Wire current = service == null ? null : service.wire;
        if (service == null || current == null) {
            synchronized (pendingClipLock) {
                pendingClipMime = mime; pendingClipData = data;
                pendingClipUntil = SystemClock.elapsedRealtime() + 30_000;
            }
            return;
        }
        service.lease.touch(SystemClock.elapsedRealtime());
        service.controls.execute(() -> {
            try {
                if (service.wire != current) return;
                current.send(Wire.message("clipStart").put("mime", mime).put("size", data.length).put("sha256", sha));
                int sentChunks = 0;
                for (int off = 0; off < data.length && service.wire == current; off += 3800) {
                    int n = Math.min(3800, data.length - off);
                    current.clip(data, off, n);
                    service.lastWrite = SystemClock.elapsedRealtime();
                    if (++sentChunks % 4 == 0) Thread.sleep(25);
                }
                if (service.wire == current) {
                    current.send(Wire.message("clipDone").put("sha256", sha));
                    service.lastWrite = SystemClock.elapsedRealtime();
                }
            } catch (Exception ignored) { }
        });
    }
    static void sendClipData(Context context, ClipData clip, boolean force, java.util.function.Consumer<String> onResult) {
        if (clip == null || clip.getItemCount() == 0) {
            if (onResult != null) onResult.accept("Clipboard is empty");
            return;
        }
        Context app = context.getApplicationContext();
        ClipData.Item item = clip.getItemAt(0);
        ClipDescription desc = clip.getDescription();
        android.net.Uri uri = item.getUri();
        boolean isImage = uri != null && (
            (desc != null && (desc.hasMimeType("image/*") || desc.hasMimeType("image/png") || desc.hasMimeType("image/jpeg") || desc.hasMimeType("image/webp")))
            || (app.getContentResolver().getType(uri) != null && app.getContentResolver().getType(uri).startsWith("image/"))
        );
        CharSequence textSeq = isImage ? null : item.coerceToText(app);
        new Thread(() -> {
            String msg = null;
            if (isImage) {
                byte[] png = readImageAsPng(app, uri);
                if (png == null) msg = "Could not read image (max 10 MB)";
                else {
                    String sha = sha256Bytes(png);
                    if (force || !sha.equalsIgnoreCase(lastClipHash)) {
                        sendClipboard("image/png", png);
                        msg = "Sent image to PC clipboard";
                    }
                }
            } else if (textSeq != null && textSeq.length() > 0) {
                byte[] bytes = textSeq.toString().getBytes(StandardCharsets.UTF_8);
                if (bytes.length > MAX_CLIP_BYTES) msg = "Text too large";
                else {
                    String sha = sha256Bytes(bytes);
                    if (force || !sha.equalsIgnoreCase(lastClipHash)) {
                        sendClipboard("text/plain", bytes);
                        msg = "Sent text to PC clipboard";
                    }
                }
            } else if (force) {
                msg = "Clipboard is empty";
            }
            if (onResult != null && msg != null) {
                String finalMsg = msg;
                new Handler(Looper.getMainLooper()).post(() -> onResult.accept(finalMsg));
            }
        }, "ClipSend").start();
    }
    static void sendSharedIntent(Context context, Intent intent, java.util.function.Consumer<String> onResult) {
        if (intent == null || !Intent.ACTION_SEND.equals(intent.getAction())) return;
        Context app = context.getApplicationContext();
        String type = intent.getType();
        if (type != null && type.startsWith("image/")) {
            @SuppressWarnings("deprecation")
            android.net.Uri uri = intent.getParcelableExtra(Intent.EXTRA_STREAM);
            if (uri == null) return;
            new Thread(() -> {
                byte[] png = readImageAsPng(app, uri);
                String msg;
                if (png != null) { sendClipboard("image/png", png); msg = "Sending image to PC clipboard…"; }
                else msg = "Could not read shared image";
                if (onResult != null) new Handler(Looper.getMainLooper()).post(() -> onResult.accept(msg));
            }, "ClipShare").start();
        } else {
            CharSequence text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT);
            if (text == null || text.length() == 0) return;
            byte[] bytes = text.toString().getBytes(StandardCharsets.UTF_8);
            if (bytes.length <= MAX_CLIP_BYTES) {
                sendClipboard("text/plain", bytes);
                if (onResult != null) onResult.accept("Sending text to PC clipboard…");
            }
        }
    }
    private static byte[] readImageAsPng(Context context, android.net.Uri uri) {
        try {
            byte[] raw;
            try (java.io.InputStream in = context.getContentResolver().openInputStream(uri)) {
                if (in == null) return null;
                java.io.ByteArrayOutputStream buf = new java.io.ByteArrayOutputStream();
                byte[] chunk = new byte[16384]; int n;
                while ((n = in.read(chunk)) > 0) {
                    if (buf.size() + n > 24 * 1024 * 1024) return null;
                    buf.write(chunk, 0, n);
                }
                raw = buf.toByteArray();
            }
            if (raw.length == 0) return null;
            boolean isPng = raw.length >= 8
                && (raw[0] & 0xFF) == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47
                && raw[4] == 0x0D && raw[5] == 0x0A && raw[6] == 0x1A && raw[7] == 0x0A;
            if (isPng && raw.length <= MAX_CLIP_BYTES) return raw;
            android.graphics.Bitmap bmp = android.graphics.BitmapFactory.decodeByteArray(raw, 0, raw.length);
            if (bmp == null) return null;
            try {
                for (int attempt = 0; attempt < 4; attempt++) {
                    java.io.ByteArrayOutputStream pngOut = new java.io.ByteArrayOutputStream();
                    bmp.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, pngOut);
                    byte[] png = pngOut.toByteArray();
                    if (png.length <= MAX_CLIP_BYTES) return png;
                    int nw = Math.max(1, (int)(bmp.getWidth() * 0.7)), nh = Math.max(1, (int)(bmp.getHeight() * 0.7));
                    android.graphics.Bitmap scaled = android.graphics.Bitmap.createScaledBitmap(bmp, nw, nh, true);
                    if (scaled != bmp) bmp.recycle();
                    bmp = scaled;
                }
                return null;
            } finally { bmp.recycle(); }
        } catch (Exception e) { return null; }
    }
    static void command(String kind) {
        RelayService service = instance; if (service != null) service.requestCommand(kind);
    }
    private synchronized void requestCommand(String kind) {
        lease.touch(SystemClock.elapsedRealtime());
        if (kind == null || kind.isEmpty()) return;
        Wire current = wire;
        if (current == null) { pendingCommand = SessionCommands.forConnection(kind, false); pendingUntil = SystemClock.elapsedRealtime() + 30_000; }
        else sendCommand(kind, current);
    }
    private void sendCommand(String kind, Wire current) {
        controls.execute(() -> {
            try { if (wire == current) current.send(Wire.message(kind).put("seq", sequence.incrementAndGet())); }
            catch (Exception e) { closeConnection(); }
        });
    }
    static void mute(boolean value) {
        RelayService service = instance; if (service == null || service.wire == null) return;
        Wire current = service.wire;
        service.controls.execute(() -> { try { if (service.wire == current) current.send(Wire.message("mute").put("value", value)); } catch (Exception e) { service.closeConnection(); } });
    }
    private void closeConnection() { SSLSocket s = socket; if (s != null) try { s.close(); } catch (Exception ignored) { } NativePeer.Link d = directSocket; if (d != null) d.close(); NativePeer peer = internetPeer; if (peer != null) peer.close(); }
    private static String reason(Throwable error) {
        while (error.getCause() != null && error.getCause() != error) error = error.getCause();
        String message = error.getMessage(); return error.getClass().getSimpleName() + (message == null ? "" : ": " + message);
    }
    private static boolean isUsableHost(String host) { return host != null && !host.isEmpty() && !host.startsWith("169.254.") && !host.startsWith("127.") && !"0.0.0.0".equals(host) && !host.startsWith("fe80:") && !"::1".equals(host); }
    private void pause(long ms) { try { Thread.sleep(ms); } catch (InterruptedException ignored) { } }
    private static byte[] hex(String text) throws Exception {
        if (text.length() != 64 || !text.matches("[0-9a-fA-F]+")) throw new Exception("Invalid PC identity");
        byte[] bytes = new byte[text.length() / 2]; for (int i = 0; i < bytes.length; i++) bytes[i] = (byte)Integer.parseInt(text.substring(i * 2, i * 2 + 2), 16); return bytes;
    }
    private void stopClipboardWatch() {
        ClipboardManager.OnPrimaryClipChangedListener listener = clipListener;
        clipListener = null;
        if (listener != null) {
            try {
                ClipboardManager cm = (ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
                if (cm != null) cm.removePrimaryClipChangedListener(listener);
            } catch (Exception ignored) { }
        }
        java.lang.Process p = clipLogcat; clipLogcat = null;
        if (p != null) try { p.destroyForcibly(); } catch (Exception ignored) { }
    }
    private void startClipboardWatch() {
        new Handler(Looper.getMainLooper()).post(() -> {
            if (!running) return;
            ClipboardManager cm = (ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
            if (cm == null) return;
            clipListener = () -> {
                if (!running || wire == null || SystemClock.elapsedRealtime() < suppressClipReadUntil) return;
                try {
                    ClipData clip = cm.getPrimaryClip();
                    if (clip != null && clip.getItemCount() > 0) sendClipData(this, clip, false, null);
                } catch (Exception ignored) { }
            };
            cm.addPrimaryClipChangedListener(clipListener);
        });
        if (checkSelfPermission(Manifest.permission.READ_LOGS) == PackageManager.PERMISSION_GRANTED && android.provider.Settings.canDrawOverlays(this)) {
            new Thread(this::watchClipboardLogs, "RelayClipLog").start();
        }
    }
    private void watchClipboardLogs() {
        try {
            java.lang.Process p = new ProcessBuilder("logcat", "-T", "1", "ClipboardService:D", "SemClipboardService:D", "*:S").redirectErrorStream(true).start();
            clipLogcat = p;
            String pkg = getPackageName();
            try (java.io.BufferedReader reader = new java.io.BufferedReader(new java.io.InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while (running && (line = reader.readLine()) != null) {
                    String lower = line.toLowerCase(java.util.Locale.US);
                    if (!lower.contains(pkg) && !lower.contains("setprimaryclip") && !lower.contains("adddata")) continue;
                    long now = SystemClock.elapsedRealtime();
                    if (wire == null || now < suppressClipReadUntil || now - lastOverlayRead < 400) continue;
                    lastOverlayRead = now;
                    readClipboardViaOverlay();
                }
            }
        } catch (Exception ignored) { }
    }
    private void readClipboardViaOverlay() {
        if (!android.provider.Settings.canDrawOverlays(this)) return;
        try {
            startActivity(new Intent(this, ClipboardReadActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_MULTIPLE_TASK | Intent.FLAG_ACTIVITY_NO_ANIMATION));
        } catch (Exception ignored) { }
    }
    @Override public void onDestroy() {
        running = false; instance = null; stopClipboardWatch(); availableUpdateVersion = 0; availableUpdateName = ""; updateProgress = -1; updateReady = false; closeConnection(); if (discovery != null) discovery.close();
        if (connectionThread != null) connectionThread.interrupt(); controls.shutdownNow();
        if (wake != null && wake.isHeld()) wake.release(); if (multicast != null && multicast.isHeld()) multicast.release();
        if (!pairingResult && !status.startsWith("Could not start:") && !status.startsWith("Open Nightcall")) status = "Idle · microphone off"; dictation = "idle"; stopForeground(STOP_FOREGROUND_REMOVE); super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
}
