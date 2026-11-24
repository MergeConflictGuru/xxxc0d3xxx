using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

static class Program
{
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenWindowStation(string name, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr desktop);
    delegate bool WindowVisitor(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisitor visitor, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [StructLayout(LayoutKind.Sequential)]
    struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out WindowRect rectangle);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);


    static string ObjectName(IntPtr handle)
    {
        var text = new StringBuilder(256);
        return GetUserObjectInformation(handle, 2, text, text.Capacity * 2, out _) ? text.ToString() : "error " + Marshal.GetLastWin32Error();
    }
    static void UseInteractiveDesktop()
    {
        var stationName = ObjectName(GetProcessWindowStation());
        var desktopName = ObjectName(GetThreadDesktop(GetCurrentThreadId()));
        Log($"Launch desktop={stationName}\\{desktopName}");
        if (stationName.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) && desktopName.Equals("Default", StringComparison.OrdinalIgnoreCase)) return;
        var station = OpenWindowStation("WinSta0", false, 0x02000000);
        if (station == IntPtr.Zero || !SetProcessWindowStation(station))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot access the interactive window station.");
        var desktop = OpenDesktop("Default", 0, false, 0x02000000);
        if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot select the interactive desktop.");
        Log("Selected interactive launch desktop WinSta0\\Default");
    }
    static int ShowCodexWindow(int? desktopPid = null)
    {
        UseInteractiveDesktop();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var processes = Process.GetProcessesByName("ChatGPT");
            var ids = processes.Where(process => process.SessionId == Process.GetCurrentProcess().SessionId && (desktopPid == null || process.Id == desktopPid)).Select(process => (uint)process.Id).ToHashSet();
            foreach (var process in processes) process.Dispose();
            IntPtr target = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                if (!ids.Contains(pid) || GetWindowTextLength(window) == 0) return true;
                var className = new StringBuilder(128);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() != "Chrome_WidgetWin_1") return true;
                target = window;
                return false;
            }, IntPtr.Zero);
            if (target != IntPtr.Zero)
            {
                ShowWindowAsync(target, 9);
                SetForegroundWindow(target);
                Thread.Sleep(100);
                if (IsWindowVisible(target) && GetWindowRect(target, out var rectangle)
                    && DwmGetWindowAttribute(target, 14, out var cloaked, sizeof(int)) == 0 && cloaked == 0
                    && System.Windows.Forms.Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(
                        System.Drawing.Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom))))
                {
                    Log($"Codex GUI visible on interactive desktop; hwnd={target}; pid={desktopPid}; bounds={rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}; cloaked=0");
                    return 0;
                }
            }
            Thread.Sleep(200);
        }
        throw new InvalidOperationException("Codex started without a window on the interactive desktop.");
    }
    static string logPath = Path.Combine(AppContext.BaseDirectory, "InvisibleLauncher.error.log");

    static void Log(string message)
    {
        var line = $"[{DateTimeOffset.Now:O}] launcher pid={Environment.ProcessId}: {message}{Environment.NewLine}";
        try { File.AppendAllText(logPath, line); }
        catch
        {
            logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InvisibleLauncher.error.log");
            File.AppendAllText(logPath, line);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        var code = Run(args);
        var index = Array.IndexOf(args, "--result-file");
        if (index >= 0 && index + 1 < args.Length)
        {
            try { File.WriteAllText(args[index + 1], code.ToString()); }
            catch (Exception error) { Log("Cannot report relay result: " + error.Message); return 1; }
        }
        return code;
    }

    static int Run(string[] args)
    {
        string? relayResult = null;
        try
        {
            if (args.Length is 1 or 2 && args[0] == "--show-codex-window")
                return ShowCodexWindow(args.Length == 2 ? int.Parse(args[1]) : null);
            // Internal launch mode also works inside an activated Store package.
            // Start the GUI relay first, then create the console child with no window.
            if (args.Length > 0 && args[0] == "--hidden-powershell")
                return RunHiddenPowerShell(args.Skip(1));
            if (args.Length == 0 || args[0].StartsWith("--") || string.IsNullOrWhiteSpace(args[0]))
                throw new ArgumentException("Usage: InvisibleLauncher.exe <WindowsUsername> [--exe <path>]");
            var targetUser = args[0].Trim();
            string? customExe = null;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] is "--target-user" or "--check-hidden") continue;
                if (args[i] == "--result-file" && i + 1 < args.Length) { i++; continue; }
                if (args[i] != "--exe" || customExe != null || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    throw new ArgumentException("Usage: InvisibleLauncher.exe <WindowsUsername> [--exe <path>]");
                if (args[i].Any(ch => char.IsControl(ch) || ch == '"')) throw new ArgumentException("Invalid executable path.");
                customExe = Path.GetFullPath(args[i]);
            }
            if (targetUser.Any(ch => char.IsControl(ch) || ch == '\"')) throw new ArgumentException("Invalid Windows username.");
            var user = WindowsIdentity.GetCurrent().Name;
            Log($"BEGIN user={user}; executable={Environment.ProcessPath}; workingDirectory={Environment.CurrentDirectory}");
            var childLog = Path.Combine(Path.GetDirectoryName(logPath)!, "InvisibleLauncher.child.log");
            var scriptFile = Path.Combine(AppContext.BaseDirectory, "InvisibleLauncher.ps1");
            if (customExe == null && !File.Exists(scriptFile)) throw new FileNotFoundException("Launcher script is missing.", scriptFile);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptFile}\"";
            var checkHidden = args.Contains("--check-hidden");
            if (checkHidden)
            {
                const string check = "Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ConsoleProbe { [DllImport(\"kernel32.dll\")] public static extern IntPtr GetConsoleWindow(); }'; [ConsoleProbe]::GetConsoleWindow().ToInt64()";
                arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(check));
            }
            var isTarget = (targetUser.Contains('\\') || targetUser.Contains('@') ? user.Equals(targetUser, StringComparison.OrdinalIgnoreCase) : user.Split('\\').Last().Equals(targetUser, StringComparison.OrdinalIgnoreCase));
            if (isTarget && !checkHidden) UseInteractiveDesktop();
            if (args.Contains("--target-user") && !isTarget)
                throw new InvalidOperationException($"The hidden launcher relay did not start as {targetUser}.");
            if (!isTarget && !checkHidden)
                StartDesktopFileBridge(customExe);
            // runas starts a GUI executable, never a console executable under the other user.
            // That relay then creates PowerShell with CREATE_NO_WINDOW in its own user context.
            if (isTarget && customExe != null && !checkHidden && !File.Exists(customExe))
                throw new FileNotFoundException("Requested executable is missing.", customExe);
            var info = isTarget
                ? (customExe != null && !checkHidden
                    ? new ProcessStartInfo(customExe) { WorkingDirectory = Path.GetDirectoryName(customExe)! }
                    : new ProcessStartInfo(powershell, arguments))
                : new ProcessStartInfo("runas.exe");
            if (!isTarget)
            {
                relayResult = CreateRelayResult(targetUser);
                info.ArgumentList.Add("/savecred");
                info.ArgumentList.Add("/user:" + targetUser);
                info.ArgumentList.Add("\"" + Environment.ProcessPath + "\" \"" + targetUser + "\" --target-user" + (customExe != null ? " --exe \"" + customExe + "\"" : "") + (checkHidden ? " --check-hidden" : "") + " --result-file \"" + relayResult + "\"");
            }
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = isTarget && customExe != null && !checkHidden ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden;
            info.RedirectStandardError = isTarget && (customExe == null || checkHidden);
            info.RedirectStandardOutput = isTarget && (customExe == null || checkHidden);
            Log($"Starting {info.FileName}; mode={(isTarget ? "direct" : $"runas /savecred /user:{targetUser}")}; childLog={childLog}");
            using var process = Process.Start(info)!;
            Log($"Started pid={process.Id}");
            if (isTarget && customExe != null && !checkHidden)
            {
                Log("END custom executable started");
                return 0;
            }
            var error = isTarget ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
            var output = isTarget ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
            if (!process.WaitForExit(isTarget ? 360000 : 20000))
            {
                Log($"ERROR: pid={process.Id} did not complete startup within the allowed time. See the child log; runas may be waiting for a password.");
                return 1;
            }
            // A launched GUI can inherit pipe handles after the launcher script exits.
            // Its lifetime must not keep this launcher waiting for end-of-stream.
            Task.WaitAll(new Task[] { output, error }, 2000);
            var outputText = output.IsCompletedSuccessfully ? output.Result : "";
            var errorText = error.IsCompletedSuccessfully ? error.Result : "";
            Log($"Exit code={process.ExitCode}; stdout={outputText}; stderr={errorText}");
            if (checkHidden && isTarget)
            {
                var passed = process.ExitCode == 0 && outputText.Trim() == "0";
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "InvisibleLauncher.hidden-check.txt"), passed ? "PASS: PowerShell has no console window (GetConsoleWindow = 0)." : "FAIL: " + outputText + " " + errorText);
                return passed ? 0 : 1;
            }
            if (!isTarget && process.ExitCode == 0)
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromMinutes(6))
                {
                    if (int.TryParse(File.ReadAllText(relayResult!), out var code))
                    {
                        Log($"END target-user startup exit={code}");
                        return code;
                    }
                    Thread.Sleep(200);
                }
                throw new TimeoutException("The target-user launcher did not report startup completion. See InvisibleLauncher.child.log.");
            }
            Log("END");
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex);
            return 1;
        }
        finally
        {
            if (relayResult != null)
            {
                try { File.Delete(relayResult); Directory.Delete(Path.GetDirectoryName(relayResult)!); }
                catch (Exception error) { Log("Cannot remove relay result: " + error.Message); }
            }
        }
    }
    internal static string CreateRelayResult(string targetUser)
    {
        // Installation folders may be read-only to the desktop user. Each
        // invocation owns a private handoff folder; only its target gets access.
        var target = (SecurityIdentifier)new NTAccount(targetUser).Translate(typeof(SecurityIdentifier));
        var caller = WindowsIdentity.GetCurrent().User!;
        var directory = new DirectoryInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InvisibleLauncher", ".app_tmp", "relay", Guid.NewGuid().ToString("N")));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { caller, target, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.Create(directory, security);
        try
        {
            var result = Path.Combine(directory.FullName, "startup.result");
            File.WriteAllText(result, "");
            return result;
        }
        catch { directory.Delete(true); throw; }
    }

    static void StartDesktopFileBridge(string? customExe)
    {
        // Start before runas so Explorer and its drop target share a Windows user.
        // The helper's session mutex makes repeated launcher invocations harmless.
        var directory = customExe == null ? AppContext.BaseDirectory : Path.GetDirectoryName(customExe)!;
        var helper = Path.Combine(directory, "desktop-file-bridge", "DesktopFileBridge.exe");
        if (!File.Exists(helper))
        {
            Log("Desktop file helper not found: " + helper);
            return;
        }
        var info = new ProcessStartInfo(helper)
        {
            WorkingDirectory = Path.GetDirectoryName(helper)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Desktop file helper failed to start.");
        Log($"Started desktop file helper pid={process.Id} as {WindowsIdentity.GetCurrent().Name}");
    }
    static int RunHiddenPowerShell(IEnumerable<string> arguments)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Hidden PowerShell failed to start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(true);
            Log("Hidden PowerShell timed out.");
            return 1;
        }
        Task.WaitAll(new Task[] { output, error }, 2000);
        if (process.ExitCode != 0) Log("Hidden PowerShell failed: "
            + (error.IsCompletedSuccessfully ? error.Result : "")
            + (output.IsCompletedSuccessfully ? output.Result : ""));
        return process.ExitCode;
    }

}

