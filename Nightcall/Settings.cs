using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nightcall;

public sealed class Settings
{
    public uint Modifiers { get; set; } = 3; // Ctrl + Alt
    public uint Key { get; set; } = 0x20;
    public string AuthFile { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
    public string Language { get; set; } = "";
    public string[] SignalingServers { get; set; } = DirectPeer.DefaultServers;
    public string CloudflareTurnKeyId { get; set; } = "";
    public string CloudflareTurnApiToken { get; set; } = "";
    public string TurnUrl { get; set; } = "";
    public string TurnUsername { get; set; } = "";
    public string TurnCredential { get; set; } = "";
    private static readonly HttpClient turnHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    public async Task<string> ResolveIceServersJson(CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(CloudflareTurnKeyId) && !string.IsNullOrWhiteSpace(CloudflareTurnApiToken))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://rtc.live.cloudflare.com/v1/turn/keys/{Uri.EscapeDataString(CloudflareTurnKeyId.Trim())}/credentials/generate-ice-servers");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudflareTurnApiToken.Trim());
            req.Content = new StringContent("{\"ttl\":86400}", System.Text.Encoding.UTF8, "application/json");
            using var resp = await turnHttp.SendAsync(req, cancel);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancel));
            if (doc.RootElement.TryGetProperty("iceServers", out var servers) && servers.ValueKind == JsonValueKind.Array)
            {
                var filtered = new System.Collections.Generic.List<object>();
                foreach (var entry in servers.EnumerateArray())
                {
                    if (!entry.TryGetProperty("urls", out var urlsProp)) continue;
                    var udpUrls = new System.Collections.Generic.List<string>();
                    if (urlsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var u in urlsProp.EnumerateArray())
                            if (u.GetString() is string s && s.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) && !s.Contains("transport=tcp", StringComparison.OrdinalIgnoreCase))
                                udpUrls.Add(s);
                    }
                    else if (urlsProp.ValueKind == JsonValueKind.String && urlsProp.GetString() is string s && s.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) && !s.Contains("transport=tcp", StringComparison.OrdinalIgnoreCase))
                    {
                        udpUrls.Add(s);
                    }
                    if (udpUrls.Count == 0) continue;
                    filtered.Add(new
                    {
                        urls = udpUrls,
                        username = entry.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
                        credential = entry.TryGetProperty("credential", out var cr) ? cr.GetString() ?? "" : ""
                    });
                }
                return JsonSerializer.Serialize(filtered);
            }
        }
        if (!string.IsNullOrWhiteSpace(TurnUrl))
            return JsonSerializer.Serialize(new[] { new { urls = new[] { TurnUrl.Trim() }, username = TurnUsername.Trim(), credential = TurnCredential } });
        return "[]";
    }
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    public static Settings Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();
    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    public string Shortcut => ((Modifiers & 2) != 0 ? "Ctrl + " : "") + ((Modifiers & 1) != 0 ? "Alt + " : "") +
        ((Modifiers & 4) != 0 ? "Shift + " : "") + ((Modifiers & 8) != 0 ? "Win + " : "") +
        System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)Key);
}

public sealed class Transcriber : IDisposable
{
    public Task<string> Transcribe(byte[] wav, Settings settings, CancellationToken cancel) => Task.Run(() =>
    {
        cancel.ThrowIfCancellationRequested();
        if (!File.Exists(settings.AuthFile)) throw new InvalidOperationException("Choose your Codex auth.json in Settings, or sign in to Codex first.");
        using var auth = JsonDocument.Parse(File.ReadAllText(settings.AuthFile).TrimStart('\uFEFF'));
        if (!auth.RootElement.TryGetProperty("tokens", out var tokens) ||
            !tokens.TryGetProperty("access_token", out var access) || string.IsNullOrEmpty(access.GetString()))
            throw new InvalidOperationException("This login file has no ChatGPT access token. Sign in to Codex with ChatGPT.");
        string boundary = "----dictation-" + Guid.NewGuid().ToString("N");
        using var body = new MemoryStream();
        void Write(string text) { var bytes = System.Text.Encoding.UTF8.GetBytes(text); body.Write(bytes); }
        Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"dictation.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
        body.Write(wav); Write("\r\n");
        if (!string.IsNullOrWhiteSpace(settings.Language))
            Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"language\"\r\n\r\n{settings.Language.Trim()}\r\n");
        Write($"--{boundary}--\r\n");
        // Windows' own HTTP transport matches the desktop's connectivity on this PC.
        // Credentials are sent only to the fixed OpenAI endpoint; redirects are disabled.
        dynamic http = Activator.CreateInstance(Type.GetTypeFromProgID("WinHttp.WinHttpRequest.5.1")!)!;
        try
        {
            http.Option[6] = false;
            http.SetTimeouts(10000, 10000, 30000, 90000);
            http.Open("POST", "https://chatgpt.com/backend-api/transcribe", false);
            http.SetRequestHeader("Authorization", "Bearer " + access.GetString());
            if (tokens.TryGetProperty("account_id", out var account)) http.SetRequestHeader("ChatGPT-Account-Id", account.GetString());
            http.SetRequestHeader("User-Agent", "codex_cli_rs/0.162.0");
            http.SetRequestHeader("originator", "codex_cli_rs");
            http.SetRequestHeader("Content-Type", "multipart/form-data; boundary=" + boundary);
            using var registration = cancel.Register(() => { try { http.Abort(); } catch { } });
            http.Send(body.ToArray());
            cancel.ThrowIfCancellationRequested();
            int status = http.Status;
            if (status < 200 || status >= 300)
                throw new InvalidOperationException(status switch
                {
                    401 => "Your login expired. Sign in to Codex again, then retry.",
                    403 => "The transcription service rejected the request. Check the login in Settings or retry.",
                    429 => "Transcription is rate limited. Wait a moment and retry.",
                    _ => $"Transcription service returned HTTP {status}. Your recording is kept in memory for Retry."
                });
            return TranscriptResponse.Parse((byte[])http.ResponseBody);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(http); }
    }, cancel);
    public void Dispose() { }
}
