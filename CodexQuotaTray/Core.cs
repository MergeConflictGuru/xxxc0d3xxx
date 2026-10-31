using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaTray;

static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static JsonElement Get(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    public static string? Text(this JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    public static double? Number(this JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) ? n : null;
}

record Identity(string Key, string Email, string Name)
{
    public static Identity Read(string path)
    {
        return FromContents(File.ReadAllText(path));
    }
    public static Identity FromContents(string contents)
    {
        using var doc = JsonDocument.Parse(contents);
        var tokens = doc.RootElement.Get("tokens");
        var token = tokens.Get("id_token").Text() ?? throw new InvalidOperationException("ChatGPT login required: auth.json has no ID token.");
        var pieces = token.Split('.');
        if (pieces.Length != 3) throw new InvalidOperationException("Invalid login token.");
        var encoded = pieces[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
        var root = claims.RootElement;
        var email = root.Get("email").Text() ?? root.Get("https://api.openai.com/profile").Get("email").Text();
        if (string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("This login has no email address.");
        var id = tokens.Get("account_id").Text() ?? root.Get("https://api.openai.com/auth").Get("chatgpt_account_id").Text() ?? root.Get("sub").Text() ?? email;
        return new Identity(email.ToLowerInvariant() + "|" + id, email, root.Get("name").Text() ?? email.Split('@')[0]);
    }
    public string Suffix => Uri.EscapeDataString(Email).Replace("%40", "@");
}

record QuotaWindow(double Used, int Minutes, long? Reset)
{
    public double Remaining => Math.Clamp(100 - Used, 0, 100);
    public double Density(DateTimeOffset now)
    {
        if (Reset is not long reset) return double.NegativeInfinity;
        var minutes = (reset - now.ToUnixTimeSeconds()) / 60.0;
        return minutes > 0 ? Remaining / minutes : double.NegativeInfinity;
    }
    public static QuotaWindow? Read(JsonElement e)
    {
        var used = e.Get("usedPercent").Number();
        var duration = e.Get("windowDurationMins").Number();
        if (used == null || duration == null) return null;
        return new(used.Value, (int)duration.Value, e.Get("resetsAt").Number() is double reset ? (long)reset : null);
    }
    public static QuotaWindow? ReadUsage(JsonElement e)
    {
        var used = e.Get("used_percent").Number();
        var seconds = e.Get("limit_window_seconds").Number();
        if (used == null || seconds == null || seconds <= 0) return null;
        return new(used.Value, (int)Math.Ceiling(seconds.Value / 60), e.Get("reset_at").Number() is double reset ? (long)reset : null);
    }
}

sealed class Account
{
    public string Key { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Backup { get; set; } = "";
    public QuotaWindow? FiveHour { get; set; }
    public QuotaWindow? Weekly { get; set; }
    public int? Resets { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public string? Error { get; set; }
    [JsonIgnore] public bool Active { get; set; }
    public void Apply(JsonElement result)
    {
        var bucket = result.Get("rateLimitsByLimitId").Get("codex");
        if (bucket.ValueKind != JsonValueKind.Object) bucket = result.Get("rateLimits");
        var windows = new[] { QuotaWindow.Read(bucket.Get("primary")), QuotaWindow.Read(bucket.Get("secondary")) };
        FiveHour = windows.FirstOrDefault(w => w?.Minutes == 300);
        Weekly = windows.FirstOrDefault(w => w?.Minutes == 10080);
        Resets = result.Get("rateLimitResetCredits").Get("availableCount").Number() is double count ? (int)count : null;
        Updated = DateTimeOffset.Now;
        Error = null;
    }
    public void ApplyUsage(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("rate_limit", out var bucket))
            throw new InvalidOperationException("Usage response has an unexpected format; previous quota retained.");
        var windows = new[] { QuotaWindow.ReadUsage(bucket.Get("primary_window")), QuotaWindow.ReadUsage(bucket.Get("secondary_window")) };
        FiveHour = windows.FirstOrDefault(w => w?.Minutes == 300);
        Weekly = windows.FirstOrDefault(w => w?.Minutes == 10080);
        Resets = result.Get("rate_limit_reset_credits").Get("available_count").Number() is double count ? (int)count : null;
        Updated = DateTimeOffset.Now;
        Error = null;
    }
}

sealed class Settings
{
    public bool ResumeRecentChatsAfterSwitch { get; set; } = true;
    // Existing chat in which the user authorized account-switch recovery; used for app-tool attribution.
    public string RecoveryControllerThreadId { get; set; } = "01a11bfc-788c-7ab1-9331-1535c01c284a";
    public string CodexHome { get; set; } = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public string CliPath { get; set; } = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "plugins", ".plugin-appserver", "codex.exe");
    public string CliArguments { get; set; } = "--no-daemon resume --all";
    public List<string> RemovedAccountKeys { get; set; } = [];
    public string DesktopPath { get; set; } = "";
    public string DesktopAppId { get; set; } = "";
    public bool StartWithWindows { get; set; }
    public List<Account> Accounts { get; set; } = [];
}

sealed class Store
{
    public string DirectoryPath { get; }
    public Settings Data { get; }
    public string AuthPath => Path.Combine(Data.CodexHome, "auth.json");
    public Store(string? directory = null)
    {
        DirectoryPath = directory ?? ResolveDataDirectory(AppContext.BaseDirectory);
        Directory.CreateDirectory(DirectoryPath);
        var file = Path.Combine(DirectoryPath, "settings.json");
        if (directory == null && !File.Exists(file))
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray", "settings.json");
            if (File.Exists(legacy)) File.Copy(legacy, file, false);
        }
        Data = File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json.Options) ?? new() : new();
    }
    internal static string ResolveDataDirectory(string executableDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(executableDirectory));
        for (var depth = 0; current != null && depth < 6; depth++, current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "CodexQuotaTray.csproj")))
                return Path.Combine(current.FullName, "data");
        return Path.Combine(Path.GetFullPath(executableDirectory), "data");
    }
    public void Save() => AtomicWrite(Path.Combine(DirectoryPath, "settings.json"), JsonSerializer.Serialize(Data, Json.Options));
    public string BackupPath(Identity identity)
    {
        var path = AuthPath + "." + identity.Suffix;
        if (File.Exists(path) && Identity.Read(path).Key != identity.Key)
            path += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity.Key)))[..8];
        return path;
    }
    public void RepairBackupPaths()
    {
        foreach (var account in Data.Accounts)
        {
            var identity = new Identity(account.Key, account.Email, account.Name);
            var canonical = BackupPath(identity);
            if (string.Equals(account.Backup, canonical, StringComparison.OrdinalIgnoreCase)) continue;
            // Salvage a legacy copy only if its contents really belong to this row.
            if (!File.Exists(canonical) && File.Exists(account.Backup))
            {
                try
                {
                    var contents = File.ReadAllText(account.Backup);
                    if (Identity.FromContents(contents).Key == account.Key) AtomicWrite(canonical, contents);
                }
                catch { }
            }
            account.Backup = canonical;
        }
    }
    public static void AtomicWrite(string path, string contents)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, contents, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Account? CaptureCurrent(bool readdRemoved = false)
    {
        foreach (var a in Data.Accounts) a.Active = false;
        if (!File.Exists(AuthPath)) return null;
        var contents = File.ReadAllText(AuthPath);
        var identity = Identity.FromContents(contents);
        if (Data.RemovedAccountKeys.Contains(identity.Key, StringComparer.Ordinal))
        {
            if (!readdRemoved) return new Account { Key = identity.Key, Email = identity.Email, Name = identity.Name, Active = true };
            Data.RemovedAccountKeys.RemoveAll(key => key == identity.Key);
        }
        var account = Data.Accounts.FirstOrDefault(a => a.Key == identity.Key);
        if (account == null)
        {
            var backup = BackupPath(identity);
            account = new Account { Key = identity.Key, Email = identity.Email, Name = identity.Name, Backup = backup };
            Data.Accounts.Add(account);
        }
        // Never trust a cached path: an old row may point to the live auth.json.
        account.Backup = BackupPath(identity);
        AtomicWrite(account.Backup, contents);
        account.Active = true;
        Save();
        return account;
    }
    public void DiscoverBackups()
    {
        if (!Directory.Exists(Data.CodexHome)) return;
        RepairBackupPaths();
        foreach (var path in Directory.EnumerateFiles(Data.CodexHome, "auth.json.*"))
        {
            if (path.EndsWith(".tmp")) continue;
            try
            {
                var id = Identity.Read(path);
                if (!Data.RemovedAccountKeys.Contains(id.Key, StringComparer.Ordinal) && !Data.Accounts.Any(a => a.Key == id.Key))
                    Data.Accounts.Add(new Account { Key = id.Key, Email = id.Email, Name = id.Name, Backup = path });
            }
            catch { /* Ignore unrelated backup formats, never display file contents. */ }
        }
    }
    public int Remove(Account account)
    {
        if (!Data.RemovedAccountKeys.Contains(account.Key, StringComparer.Ordinal)) Data.RemovedAccountKeys.Add(account.Key);
        Data.Accounts.RemoveAll(a => a.Key == account.Key);
        Save(); // Persist the exclusion before touching backups; discovery must never recreate the row.
        var deleted = 0;
        if (!Directory.Exists(Data.CodexHome)) return deleted;
        foreach (var path in Directory.EnumerateFiles(Data.CodexHome, "auth.json.*"))
        {
            if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Identity.Read(path).Key != account.Key) continue;
                File.Delete(path);
                deleted++;
            }
            catch (FileNotFoundException) { }
            catch (JsonException) { /* Ignore unrelated or incomplete backup files. */ }
            catch (FormatException) { /* Ignore unrelated or incomplete backup files. */ }
        }
        return deleted;
    }
    public void Restore(Account account)
    {
        var contents = File.ReadAllText(account.Backup);
        if (Identity.FromContents(contents).Key != account.Key) throw new InvalidOperationException("The saved login does not match this account.");
        AtomicWrite(AuthPath, contents);
    }
}

// One short-lived app-server connection; no inference requests, threads, or MCP tools.
sealed class Rpc : IAsyncDisposable
{
    readonly CancellationToken cancellation;
    readonly Process process;
    readonly Task stderrDrain;
    int nextId;
    public Rpc(string cli, string home, bool fakeServer = false, CancellationToken cancellation = default)
    {
        this.cancellation = cancellation;
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in fakeServer ? new[] { "--fake-rpc" } : new[] { "app-server", "--listen", "stdio://", "-c", "cli_auth_credentials_store=\"file\"" }) start.ArgumentList.Add(arg);
        start.Environment["CODEX_HOME"] = home;
        foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" }) start.Environment.Remove(key);
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex CLI.");
        stderrDrain = DrainAsync();
    }
    async Task DrainAsync() { while (await process.StandardError.ReadLineAsync() != null) { } }
    public async Task<JsonElement> Call(string method, object? parameters = null)
    {
        var id = ++nextId;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }));
        await process.StandardInput.FlushAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("Codex app-server closed the connection.");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.Get("method").Text() == "account/chatgptAuthTokens/refresh")
                    throw new InvalidOperationException("Saved access token expired. Quota is cached until Codex renews that account's login.");
                if (root.Get("id").Number() != id) continue;
                if (root.Get("error").ValueKind == JsonValueKind.Object)
                    throw new InvalidOperationException($"Codex rejected {method} (code {root.Get("error").Get("code").Number()}); sign in again if credentials expired.");
                return root.Get("result").Clone();
            }
        }
    }
    public async Task Initialize()
    {
        await Call("initialize", new { clientInfo = new { name = "codex_quota_tray", title = "Codex Quota Tray", version = "1.0.0" }, capabilities = new { experimentalApi = true } });
        await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
        await process.StandardInput.FlushAsync();
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(2000);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); }
            await stderrDrain.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch { }
        process.Dispose();
    }
}

sealed class QuotaService(Store store, HttpClient? client = null)
{
    static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
    public async Task Refresh(Account account, CancellationToken cancellation = default)
    {
        try
        {
            var contents = File.ReadAllText(account.Active ? store.AuthPath : account.Backup);
            if (Identity.FromContents(contents).Key != account.Key) throw new InvalidOperationException("Saved account identity mismatch.");
            using var auth = JsonDocument.Parse(contents);
            var tokens = auth.RootElement.Get("tokens");
            var accessToken = tokens.Get("access_token").Text() ?? throw new InvalidOperationException("Saved access token is missing.");
            var accountId = tokens.Get("account_id").Text() ?? throw new InvalidOperationException("Saved account ID is missing.");
            // Read-only HTTPS: no CLI process, plugin initialization, temporary home or token refresh.
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("ChatGPT-Account-Id", accountId);
            request.Headers.UserAgent.ParseAdd("CodexQuotaTray/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await (client ?? SharedClient).SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Saved access token expired. Quota stays cached until Codex renews this account's login.");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Quota request failed (HTTP {(int)response.StatusCode}); previous quota retained.");
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var responseAccount = payload.RootElement.Get("account_id").Text();
            if (responseAccount != null && responseAccount != accountId) throw new InvalidOperationException("Usage response belongs to a different account; previous quota retained.");
            account.ApplyUsage(payload.RootElement);
        }
        catch (OperationCanceledException) { account.Error = "Quota request timed out"; }
        catch (Exception e) { account.Error = e is InvalidOperationException ? e.Message : "Unable to read quota; check paths, permissions, and network."; }
    }
}

record DesktopCommand(string Executable, string[] Arguments)
{
    public static DesktopCommand Parse(string value)
    {
        value = value.Trim();
        if (File.Exists(value)) return new(Path.GetFullPath(value), []); // Existing bare paths with spaces.
        if (value.Length == 0) return new("", []);
        var pointer = CommandLineToArgvW(value, out var count);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Invalid desktop launch command.");
        try
        {
            var words = Enumerable.Range(0, count).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "").ToArray();
            return new(words[0], words[1..]);
        }
        finally { LocalFree(pointer); }
    }
    public ProcessStartInfo StartInfo()
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in Arguments) info.ArgumentList.Add(argument);
        return info;
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string command, out int count);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}

static class CodexProcess
{
    static async Task<string> PowerShell(string script)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(15000);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch { if (!p.HasExited) p.Kill(true); throw; }
        await error;
        return (await output).Trim();
    }
    public static async Task Discover(Settings settings)
    {
        if (!IsLauncher(settings) && ((settings.DesktopPath == "" && settings.DesktopAppId == "") || settings.DesktopAppId.StartsWith("OpenAI.Codex_", StringComparison.Ordinal)))
        {
            var json = await PowerShell("$p = Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1; if ($p) { $m = Get-AppxPackageManifest $p; $a = $m.Package.Applications.Application | Select-Object -First 1; @{ appId = ($p.PackageFamilyName + '!' + $a.Id); path = (Join-Path $p.InstallLocation $a.Executable) } | ConvertTo-Json -Compress }");
            if (json.StartsWith('{'))
            {
                using var d = JsonDocument.Parse(json);
                settings.DesktopPath = d.RootElement.Get("path").Text() ?? "";
                settings.DesktopAppId = d.RootElement.Get("appId").Text() ?? "";
            }
        }
    }
    public static void ValidateLaunch(Settings settings)
    {
        var executable = DesktopCommand.Parse(settings.DesktopPath).Executable;
        if (!File.Exists(executable) || Path.GetFileName(executable).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose the Codex desktop executable in Settings before switching accounts.");
    }
    static bool IsLauncher(Settings settings) => Path.GetFileName(DesktopCommand.Parse(settings.DesktopPath).Executable).Equals("InvisibleLauncher.exe", StringComparison.OrdinalIgnoreCase);
    static List<Process> DesktopProcesses(Settings settings) => IsLauncher(settings)
        ? DesktopProcesses("ChatGPT.exe", true)
        : DesktopProcesses(Path.GetFullPath(DesktopCommand.Parse(settings.DesktopPath).Executable));
    internal static List<Process> DesktopProcesses(string target, bool matchNameOnly = false)
    {
        var matches = new List<Process>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            try
            {
                if (p.Id != Environment.ProcessId && string.Equals(matchNameOnly ? Path.GetFileName(p.MainModule?.FileName) : p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) { matches.Add(p); continue; }
            }
            catch { }
            p.Dispose();
        }
        return matches;
    }
    public static Task Stop(Settings settings) => Task.Run(async () =>
    {
        ValidateLaunch(settings);
        // Only the configured desktop executable, never codex.exe CLI/app-server processes.
        foreach (var p in DesktopProcesses(settings))
        {
            using (p)
            {
                // Never kill the process tree: it may contain this switcher or its launcher.
                try { if (!p.HasExited) p.Kill(false); }
                catch (InvalidOperationException) { continue; }
                var wait = Stopwatch.StartNew();
                while (true)
                {
                    var processes = Process.GetProcessesByName(IsLauncher(settings) ? "ChatGPT" : Path.GetFileNameWithoutExtension(DesktopCommand.Parse(settings.DesktopPath).Executable));
                    var running = processes.Any(candidate => candidate.Id == p.Id);
                    foreach (var candidate in processes) candidate.Dispose();
                    if (!running) break;
                    if (wait.ElapsedMilliseconds >= 10000) throw new TimeoutException($"Codex process {p.Id} did not stop within 10 seconds.");
                    await Task.Delay(100);
                }
            }
        }
    });
    public static Task Start(Settings settings) => Task.Run(async () =>
    {
        await Discover(settings);
        ValidateLaunch(settings);
        if (settings.DesktopAppId != "" && !IsLauncher(settings))
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + settings.DesktopAppId) { UseShellExecute = true });
        else Process.Start(DesktopCommand.Parse(settings.DesktopPath).StartInfo());
        for (var attempt = 0; attempt < 240; attempt++)
        {
            await Task.Delay(250);
            var running = DesktopProcesses(settings);
            var found = running.Count > 0;
            foreach (var p in running) p.Dispose();
            if (found) return;
        }
        throw new InvalidOperationException("Codex did not start within 60 seconds. Check its desktop executable in Settings.");
    });
}






