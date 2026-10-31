using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexQuotaTray;

interface IDesktopChats
{
    Task<JsonElement> Call(string tool, object arguments);
}

// Uses the desktop's own app-tools transport. Never starts a CLI agent or writes its database.
sealed class DesktopChats(string controllerThreadId, string? desktopUser = null) : IDesktopChats
{
    string? pipeName;
    public async Task<JsonElement> Call(string tool, object arguments)
    {
        if (desktopUser != null && !desktopUser.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
            return await CallAsDesktopUser(tool, arguments);
        pipeName ??= await FindPipe();
        var response = await Request(pipeName, "tools/call", new
        {
            arguments, callerSource = "codex", callId = "quota-recovery-" + Guid.NewGuid(),
            hostId = "local", @namespace = "codex_app", threadId = controllerThreadId,
            tool, turnId = "quota-recovery-" + Guid.NewGuid()
        }, 45000);
        if (response.Get("success").ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("The desktop could not complete chat recovery: " + string.Join(" ",
                response.Get("contentItems").EnumerateArray().Select(i => i.Get("text").Text())));
        foreach (var item in response.Get("contentItems").EnumerateArray())
            if (item.Get("type").Text() == "inputText" && item.Get("text").Text() is string text)
            {
                using var parsed = JsonDocument.Parse(text);
                return parsed.RootElement.Clone();
            }
        throw new InvalidOperationException("The desktop returned no chat data.");
    }
    async Task<JsonElement> CallAsDesktopUser(string tool, object arguments)
    {
        // Same saved-login mechanism already used by InvisibleLauncher; no password is read or stored here.
        var directory = Path.Combine(AppContext.BaseDirectory, "recovery-rpc");
        Directory.CreateDirectory(directory);
        var request = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var reply = request + ".reply";
        File.WriteAllText(request, JsonSerializer.Serialize(new { controllerThreadId, tool, arguments }));
        try
        {
            var info = new ProcessStartInfo("runas.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add("/savecred"); info.ArgumentList.Add("/user:" + desktopUser);
            info.ArgumentList.Add("\"" + Environment.ProcessPath + "\" --desktop-chat-rpc \"" + request + "\"");
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start desktop-user recovery helper.");
            var wait = Stopwatch.StartNew();
            while (!File.Exists(reply))
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(55)) throw new TimeoutException("Desktop-user recovery helper did not reply. Check the saved Windows login used by InvisibleLauncher.");
                await Task.Delay(100);
            }
            using var response = JsonDocument.Parse(File.ReadAllText(reply));
            if (response.RootElement.Get("error").Text() is string error) throw new InvalidOperationException(error);
            return response.RootElement.Get("result").Clone();
        }
        finally { File.Delete(request); if (File.Exists(reply)) File.Delete(reply); }
    }
    public static int RunHelper(string request)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "recovery-rpc")) + Path.DirectorySeparatorChar;
        request = Path.GetFullPath(request);
        if (!request.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(request) + Path.DirectorySeparatorChar != directory) return 1;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(request));
            var root = document.RootElement;
            var tool = root.Get("tool").Text();
            if (tool is not ("list_threads" or "read_thread" or "send_message_to_thread")) throw new InvalidOperationException("Unsupported recovery helper operation.");
            var result = new DesktopChats(root.Get("controllerThreadId").Text()!).Call(tool, root.Get("arguments")).GetAwaiter().GetResult();
            Store.AtomicWrite(request + ".reply", JsonSerializer.Serialize(new { result }));
            return 0;
        }
        catch (Exception error) { Store.AtomicWrite(request + ".reply", JsonSerializer.Serialize(new { error = error.Message })); return 1; }
    }
    static async Task<string> FindPipe()
    {
        var candidates = Directory.GetFiles(@"\\.\pipe\", "codex-browser-use-*");
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var probes = candidates.Select(async path =>
        {
            var name = path[(path.LastIndexOf('\\') + 1)..];
            try
            {
                var response = await Request(name, "tools/list", null, 5000);
                if (response.Get("tools").ValueKind == JsonValueKind.Array &&
                    response.Get("tools").EnumerateArray().Any(t => t.Get("name").Text() == "send_message_to_thread")) return name;
            }
            catch (Exception error) { errors.Add(error.Message); }
            return null;
        });
        return (await Task.WhenAll(probes)).FirstOrDefault(n => n != null)
            ?? throw new InvalidOperationException("Codex desktop messaging bridge is not ready. " + string.Join("; ", errors.Distinct()));
    }
    internal static async Task<JsonElement> Request(string name, string method, object? arguments, int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
            throw new InvalidOperationException("Cannot identify desktop messaging bridge.");
        using (var server = Process.GetProcessById((int)pid))
            if (!server.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Messaging pipe does not belong to the Codex desktop.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, jsonrpc = "2.0", method, @params = arguments },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, timeout.Token); await pipe.WriteAsync(payload, timeout.Token);
        while (true)
        {
            await pipe.ReadExactlyAsync(header, timeout.Token);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size < 1 || size > 8 * 1024 * 1024) throw new InvalidOperationException("Invalid desktop response size.");
            var data = new byte[size]; await pipe.ReadExactlyAsync(data, timeout.Token);
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.Get("id").Number() != 1) continue;
            if (root.Get("error").ValueKind == JsonValueKind.Object)
                throw new InvalidOperationException("Desktop messaging request failed: " + root.Get("error").Get("message").Text());
            return root.Get("result").Clone();
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}

record RecoveryCandidate(string ThreadId, string TurnId, string Title, DateTimeOffset CapturedAt);

sealed class ChatRecovery(Store store, IDesktopChats chats)
{
    internal static readonly TimeSpan Recent = TimeSpan.FromMinutes(30);
    const string ContinueMessage = "Continue the unfinished work from your latest turn. It stopped during a quota/account interruption and the account switcher has now restarted Codex. Check current files and any still-running processes first to avoid duplicating work, then finish the existing user request and remaining verification, preserving all prior constraints. If the request is already complete or needs user input, report that instead.";
    string AttemptsPath => Path.Combine(store.DirectoryPath, "chat-recovery-attempts.json");
    public async Task<List<RecoveryCandidate>> Capture(DateTimeOffset now)
    {
        var list = await chats.Call("list_threads", new { limit = 50 });
        if (list.Get("unavailableHosts").ValueKind == JsonValueKind.Array &&
            list.Get("unavailableHosts").GetArrayLength() > 0)
            throw new InvalidOperationException("Chat recovery cannot inspect all desktop hosts.");
        var candidates = new List<RecoveryCandidate>();
        var attempts = ReadAttempts();
        foreach (var thread in Items(list, "pinnedThreads").Concat(Items(list, "threads")).DistinctBy(t => t.Get("id").Text()))
        {
            var id = thread.Get("id").Text();
            if (thread.Get("kind").Text() != "codex" || thread.Get("hostId").Text() != "local" ||
                id == null || id == store.Data.RecoveryControllerThreadId) continue;
            var active = thread.Get("status").Text() == "active";
            var updated = thread.Get("updatedAt").Number();
            if (!active && (updated == null || !IsRecent(updated.Value, now))) continue;
            var detail = await chats.Call("read_thread", new { threadId = id, hostId = "local", turnLimit = 1, includeOutputs = false, maxOutputCharsPerItem = 100 });
            var turn = Items(detail, "turns").FirstOrDefault();
            var turnId = turn.Get("id").Text();
            if (turnId == null || attempts.ContainsKey(turnId) || !Eligible(turn, active, updated, now)) continue;
            candidates.Add(new(id, turnId, thread.Get("title").Text() ?? id, now));
        }
        return candidates;
    }
    internal static bool Eligible(JsonElement turn, bool active, double? updated, DateTimeOffset now)
    {
        var status = turn.Get("status").Text();
        if (active && status == "inProgress") return true;
        if (updated == null || !IsRecent(updated.Value, now)) return false;
        return status == "interrupted" || (status == "failed" && IsQuotaError(turn.Get("error")));
    }
    static bool IsRecent(double timestamp, DateTimeOffset now)
    {
        if (timestamp > 10_000_000_000) timestamp /= 1000; // Desktop sources can return milliseconds.
        var age = now.ToUnixTimeSeconds() - timestamp;
        return age >= -60 && age <= Recent.TotalSeconds;
    }
    internal static bool IsQuotaError(JsonElement error)
    {
        var text = error.Get("message").Text() ?? "";
        return new[] { "usage limit", "quota", "insufficient_quota", "usage_limit", "usage_limit_reached" }
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
    public async Task<int> Recover(IReadOnlyList<RecoveryCandidate> candidates, DateTimeOffset now)
    {
        var attempts = ReadAttempts();
        foreach (var old in attempts.Where(p => now - p.Value > TimeSpan.FromDays(2)).Select(p => p.Key).ToArray()) attempts.Remove(old);
        var sent = 0;
        foreach (var candidate in candidates)
        {
            if (now < candidate.CapturedAt || now - candidate.CapturedAt > Recent || attempts.ContainsKey(candidate.TurnId)) continue;
            var detail = await chats.Call("read_thread", new { threadId = candidate.ThreadId, hostId = "local", turnLimit = 1, includeOutputs = false, maxOutputCharsPerItem = 100 });
            var turn = Items(detail, "turns").FirstOrDefault();
            if (turn.Get("id").Text() != candidate.TurnId || turn.Get("status").Text() == "completed" ||
                detail.Get("thread").Get("status").Get("type").Text() == "active") continue;
            // Save before dispatch: an ambiguous timeout must never send the same continuation twice.
            attempts[candidate.TurnId] = now;
            Store.AtomicWrite(AttemptsPath, JsonSerializer.Serialize(attempts, Json.Options));
            await chats.Call("send_message_to_thread", new { threadId = candidate.ThreadId, hostId = "local", prompt = ContinueMessage });
            sent++;
        }
        return sent;
    }
    Dictionary<string, DateTimeOffset> ReadAttempts() => File.Exists(AttemptsPath)
        ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(AttemptsPath), Json.Options) ?? new()
        : new();
    static IEnumerable<JsonElement> Items(JsonElement root, string key) => root.Get(key).ValueKind == JsonValueKind.Array
        ? root.Get(key).EnumerateArray() : Enumerable.Empty<JsonElement>();
}
