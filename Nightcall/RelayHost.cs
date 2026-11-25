using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nightcall;

public sealed class RelayHost : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim slots = new(8);
    private readonly int port;
    private readonly string? storePath;
    private readonly bool advertise;
    private PairingStore? store;
    private X509Certificate2? certificate;
    private TcpListener? listener;
    private IDisposable? active;
    private DirectPeer? directPeer;
    public string[] SignalingServers { get; set; } = DirectPeer.DefaultServers;
    public string IceServersJson { get; set; } = "[]";
    public Func<CancellationToken, Task<string>>? IceServersProvider { get; set; }
    public string? ApkPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Nightcall.apk");
    private (string Path, DateTime WriteUtc, long Length, long Version, string Name, string Sha256)? cachedApk;
    public string IdentityId => store?.Id ?? throw new InvalidOperationException("PC identity is not ready.");
    private string phoneName = "", pairingToken = "", state = "idle";
    private DateTime pairingExpiry;
    private bool muted;
    private long revision;
    private readonly Queue<string> traceLog = new();
    private string phoneTrace = "";
    public event Action<byte[]>? Audio;
    public event Action<byte[]>? StereoAudio;
    public event Action<string>? Command;
    public event Action? Changed;
    public bool Connected { get { lock (gate) return active != null; } }
    public string PhoneName { get { lock (gate) return phoneName; } }
    public string Status { get; private set; } = "Starting phone relay…";
    public string TraceText { get { lock (gate) return "=== PC ===" + Environment.NewLine + string.Join(Environment.NewLine, traceLog) + (string.IsNullOrEmpty(phoneTrace) ? "" : Environment.NewLine + Environment.NewLine + "=== Phone ===" + Environment.NewLine + phoneTrace); } }
    public void Trace(string msg) { lock (gate) { traceLog.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg); while (traceLog.Count > 50) traceLog.Dequeue(); } }
    public bool Muted { get { lock (gate) return muted; } set { lock (gate) { muted = value; revision++; } Changed?.Invoke(); } }
    public RelayHost(int port = RelayProtocol.Port, string? storePath = null, bool advertise = true) { this.port = port; this.storePath = storePath; this.advertise = advertise; }
    public void SetState(string value) { lock (gate) { state = value; revision++; } }
    public Task Start() => Task.Run(() =>
    {
        try
        {
            store = PairingStore.Load(storePath);
            try
            {
                certificate = store.OpenCertificate();
                stop.Token.ThrowIfCancellationRequested();
                listener = new TcpListener(IPAddress.Any, port); listener.Start();
                Status = "PC microphone - waiting for a paired phone";
                _ = AcceptLoop(); if (advertise) _ = Advertise();
            }
            catch (Exception e) when (!stop.IsCancellationRequested) { Status = "Local phone connection unavailable: " + e.Message; }

            if (DirectPeer.Available)
            {
                try
                {
                    RefreshIceServers().GetAwaiter().GetResult();
                    directPeer = new DirectPeer(DirectPeer.PeerId(store.Id), true, SignalingServers, signer: (session, sdp) => DirectIdentity.Sign(store, session, sdp), iceServersJson: IceServersJson);
                    directPeer.Traced += Trace; directPeer.Connected += stream => { _ = ServeDirect(stream); };
                    if (IceServersProvider != null) _ = RefreshIceLoop();
                }
                catch { }
            }
            Changed?.Invoke();
        }
        catch (Exception e) when (!stop.IsCancellationRequested) { Status = "Phone relay unavailable: " + e.Message; Changed?.Invoke(); }
    });
    public async Task RefreshIceServers()
    {
        if (IceServersProvider == null) { directPeer?.SetIceServers(IceServersJson); return; }
        try
        {
            string resolved = await IceServersProvider(stop.Token);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                IceServersJson = resolved; Trace("ice-resolved len=" + resolved.Length);
                directPeer?.SetIceServers(resolved);
            }
        }
        catch { }
    }
    private async Task RefreshIceLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromHours(6), stop.Token);
                await RefreshIceServers();
            }
        }
        catch (OperationCanceledException) { }
    }
    public Task ServeDirect(DirectStream stream)
    {
        if (!slots.Wait(0)) { stream.Dispose(); return Task.CompletedTask; }
        return ServeEncrypted(stream, stream, "internet");
    }
    public string CreatePairingUri()
    {
        lock (gate)
        {
            if (store == null || listener == null && directPeer == null) throw new InvalidOperationException(Status);
            pairingToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); pairingExpiry = DateTime.UtcNow.AddMinutes(2);
            var ice = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(IceServersJson) ? "[]" : IceServersJson);
            string data = JsonSerializer.Serialize(new { v = 1, id = store.Id, name = Environment.MachineName,
                hosts = Addresses().Select(x => x.Address.ToString()).Distinct().ToArray(), port,
                pin = IdentityPin(), token = pairingToken, servers = SignalingServers, ice }, RelayProtocol.Options);
            return "dictationrelay://pair?data=" + Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(data)));
        }
    }
    public string IdentityPin()
    {
        if (store == null) throw new InvalidOperationException("PC identity is not ready.");
        using var identity = DirectIdentity.Open(store);
        return Convert.ToHexStringLower(SHA256.HashData(identity.Certificate.RawData));
    }
    public bool PairingActive { get { lock (gate) return pairingToken.Length > 0 && DateTime.UtcNow < pairingExpiry; } }
    public void EndPairing() { lock (gate) pairingToken = ""; }
    public PairedPhone[] PairedPhones { get { lock (gate) return store?.Phones.ToArray() ?? Array.Empty<PairedPhone>(); } }
    public void Forget(string id)
    {
        lock (gate)
        {
            if (store == null) return;
            var previous = store.Phones.ToArray(); store.Phones.RemoveAll(x => x.Id == id);
            try { store.Save(storePath); } catch { store.Phones = previous.ToList(); throw; }
            // Reauthenticate every open session after revocation.
            active?.Dispose();
        }
        Changed?.Invoke();
    }
    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(stop.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                _ = Serve(client);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch (Exception e) { Status = "Phone listener stopped: " + e.Message; Changed?.Invoke(); }
    }
    private async Task Serve(TcpClient client)
    {
        bool delegated = false;
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                client.NoDelay = true; timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
                delegated = true; await ServeEncrypted(tls, client, "Wi-Fi");
            }
            catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException) { }
            finally { if (!delegated) slots.Release(); }
        }
    }
    private async Task ServeEncrypted(Stream tls, IDisposable connection, string path)
    {
        bool claimed = false;
        using var opus = new OpusCodec();
        using (connection)
        using (var life = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                life.CancelAfter(TimeSpan.FromSeconds(10));
                var frame = await RelayProtocol.Read(tls, life.Token);
                if (frame.Type != RelayProtocol.Json) return;
                using var hello = JsonDocument.Parse(frame.Data);
                var root = hello.RootElement;
                string kind = root.GetProperty("kind").GetString() ?? "", id = root.GetProperty("id").GetString() ?? "";
                if (!Guid.TryParseExact(id, "N", out _)) return;
                PairedPhone? phone;
                lock (gate)
                {
                    if (kind == "pair")
                    {
                        string token = root.GetProperty("token").GetString() ?? "";
                        if (pairingToken.Length == 0 || DateTime.UtcNow > pairingExpiry || !Same(token, pairingToken)) return;
                        string name = root.GetProperty("name").GetString() ?? "Phone";
                        name = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray());
                        phone = new PairedPhone(id, name, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
                        var previous = store!.Phones.ToArray();
                        store.Phones.RemoveAll(x => x.Id == id); store.Phones.Add(phone);
                        try { store.Save(storePath); }
                        catch { store.Phones = previous.ToList(); throw; }
                        pairingToken = "";
                    }
                    else
                    {
                        phone = store!.Phones.Find(x => x.Id == id);
                        if (kind != "auth" || phone == null || !Same(root.GetProperty("secret").GetString() ?? "", phone.Secret)) return;
                    }
                }
                if (kind == "pair")
                {
                    await RelayProtocol.Send(tls, new { kind = "paired", secret = phone.Secret }, life.Token);
                    Changed?.Invoke(); return; // Next connection proves that the phone saved its credentials.
                }
                lock (gate)
                {
                    if (active == null) { active = connection; phoneName = phone.Name; claimed = true; }
                }
                if (!claimed) { await RelayProtocol.Send(tls, new { kind = "busy" }, life.Token); return; }
                Trace("session-ready path=" + path); Status = "Phone microphone - " + phone.Name + " (" + path + ")"; Changed?.Invoke();
                using var writeGate = new SemaphoreSlim(1, 1);
                var apk = ReadApkInfo();
                var ice = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(IceServersJson) ? "[]" : IceServersJson); await SendLocked(tls, writeGate, new { kind = "ready", rate = RelayProtocol.Rate, codec = OpusCodec.Available ? "opus" : "pcm16", bitrate = OpusCodec.Bitrate, opusRate = 48000, opusChannels = 2, frameMs = 20, servers = SignalingServers, ice, apkVersion = apk?.Version ?? 0, apkVersionName = apk?.Name ?? "", apkSize = apk?.Size ?? 0, apkSha256 = apk?.Sha256 ?? "" }, life.Token);
                life.CancelAfter(TimeSpan.FromSeconds(8));
                var updates = SendUpdates(tls, writeGate, life.Token);
                Task? apkTransfer = null;
                SemaphoreSlim? apkCredit = null;
                try
                {
                    long lastCommand = -1;
                    while (!life.IsCancellationRequested)
                    {
                        frame = await RelayProtocol.Read(tls, life.Token);
                        life.CancelAfter(TimeSpan.FromSeconds(8));
                        if (frame.Type == RelayProtocol.Audio)
                        {
                            if (frame.Data.Length > 3200 || (frame.Data.Length & 1) != 0) throw new InvalidDataException("Invalid PCM packet.");
                            if (!Muted) { Audio?.Invoke(frame.Data); StereoAudio?.Invoke(AudioChannels.Stereo48(frame.Data)); }
                        }
                        else if (frame.Type == RelayProtocol.Opus)
                        {
                            byte[] decoded = opus.Decode(frame.Data), mono = opus.DecodeMono(frame.Data);
                            if (!Muted) { Audio?.Invoke(mono); StereoAudio?.Invoke(decoded); }
                        }
                        else
                        {
                            using var message = JsonDocument.Parse(frame.Data);
                            var r = message.RootElement;
                            string command = r.GetProperty("kind").GetString() ?? "";
                            if (command is "toggle" or "start" or "finish" or "finishSend" or "cancel")
                            {
                                long sequence = r.GetProperty("seq").GetInt64();
                                if (sequence > lastCommand) { lastCommand = sequence; Command?.Invoke(command); }
                            }
                            else if (command == "mute") Muted = r.GetProperty("value").GetBoolean();
                            else if (command == "trace") { lock (gate) phoneTrace = r.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : ""; }
                            else if (command == "getApk" && (apkTransfer == null || apkTransfer.IsCompleted))
                            {
                                long offset = r.TryGetProperty("offset", out var offProp) && offProp.TryGetInt64(out long o) && o >= 0 ? o : 0;
                                string expectedSha = r.TryGetProperty("sha256", out var shaProp) ? shaProp.GetString() ?? "" : "";
                                apkCredit?.Dispose();
                                apkCredit = new SemaphoreSlim(12);
                                apkTransfer = SendApk(tls, writeGate, apkCredit, offset, expectedSha, life.Token);
                            }
                            else if (command == "apkAck" && apkCredit != null)
                            {
                                int count = r.TryGetProperty("count", out var cntProp) && cntProp.TryGetInt32(out int c) && c is > 0 and <= 16 ? c : 4;
                                try { apkCredit.Release(count); } catch (SemaphoreFullException) { }
                            }
                        }
                    }
                }
                finally { life.Cancel(); try { await updates; } catch (OperationCanceledException) { } if (apkTransfer != null) try { await apkTransfer; } catch { } apkCredit?.Dispose(); }
            }
            catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { Trace("session-end path=" + path + " " + e.GetType().Name + ": " + e.Message); }
            finally
            {
                if (claimed)
                {
                    lock (gate) { if (ReferenceEquals(active, connection)) { active = null; phoneName = ""; } }
                    Status = "PC microphone · waiting for a paired phone"; Changed?.Invoke();
                }
                slots.Release();
            }
        }
    }
    private static async Task SendLocked(Stream stream, SemaphoreSlim writeGate, object message, CancellationToken cancel)
    {
        await writeGate.WaitAsync(cancel);
        try { await RelayProtocol.Send(stream, message, cancel); }
        finally { writeGate.Release(); }
    }
    private async Task SendUpdates(Stream stream, SemaphoreSlim writeGate, CancellationToken cancel)
    {
        long previous = -1; int ticks = 0;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                object update; long current;
                lock (gate) { current = revision; update = new { kind = "state", state, muted, phone = phoneName }; }
                if (current != previous || ticks++ % 8 == 0) { await SendLocked(stream, writeGate, update, cancel); previous = current; }
                await Task.Delay(250, cancel);
            }
        }
        catch { stream.Dispose(); throw; }
    }
    private async Task SendApk(Stream stream, SemaphoreSlim writeGate, SemaphoreSlim credit, long offset, string expectedSha, CancellationToken cancel)
    {
        try
        {
            var info = ReadApkInfo();
            if (info == null || ApkPath == null) { await SendLocked(stream, writeGate, new { kind = "apkError", message = "PC does not have Nightcall.apk." }, cancel); return; }
            await using var file = new FileStream(ApkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            long start = offset > 0 && offset <= file.Length && (string.IsNullOrEmpty(expectedSha) || string.Equals(expectedSha, info.Value.Sha256, StringComparison.OrdinalIgnoreCase)) ? offset : 0;
            file.Position = start;
            await SendLocked(stream, writeGate, new { kind = "apkMeta", size = info.Value.Size, sha256 = info.Value.Sha256, offset = start }, cancel);
            byte[] buffer = new byte[3800];
            int read;
            while ((read = await file.ReadAsync(buffer, cancel)) > 0)
            {
                await credit.WaitAsync(cancel);
                byte[] chunk = read == buffer.Length ? buffer : buffer[..read];
                await writeGate.WaitAsync(cancel);
                try { await RelayProtocol.Write(stream, RelayProtocol.Apk, chunk, cancel); }
                finally { writeGate.Release(); }
            }
            await SendLocked(stream, writeGate, new { kind = "apkDone", sha256 = info.Value.Sha256 }, cancel);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { try { await SendLocked(stream, writeGate, new { kind = "apkError", message = e.Message }, cancel); } catch { } }
    }
    private (long Version, string Name, long Size, string Sha256)? ReadApkInfo()
    {
        try
        {
            if (string.IsNullOrEmpty(ApkPath) || !File.Exists(ApkPath)) return null;
            var fi = new FileInfo(ApkPath);
            lock (gate)
            {
                if (cachedApk is { } c && c.Path == ApkPath && c.WriteUtc == fi.LastWriteTimeUtc && c.Length == fi.Length)
                    return (c.Version, c.Name, c.Length, c.Sha256);
            }
            using var zip = ZipFile.OpenRead(ApkPath);
            var manifest = zip.GetEntry("AndroidManifest.xml");
            if (manifest == null) return null;
            using var ms = new MemoryStream();
            using (var es = manifest.Open()) es.CopyTo(ms);
            if (!TryParseManifestVersion(ms.ToArray(), out long version, out string name)) return null;
            using var fs = new FileStream(ApkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string sha = Convert.ToHexStringLower(SHA256.HashData(fs));
            lock (gate) cachedApk = (ApkPath, fi.LastWriteTimeUtc, fi.Length, version, name, sha);
            return (version, name, fi.Length, sha);
        }
        catch { return null; }
    }
    private static bool TryParseManifestVersion(byte[] data, out long versionCode, out string versionName)
    {
        versionCode = 0; versionName = "";
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x00080003) return false;
        int pos = 8;
        string[] strings = Array.Empty<string>();
        while (pos + 8 <= data.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
            if (size < 8 || pos + size > data.Length) break;
            if (type == 0x0001 && pos + 28 <= data.Length)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 8));
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 16));
                int stringsStart = pos + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 20));
                bool utf8 = (flags & 0x100) != 0;
                strings = new string[count];
                for (int i = 0; i < count; i++)
                {
                    int off = stringsStart + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 28 + i * 4));
                    if (off < 0 || off + 2 > data.Length) { strings[i] = ""; continue; }
                    if (utf8)
                    {
                        int p = off; if ((data[p++] & 0x80) != 0) p++;
                        int byteLen = data[p++]; if ((byteLen & 0x80) != 0) byteLen = ((byteLen & 0x7F) << 8) | data[p++];
                        strings[i] = p + byteLen <= data.Length ? Encoding.UTF8.GetString(data, p, byteLen) : "";
                    }
                    else
                    {
                        int charLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off));
                        int p = off + 2; if ((charLen & 0x8000) != 0) { charLen = ((charLen & 0x7FFF) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)); p += 2; }
                        strings[i] = p + charLen * 2 <= data.Length ? Encoding.Unicode.GetString(data, p, charLen * 2) : "";
                    }
                }
            }
            else if (type == 0x0102 && pos + 36 <= data.Length)
            {
                int nameIdx = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 20));
                if ((uint)nameIdx < strings.Length && strings[nameIdx] == "manifest")
                {
                    int attrCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 28));
                    int attrPos = pos + 36;
                    for (int i = 0; i < attrCount && attrPos + 20 <= pos + size; i++, attrPos += 20)
                    {
                        int attrName = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(attrPos + 4));
                        int valStr = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(attrPos + 8));
                        byte valType = data[attrPos + 15];
                        uint valData = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(attrPos + 16));
                        string key = (uint)attrName < strings.Length ? strings[attrName] : "";
                        if (key == "versionCode" && valType == 0x10) versionCode = valData;
                        else if (key == "versionName" && valType == 0x03 && (uint)valStr < strings.Length) versionName = strings[valStr];
                    }
                    return versionCode > 0;
                }
            }
            pos += size;
        }
        return false;
    }
    public static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static bool IsLinkLocal(IPAddress address) { byte[] b = address.GetAddressBytes(); return b.Length == 4 && b[0] == 169 && b[1] == 254; }

    private static IEnumerable<UnicastIPAddressInformation> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .OrderByDescending(x => x.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
        .SelectMany(x => x.GetIPProperties().UnicastAddresses)
        .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(x.Address));
    private async Task Advertise()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        try
        {
            while (!stop.IsCancellationRequested)
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { app = "dictation-relay", v = 1, id = store!.Id, port }, RelayProtocol.Options);
                var destinations = new HashSet<IPAddress> { IPAddress.Broadcast };
                foreach (var ip in Addresses())
                {
                    byte[] address = ip.Address.GetAddressBytes(), mask = ip.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4 || mask.All(b => b == 0)) continue;
                    for (int i = 0; i < 4; i++) address[i] |= (byte)~mask[i];
                    destinations.Add(new IPAddress(address));
                }
                foreach (var address in destinations)
                    try { await udp.SendAsync(bytes, new IPEndPoint(address, RelayProtocol.DiscoveryPort), stop.Token); }
                    catch (SocketException) { /* An adapter may disappear during a Wi-Fi transition. */ }
                await Task.Delay(1000, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { stop.Cancel(); directPeer?.Dispose(); listener?.Stop(); lock (gate) active?.Dispose(); EndPairing(); }
}
