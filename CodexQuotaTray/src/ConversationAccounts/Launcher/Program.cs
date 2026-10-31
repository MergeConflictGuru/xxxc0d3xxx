using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string root = Bootstrap.ResolveRoot();
        string logPath = Path.Combine(root, "bridge", "launcher.log");
        try
        {
            if (args.Length == 2 && args[0] == "--self-test-setup")
                return Bootstrap.VerifyCleanInstall(args[1]);
            if (args.Contains("--setup") || args.Contains("--recreate") || !Bootstrap.Ready(root))
            {
                var installed = Bootstrap.Configure(root);
                if (installed == null) return 0;
                root = installed;
                logPath = Path.Combine(root, "bridge", "launcher.log");
                args = args.Where(arg => arg != "--setup" && arg != "--recreate").ToArray();
            }
            string[] allowed = ["--server-only", "--stop-server", "--server-status", "--stop"];
            if (args.Length > 1 || (args.Length == 1 && !allowed.Contains(args[0])))
                throw new ArgumentException("Use no arguments for the desktop, or --server-only, --server-status, --stop-server, --stop.");
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "bridge", "bridge-config.json")));
            if (args.Length == 0) Bootstrap.StartDesktopHelper(root);
            var start = new ProcessStartInfo(config.RootElement.GetProperty("engine").GetString()!)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine(root, "bridge", "launch.py"));
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start launcher.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(output, error);
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {string.Join(' ', args)} exit={process.ExitCode}\n{output.Result}{error.Result}\n");
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The multi-account launcher failed. See bridge/launcher.log for details.");
            if (args.Contains("--server-status")) MessageBox.Show(output.Result.Trim(), "Conversation Accounts");
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length > 0 && args[0] == "--self-test-setup")
            {
                Console.Error.WriteLine(error);
                if (args.Length == 2 && Directory.Exists(args[1])) File.WriteAllText(Path.Combine(args[1], "result.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
                return 1;
            }
            try { File.AppendAllText(logPath, error + Environment.NewLine); } catch { }
            MessageBox.Show(error.Message, "Conversation Accounts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
