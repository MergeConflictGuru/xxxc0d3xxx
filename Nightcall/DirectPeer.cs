using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace Nightcall;

// Native ICE + DTLS + a binary data channel. No browser or media/video stack.
public sealed class DirectPeer : IDisposable
{
    public static readonly string[] DefaultServers = { "wss://0.peerjs.com/peerjs" };
    public static readonly bool Available = NativeLibrary.TryLoad("nightcall_net", Assembly.GetExecutingAssembly(), DllImportSearchPath.AssemblyDirectory, out _);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string kind, [MarshalAs(UnmanagedType.LPUTF8Str)] string session, [MarshalAs(UnmanagedType.LPUTF8Str)] string body, IntPtr data, int count);
    [DllImport("nightcall_net", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr nightcall_net_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, int host, [MarshalAs(UnmanagedType.LPUTF8Str)] string ca, [MarshalAs(UnmanagedType.LPUTF8Str)] string urls, Callback callback, IntPtr context);
    [DllImport("nightcall_net", CallingConvention = CallingConvention.Cdecl)] private static extern int nightcall_net_connect(IntPtr handle, int server, [MarshalAs(UnmanagedType.LPUTF8Str)] string remote, [MarshalAs(UnmanagedType.LPUTF8Str)] string session);
    [DllImport("nightcall_net", CallingConvention = CallingConvention.Cdecl)] private static extern int nightcall_net_answer(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string session, [MarshalAs(UnmanagedType.LPUTF8Str)] string certificate, [MarshalAs(UnmanagedType.LPUTF8Str)] string signature);
    [DllImport("nightcall_net", CallingConvention = CallingConvention.Cdecl)] private static extern int nightcall_net_accept(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string session, [MarshalAs(UnmanagedType.LPUTF8Str)] string sdp);
    [DllImport("nightcall_net", CallingConvention = CallingConvention.Cdecl)] private static extern int nightcall_net_send(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string session, byte[] data, int count);
    [DllImport("nightcall_net", CallingConvention = CallingConvention.Cdecl)] private static extern void nightcall_net_close(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string session);
    [DllImport("nightcall_net", CallingConvention = CallingConvention.Cdecl)] private static extern void nightcall_net_destroy(IntPtr handle);
    private readonly Callback callback;
    private readonly ConcurrentDictionary<string, DirectStream> streams = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DirectStream>> pending = new();
    private readonly TaskCompletionSource[] registered;
    private readonly Func<string, string, (string Certificate, string Signature)>? signer;
    private readonly string? pin;
    [DllImport("nightcall_net", CallingConvention = CallingConvention.Cdecl)] private static extern int nightcall_net_signal(IntPtr handle, int server, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport("nightcall_net", CallingConvention = CallingConvention.Cdecl)] private static extern int nightcall_net_set_ice(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string iceJson);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<int, ClientWebSocket> sockets = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> writers = new();
    private IntPtr handle;
    private int disposed;
    private readonly object nativeGate = new();
    private readonly Channel<(string Kind, string Session, string Body, byte[] Data)> events = Channel.CreateBounded<(string, string, string, byte[])>(1024);
    public event Action<DirectStream>? Connected;
    public event Action<string>? Traced;
    public string Status { get; private set; } = "Connecting to signaling service";
    public static string PeerId(string pcIdentity) => "nightcall-pc-" + pcIdentity;
    public DirectPeer(string id, bool host, string[] servers, string? pin = null, Func<string, string, (string Certificate, string Signature)>? signer = null, string? iceServersJson = null)
    {
        if (!Available) throw new InvalidOperationException("The native direct-connection library is missing.");
        if (servers.Length is < 1 or > 4 || servers.Any(s => !Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme != "wss")) throw new InvalidDataException("Signaling endpoints must use WSS.");
        this.pin = pin; this.signer = signer; callback = OnEvent;
        registered = servers.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        string token = Guid.NewGuid().ToString("N");
        string[] urls = servers.Select(s => s + (s.Contains('?') ? "&" : "?") + "key=peerjs&id=" + Uri.EscapeDataString(id) + "&token=" + token + "&version=1.5.5").ToArray();
        handle = nightcall_net_create(id, host ? 1 : 0, "", JsonSerializer.Serialize(urls), callback, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new IOException("Cannot create the native direct-connection engine.");
        if (!string.IsNullOrWhiteSpace(iceServersJson)) SetIceServers(iceServersJson);
        _ = Task.Run(async () => { await foreach (var e in events.Reader.ReadAllAsync()) { if (handle != IntPtr.Zero) ProcessEvent(e.Kind, e.Session, e.Body, e.Data); } });
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        for (int i = 0; i < urls.Length; i++) { int serverIndex = i; _ = Task.Run(() => SignalLoop(serverIndex, urls[serverIndex])); }
    }
    private void OnEvent(IntPtr context, string kind, string session, string body, IntPtr data, int count)
    {
        if (count < 0 || count > 8192) return;
        var packet = new byte[count]; if (count > 0) Marshal.Copy(data, packet, 0, count);
        if (!events.Writer.TryWrite((kind, session, body, packet))) _ = Task.Run(Dispose);
    }
    private void ProcessEvent(string kind, string session, string body, byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(body); var message = doc.RootElement;
            if (kind == "registered") { int index = message.GetProperty("server").GetInt32(); if ((uint)index < registered.Length) registered[index].TrySetResult(); Status = "Internet discovery ready"; Traced?.Invoke("peerjs-registered"); }
            else if (kind == "sign" && signer != null)
            {
                Traced?.Invoke("offer-rx/sign sid=" + session[..Math.Min(6, session.Length)]);
                var signed = signer(session, message.GetProperty("sdp").GetString()!);
                lock (nativeGate) { if (handle != IntPtr.Zero && nightcall_net_answer(handle, session, signed.Certificate, signed.Signature) != 0) Close(session); }
            }
            else if (kind == "verify")
            {
                string sdp = message.GetProperty("sdp").GetProperty("sdp").GetString()!;
                if (pin == null || !DirectIdentity.Verify(pin, session, sdp, message.GetProperty("certificate").GetString()!, message.GetProperty("signature").GetString()!)) { Close(session); return; }
                lock (nativeGate) { if (handle != IntPtr.Zero && nightcall_net_accept(handle, session, sdp) != 0) Close(session); }
            }
            else if (kind == "open")
            {
                var stream = new DirectStream(bytes => { lock (nativeGate) { if (handle == IntPtr.Zero || nightcall_net_send(handle, session, bytes, bytes.Length) != 0) throw new IOException("Direct connection stopped or is congested."); } }, () => Close(session));
                if (!streams.TryAdd(session, stream)) { stream.Dispose(); return; }
                Traced?.Invoke("ice-open sid=" + session[..Math.Min(6, session.Length)]); if (pending.TryRemove(session, out var waiter)) waiter.TrySetResult(stream); else Connected?.Invoke(stream);
            }
            else if (kind == "packet" && streams.TryGetValue(session, out var stream) && data.Length is > 0 and <= 8192) { stream.Feed(data); }
            else if (kind == "closed" || kind == "error" && session.Length > 0) { Traced?.Invoke("ice-" + kind + " " + body); Close(session); }
            else if (kind == "signal-send") _ = SendSignal(message.GetProperty("server").GetInt32(), message.GetProperty("message").GetString()!);
            else if (kind == "server-error") { Status = "Internet discovery reconnecting"; Traced?.Invoke("peerjs-server-error " + body); }
        }
        catch (Exception e) { Status = "Direct connection: " + e.Message; if (session.Length > 0) Close(session); }
    }
    public void SetIceServers(string? iceServersJson)
    {
        lock (nativeGate) { if (handle != IntPtr.Zero) nightcall_net_set_ice(handle, string.IsNullOrWhiteSpace(iceServersJson) ? "[]" : iceServersJson); }
    }
    private void OnNetworkChanged(object? sender, EventArgs e) { Traced?.Invoke("net-changed"); foreach (var socket in sockets.Values) try { socket.Abort(); } catch { } }
    private void FeedSignal(int server, string text)
    {
        lock (nativeGate) { if (handle != IntPtr.Zero) nightcall_net_signal(handle, server, text); }
    }
    private async Task SendSignal(int server, string text)
    {
        var writer = writers.GetOrAdd(server, _ => new SemaphoreSlim(1));
        try
        {
            await writer.WaitAsync(stopping.Token);
            try
            {
                if (!sockets.TryGetValue(server, out var ws) || ws.State != WebSocketState.Open) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await ws.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            }
            finally { writer.Release(); }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { Status = "Internet discovery reconnecting"; if (sockets.TryGetValue(server, out var dead)) try { dead.Abort(); } catch { } }
    }
    private async Task Heartbeat(int server, ClientWebSocket socket)
    {
        try { while (!stopping.IsCancellationRequested && socket.State == WebSocketState.Open) { await Task.Delay(5000, stopping.Token); await SendSignal(server, "{\"type\":\"HEARTBEAT\"}"); } }
        catch (OperationCanceledException) { }
    }
    private async Task SignalLoop(int server, string url)
    {
        while (!stopping.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket(); ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(5); ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(8); sockets[server] = ws;
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token); connect.CancelAfter(TimeSpan.FromSeconds(12));
                Traced?.Invoke("ws-connecting"); await ws.ConnectAsync(new Uri(url), connect.Token); Traced?.Invoke("ws-open");
                _ = Heartbeat(server, ws);
                var bytes = new byte[8192];
                while (ws.State == WebSocketState.Open && !stopping.IsCancellationRequested)
                {
                    using var message = new MemoryStream(); ValueWebSocketReceiveResult frame;
                    do
                    {
                        frame = await ws.ReceiveAsync(bytes.AsMemory(), stopping.Token);
                        if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Signaling socket closed");
                        if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 131072) throw new IOException("Invalid signaling frame");
                        message.Write(bytes, 0, frame.Count);
                    } while (!frame.EndOfMessage);
                    FeedSignal(server, Encoding.UTF8.GetString(message.ToArray()));
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { Traced?.Invoke("ws-drop " + e.GetType().Name + ": " + e.Message); }
            FeedSignal(server, "{\"type\":\"ERROR\"}");
            try { await Task.Delay(1000, stopping.Token); } catch (OperationCanceledException) { return; }
        }
    }
    public async Task<DirectStream> Connect(string pcId, CancellationToken cancel)
    {
        foreach (var (ready, index) in registered.Select((r, i) => (r, i)))
        {
            string session = Guid.NewGuid().ToString("N");
            var result = new TaskCompletionSource<DirectStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(8), cancel);
                pending[session] = result;
                lock (nativeGate) { if (handle == IntPtr.Zero || nightcall_net_connect(handle, index, PeerId(pcId), session) != 0) throw new IOException("Signaling server is not ready."); }
                return await result.Task.WaitAsync(TimeSpan.FromSeconds(25), cancel);
            }
            catch (Exception e) when (e is TimeoutException or IOException) { Close(session); }
        }
        throw new IOException("No direct internet path could be established. No relay was used.");
    }
    private void Close(string session)
    {
        if (streams.TryRemove(session, out var stream)) stream.Dispose();
        if (pending.TryRemove(session, out var waiting)) waiting.TrySetException(new IOException("Direct connection closed."));
        lock (nativeGate) { if (handle != IntPtr.Zero) nightcall_net_close(handle, session); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        stopping.Cancel(); foreach (var socket in sockets.Values) socket.Abort();
        lock (nativeGate) { IntPtr old = handle; handle = IntPtr.Zero; if (old != IntPtr.Zero) nightcall_net_destroy(old); }
        events.Writer.TryComplete();
        foreach (var stream in streams.Values) stream.Dispose(); streams.Clear();
        foreach (var waiting in pending.Values) waiting.TrySetCanceled(); pending.Clear();
        GC.KeepAlive(callback);
    }
}
