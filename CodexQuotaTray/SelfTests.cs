using System.Text;
using System.Text.Json;

namespace CodexQuotaTray;

static class SelfTests
{
    public static int Run()
    {
        var results = new List<string>();
        var directory = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settingsRoot = Path.Combine(directory, "stable-settings");
            Directory.CreateDirectory(settingsRoot);
            File.WriteAllText(Path.Combine(settingsRoot, "CodexQuotaTray.csproj"), "fixture");
            var expectedData = Path.Combine(settingsRoot, "data");
            if (Store.ResolveDataDirectory(Path.Combine(settingsRoot, "publish")) != expectedData ||
                Store.ResolveDataDirectory(Path.Combine(settingsRoot, "bin", "portable")) != expectedData ||
                Store.ResolveDataDirectory(Path.Combine(settingsRoot, "bin", "Release", "net10.0-windows")) != expectedData)
                throw new Exception("All update launch folders must share the same settings directory");
            var persisted = new Store(expectedData);
            persisted.Data.CodexHome = "saved-home"; persisted.Data.DesktopPath = "saved-launcher"; persisted.Data.StartWithWindows = true; persisted.Save();
            var reopened = new Store(Store.ResolveDataDirectory(Path.Combine(settingsRoot, "publish")));
            if (reopened.Data.CodexHome != "saved-home" || reopened.Data.DesktopPath != "saved-launcher" || !reopened.Data.StartWithWindows)
                throw new Exception("Update launch path reset saved settings");
            results.Add("PASS Settings persist across publish, portable and Release launch folders independent of Windows profile");
            var launch = DesktopCommand.Parse("\"Q:\\folder with spaces\\InvisibleLauncher.exe\" aiaiai \"two words\"");
            if (launch.Executable != "Q:\\folder with spaces\\InvisibleLauncher.exe" || !launch.Arguments.SequenceEqual(new[] { "aiaiai", "two words" })) throw new Exception("Desktop command argument parsing");
            var simpleLaunch = DesktopCommand.Parse("Q:\\_coding\\xxxc0d3xxx\\CodexQuotaTray\\publish\\InvisibleLauncher.exe aiaiai");
            if (simpleLaunch.Arguments.Length != 1 || simpleLaunch.Arguments[0] != "aiaiai" || !simpleLaunch.Executable.EndsWith("InvisibleLauncher.exe")) throw new Exception("Unquoted launcher command");
            if (!launch.StartInfo().ArgumentList.SequenceEqual(launch.Arguments)) throw new Exception("Launcher argument forwarding");
            results.Add("PASS Executable plus arguments supports positional username, quoted paths and quoted arguments");
            var store = new Store(directory); store.Data.CodexHome = directory;
            string Fixture(string email, string id)
            {
                var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { email, name = "Test", sub = id }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return JsonSerializer.Serialize(new { tokens = new { id_token = "test." + claims + ".test", account_id = id, access_token = "fake" } });
            }
            void Check(bool condition, string name) { if (!condition) throw new Exception(name); results.Add("PASS " + name); }
            File.WriteAllText(store.AuthPath, Fixture("one@example.com", "one"));
            var one = store.CaptureCurrent()!;
            Check(File.Exists(store.AuthPath + ".one@example.com"), "Email-suffixed backup beside auth.json");
            File.Delete(one.Backup); one.Backup = store.AuthPath;
            store.CaptureCurrent();
            Check(one.Backup != store.AuthPath && File.Exists(one.Backup), "Capture recreates missing backup and repairs live-file alias");
            File.WriteAllText(store.AuthPath, Fixture("two@example.com", "two"));
            var two = store.CaptureCurrent()!;
            Check(store.Data.Accounts.Count == 2 && two.Active && !one.Active, "Independent account capture and active selection");
            store.Restore(one); Check(Identity.Read(store.AuthPath).Key == one.Key, "Switch restores exact selected account");
            File.WriteAllText(store.AuthPath, Fixture("one@example.com", "other-workspace"));
            var workspace = store.CaptureCurrent()!;
            Check(workspace.Backup != one.Backup && Identity.Read(one.Backup).Key == one.Key, "Shared email workspace does not overwrite existing backup");
            File.WriteAllText(two.Backup, Fixture("wrong@example.com", "wrong"));
            var before = File.ReadAllText(store.AuthPath);
            try { store.Restore(two); throw new Exception("Identity guard failed"); } catch (InvalidOperationException) { }
            Check(File.ReadAllText(store.AuthPath) == before, "Mismatched backup rejected without modifying live authentication");
            File.WriteAllText(two.Backup, Fixture("two@example.com", "two"));
            store.Save(); var loaded = new Store(directory);
            Check(loaded.Data.Accounts.Count == 3, "Account inventory persists across restarts");
            using var quota = JsonDocument.Parse("""
                {"rateLimits":{"primary":{"usedPercent":99,"windowDurationMins":300}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":25,"windowDurationMins":10080,"resetsAt":2000000000},"secondary":{"usedPercent":60,"windowDurationMins":300,"resetsAt":1900000000}}},"rateLimitResetCredits":{"availableCount":2}}
                """);
            one.Apply(quota.RootElement);
            Check(one.FiveHour?.Remaining == 40 && one.Weekly?.Remaining == 75 && one.Resets == 2, "Bucket precedence, duration mapping, remaining percentages and reset credits");
            using var missing = JsonDocument.Parse("{\"rateLimits\":{\"primary\":null,\"secondary\":null},\"rateLimitResetCredits\":null}");
            one.Apply(missing.RootElement);
            Check(one.FiveHour == null && one.Weekly == null && one.Resets == null, "Unknown quotas stay unknown rather than showing zero");
            using var legacy = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":120,\"windowDurationMins\":300}}}");
            one.Apply(legacy.RootElement); Check(one.FiveHour?.Remaining == 0, "Legacy quota fallback and percentage clamp");
            TestRpc(directory).GetAwaiter().GetResult(); results.Add("PASS App-server handshake, interleaved notifications, account and quota RPC, error handling and cleanup");
            TestDesktopStop(directory).GetAwaiter().GetResult();
            results.Add("PASS Desktop stop preserves child processes, including a switcher launched from the desktop");
            store.Data.CliPath = Environment.ProcessPath!;
            var activeForProbe = store.CaptureCurrent()!;
            var authBeforeProbe = File.ReadAllText(store.AuthPath);
            var backupBeforeProbe = File.ReadAllText(activeForProbe.Backup);
            using var quotaClient = new System.Net.Http.HttpClient(new FakeUsage());
            new QuotaService(store, quotaClient).Refresh(activeForProbe).GetAwaiter().GetResult();
            Check(activeForProbe.Updated != null && File.ReadAllText(store.AuthPath) == authBeforeProbe && File.ReadAllText(activeForProbe.Backup) == backupBeforeProbe,
                "Direct HTTP quota read leaves live login and backup unchanged");
            Check(!Directory.Exists(Path.Combine(directory, "probes")) && activeForProbe.FiveHour?.Remaining == 75 && activeForProbe.Weekly?.Remaining == 40 && activeForProbe.Resets == 3,
                "HTTP quota windows and reset credits parsed without creating any probe folders");
            using var unauthorizedClient = new System.Net.Http.HttpClient(new FakeUsage(System.Net.HttpStatusCode.Unauthorized));
            var lastRead = activeForProbe.Updated;
            new QuotaService(store, unauthorizedClient).Refresh(activeForProbe).GetAwaiter().GetResult();
            Check(activeForProbe.Updated == lastRead && activeForProbe.Error != null && File.ReadAllText(store.AuthPath) == authBeforeProbe,
                "Expired access token preserves cached quota and never refreshes authentication");
            File.Delete(one.Backup); one.Backup = store.AuthPath;
            two.Backup = Path.Combine(directory, "auth.json.missing-legacy");
            store.RepairBackupPaths();
            Check(one.Backup != store.AuthPath && two.Backup == store.AuthPath + ".two@example.com", "Legacy inventory repaired without assigning another account's live credentials");
            var densityNow = DateTimeOffset.FromUnixTimeSeconds(2000000000);
            Check(new QuotaWindow(50, 300, 2000000600).Density(densityNow) == 5
                && new QuotaWindow(10, 300, 2000003600).Density(densityNow) == 1.5
                && double.IsNegativeInfinity(new QuotaWindow(0, 300, 2000000000).Density(densityNow))
                && double.IsNegativeInfinity(new QuotaWindow(0, 300, null).Density(densityNow)),
                "Density ranks remaining quota per minute and puts unknown or passed resets last");
            TestChatRecovery(directory).GetAwaiter().GetResult();
            results.Add("PASS Chat recovery excludes old, completed, unrelated errors, active and superseded turns; resumes recent quota failures once in the GUI");
            results.Add("All checks passed.");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "test-results.txt"), results); return 0;
        }
        catch (Exception e) { results.Add("FAIL " + e.Message); File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "test-results.txt"), results); return 1; }
        finally
        {
            // Windows may retain executable mappings briefly after process termination.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(directory, true); break; }
                catch (IOException) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) { Thread.Sleep(50); }
            }
        }
    }
    static async Task TestChatRecovery(string directory)
    {
        var now = DateTimeOffset.UtcNow;
        JsonElement Turn(string status, string? message = null) => JsonSerializer.SerializeToElement(new { id = "turn-" + status, status, error = new { message } });
        var recent = now.AddMinutes(-2).ToUnixTimeSeconds();
        var old = now.AddHours(-3).ToUnixTimeSeconds();
        if (!ChatRecovery.Eligible(Turn("failed", "usage limit"), false, recent * 1000.0, now) ||
            ChatRecovery.Eligible(Turn("failed", "usage limit"), false, old * 1000.0, now)) throw new Exception("Recovery handles seconds and milliseconds without reviving old chats");
        if (!ChatRecovery.Eligible(Turn("failed", "You’ve hit your usage limit."), false, recent, now) ||
            !ChatRecovery.Eligible(Turn("interrupted"), false, recent, now) ||
            !ChatRecovery.Eligible(Turn("inProgress"), true, old, now) ||
            ChatRecovery.Eligible(Turn("failed", "Compilation failed"), false, recent, now) ||
            ChatRecovery.Eligible(Turn("completed"), false, recent, now) ||
            ChatRecovery.Eligible(Turn("failed", "usage limit"), false, old, now) ||
            ChatRecovery.Eligible(Turn("interrupted"), false, old, now)) throw new Exception("Chat recovery eligibility");
        var fixture = new FakeChats(now);
        var store = new Store(Path.Combine(directory, "recovery"));
        var recovery = new ChatRecovery(store, fixture);
        var candidates = await recovery.Capture(now);
        if (candidates.Count != 2 || candidates.Any(c => c.ThreadId == "old" || c.ThreadId == "done")) throw new Exception("Recovery capture scope");
        fixture.Finished = true;
        if (await recovery.Recover(candidates, now) != 1 || fixture.Sends != 1 || fixture.LastTarget != "quota") throw new Exception("Recovery rechecks completed chats");
        await recovery.Recover(candidates, now);
        if (fixture.Sends != 1) throw new Exception("Duplicate recovery dispatch");
        var candidate = candidates.Single(c => c.ThreadId == "quota");
        var freshStore = new Store(Path.Combine(directory, "expired-recovery"));
        if (await new ChatRecovery(freshStore, fixture).Recover([candidate], now.AddHours(2)) != 0) throw new Exception("Expired recovery snapshot");
        fixture.Superseded = true;
        if (await new ChatRecovery(freshStore, fixture).Recover([candidate], now) != 0) throw new Exception("Superseded recovery turn");
        fixture.Superseded = false; fixture.Active = true;
        if (await new ChatRecovery(freshStore, fixture).Recover([candidate], now) != 0) throw new Exception("Already active recovery turn");
    }
    sealed class FakeChats(DateTimeOffset now) : IDesktopChats
    {
        public int Sends; public string? LastTarget; public bool Finished, Superseded, Active;
        public Task<JsonElement> Call(string tool, object arguments)
        {
            var args = JsonSerializer.SerializeToElement(arguments);
            if (tool == "list_threads") return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                pinnedThreads = new[] { Entry("quota"), Entry("interrupted"), Entry("old", true), Entry("done") }, threads = Array.Empty<object>()
            }));
            var id = args.Get("threadId").Text();
            if (tool == "send_message_to_thread") { Sends++; LastTarget = id; return Task.FromResult(JsonSerializer.SerializeToElement(new { threadId = id })); }
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                thread = new { status = new { type = Active ? "active" : "notLoaded" } },
                turns = new[] { new { id = Superseded ? "new-turn" : "turn-" + id,
                    status = id == "done" || (id == "interrupted" && Finished) ? "completed" : id == "interrupted" ? "interrupted" : "failed",
                    error = new { message = "You’ve hit your usage limit." } } }
            }));
        }
        object Entry(string id, bool old = false) => new { id, title = id, kind = "codex", hostId = "local", status = "notLoaded", updatedAt = now.AddMinutes(old ? -180 : -1).ToUnixTimeSeconds() };
    }
    sealed class FakeUsage(System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != System.Net.Http.HttpMethod.Get || request.RequestUri?.ToString() != "https://chatgpt.com/backend-api/wham/usage"
                || request.Headers.Authorization?.Scheme != "Bearer" || !request.Headers.Contains("ChatGPT-Account-Id"))
                throw new InvalidOperationException("Incorrect quota request");
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(status)
            {
                Content = new System.Net.Http.StringContent("""
                    {"rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":18000,"reset_at":2000000000},"secondary_window":{"used_percent":60,"limit_window_seconds":604800,"reset_at":2000500000}},"rate_limit_reset_credits":{"available_count":3}}
                    """)
            });
        }
    }
    static async Task TestDesktopStop(string directory)
    {
        var fixture = Path.Combine(directory, "desktop"); Directory.CreateDirectory(fixture);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
            if (file.EndsWith(".dll") || file.EndsWith(".json") || file.EndsWith(".exe")) File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)));
        var executable = Path.Combine(fixture, Path.GetFileName(Environment.ProcessPath!));
        var childFile = Path.Combine(directory, "child.pid");
        var info = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--test-parent"); info.ArgumentList.Add(Environment.ProcessPath!); info.ArgumentList.Add(childFile);
        using var parent = System.Diagnostics.Process.Start(info)!;
        System.Diagnostics.Process? child = null;
        try
        {
            for (var i = 0; i < 100 && !File.Exists(childFile); i++) await Task.Delay(50);
            child = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(childFile)));
            await CodexProcess.Stop(new Settings { DesktopPath = executable });
            if (!parent.HasExited || child.HasExited) throw new Exception("Desktop stop killed descendants or failed to stop the desktop");
        }
        finally
        {
            if (!parent.HasExited) parent.Kill(false);
            if (child != null) { if (!child.HasExited) { child.Kill(false); await child.WaitForExitAsync(); } child.Dispose(); }
            await parent.WaitForExitAsync();
        }
    }
    static async Task TestRpc(string home)
    {
        await using var rpc = new Rpc(Environment.ProcessPath!, home, true);
        await rpc.Initialize();
        var account = await rpc.Call("account/read", new { refreshToken = false });
        if (account.Get("account").Get("email").Text() != "test@example.com") throw new Exception("RPC account response");
        var quota = await rpc.Call("account/rateLimits/read");
        if (quota.Get("rateLimitResetCredits").Get("availableCount").Number() != 3) throw new Exception("RPC quota response");
        try { await rpc.Call("rejected"); throw new Exception("RPC errors not handled"); } catch (InvalidOperationException) { }
    }
    public static int FakeServer()
    {
        // WinExe has no console, but redirected standard handles are available.
        using var input = new StreamReader(Console.OpenStandardInput());
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        bool initialized = false, notified = false;
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            using var doc = JsonDocument.Parse(line); var root = doc.RootElement; var method = root.Get("method").Text();
            if (method == "initialized") { notified = true; continue; }
            var id = root.Get("id").Number();
            output.WriteLine("{\"method\":\"account/updated\",\"params\":{}}");
            if (method == "initialize") { initialized = true; output.WriteLine(JsonSerializer.Serialize(new { id, result = new { userAgent = "test" } })); }
            else if (!initialized || !notified || method == "rejected") output.WriteLine(JsonSerializer.Serialize(new { id, error = new { code = -32601, message = "test" } }));
            else if (method == "account/read") output.WriteLine(JsonSerializer.Serialize(new { id, result = new { account = new { email = "test@example.com" } } }));
            else output.WriteLine(JsonSerializer.Serialize(new { id, result = new { rateLimitResetCredits = new { availableCount = 3 } } }));
        }
        return 0;
    }
}
