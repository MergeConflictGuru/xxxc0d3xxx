using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;

namespace Nightcall;

// A deliberately small download endpoint: only the bundled APK, never arbitrary workspace files.
public sealed class InstallServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly string apk;
    private readonly string route = "/" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "/Nightcall.apk";
    private readonly SemaphoreSlim slots = new(8);
    public sealed record Address(string Network, string Url);
    public Address[] Addresses => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .OrderByDescending(x => x.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
        .SelectMany(x => x.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => new Address(x.Name, $"http://{a.Address}:{((IPEndPoint)listener.LocalEndpoint).Port}{route}")))
        .DistinctBy(x => x.Url).ToArray();
    public string Url => Addresses.FirstOrDefault()?.Url ?? $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{route}";
    public InstallServer(string apk, int port = 47830)
    {
        this.apk = Path.GetFullPath(apk);
        if (!File.Exists(apk)) throw new FileNotFoundException("Build the Android APK first (build.ps1 -Android).", apk);
        listener = new TcpListener(IPAddress.Any, port);
        try { listener.Start(); }
        catch (SocketException) when (port != 0) { listener = new TcpListener(IPAddress.Any, 0); listener.Start(); }
        _ = Accept();
    }
    public static byte[] Qr(string value)
    {
        using var generator = new QRCodeGenerator(); using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data); return code.GetGraphic(8);
    }
    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                _ = Serve(client);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var request = new MemoryStream(); byte[] one = new byte[1]; int tail = 0;
                while (request.Length < 8192 && tail != 0x0d0a0d0a)
                {
                    await stream.ReadExactlyAsync(one, deadline.Token); request.WriteByte(one[0]); tail = (tail << 8) | one[0];
                }
                if (tail != 0x0d0a0d0a) return;
                string[] lines = Encoding.ASCII.GetString(request.ToArray()).Split("\r\n");
                string[] words = lines[0].Split(' ');
                bool valid = words.Length == 3 && words[1] == route && (words[0] == "GET" || words[0] == "HEAD");
                if (!valid) { await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token); return; }
                await using var file = new FileStream(apk, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                long total = file.Length, start = 0, end = total - 1;
                bool partial = false;
                string? range = lines.FirstOrDefault(h => h.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
                if (range != null && total > 0 && range.Contains("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = range[(range.IndexOf("bytes=", StringComparison.OrdinalIgnoreCase) + 6)..].Split(',')[0].Trim();
                    string[] bounds = spec.Split('-');
                    if (bounds.Length == 2 && long.TryParse(bounds[0], out long parsedStart) && parsedStart >= 0 && parsedStart < total)
                    {
                        start = parsedStart;
                        if (!string.IsNullOrEmpty(bounds[1]) && long.TryParse(bounds[1], out long parsedEnd) && parsedEnd >= start && parsedEnd < total) end = parsedEnd;
                        partial = true;
                    }
                }
                long length = total == 0 ? 0 : end - start + 1;
                string statusLine = partial ? "HTTP/1.1 206 Partial Content" : "HTTP/1.1 200 OK";
                string rangeHeader = partial ? $"Content-Range: bytes {start}-{end}/{total}\r\n" : "";
                string header = $"{statusLine}\r\nContent-Type: application/vnd.android.package-archive\r\nContent-Disposition: attachment; filename=\"Nightcall.apk\"\r\nAccept-Ranges: bytes\r\n{rangeHeader}Content-Length: {length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), deadline.Token);
                if (words[0] == "GET" && length > 0)
                {
                    file.Position = start;
                    byte[] buffer = new byte[65536];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), deadline.Token);
                        if (read <= 0) break;
                        deadline.CancelAfter(TimeSpan.FromSeconds(30));
                        await stream.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                        remaining -= read;
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
            finally { slots.Release(); }
        }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
