using System.Diagnostics;
using System.Security.Principal;
using System.Text;

static class Program
{
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
        try
        {
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
                info.ArgumentList.Add("/savecred");
                info.ArgumentList.Add("/user:" + targetUser);
                info.ArgumentList.Add("\"" + Environment.ProcessPath + "\" \"" + targetUser + "\" --target-user" + (customExe != null ? " --exe \"" + customExe + "\"" : "") + (checkHidden ? " --check-hidden" : ""));
            }
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardError = isTarget && (customExe == null || checkHidden);
            info.RedirectStandardOutput = isTarget && (customExe == null || checkHidden);
            Log($"Starting {info.FileName}; mode={(isTarget ? "direct" : $"runas /savecred /user:{targetUser}")}; childLog={childLog}");
            using var process = Process.Start(info)!;
            Log($"Started pid={process.Id}");
            if (isTarget && customExe != null && !checkHidden) { Log("END custom executable started"); return 0; }
            var error = isTarget ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
            var output = isTarget ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
            if (!process.WaitForExit(20000))
            {
                Log($"ERROR: pid={process.Id} has not exited after 20 seconds. runas may be waiting for a password. Check saved credentials using rungpt.ps1 from this same Windows account.");
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
            if (!isTarget) Log("runas exit code only describes creation of the target-user process. Check InvisibleLauncher.child.log for the actual package launch result; if no new BEGIN appears, the target-user PowerShell never started or could not write this folder.");
            Log("END");
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex);
            return 1;
        }
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

