using System.Diagnostics;
using System.Text.Json;

// Disposable stdio adapter. The persistent server belongs to the launcher,
// never to this process or to the desktop window's process tree.
static class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bridge-config.json")));
            var start = new ProcessStartInfo(config.RootElement.GetProperty("engine").GetString()!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName
            };
            foreach (var arg in config.RootElement.GetProperty("engineArgs").EnumerateArray()) start.ArgumentList.Add(arg.GetString()!);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new IOException("Cannot start the account router");
            var input = Task.Run(async () =>
            {
                try { await Console.OpenStandardInput().CopyToAsync(child.StandardInput.BaseStream); child.StandardInput.Close(); }
                catch (IOException) { } catch (ObjectDisposedException) { }
            });
            var output = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
            var error = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
            await child.WaitForExitAsync(); await Task.WhenAll(output, error);
            return child.ExitCode;
        }
        catch (Exception error) { Console.Error.WriteLine("Conversation Accounts bridge: " + error.Message); return 1; }
    }
}
