using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

static class Bootstrap
{
    const string Payload = "ConversationAccounts.Payload.zip";
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static string Pointer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConversationAccounts", "launchers",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.ProcessPath ?? AppContext.BaseDirectory)))[..16] + ".json");
    public static bool Ready(string root)
    {
        try
        {
            using var bridge = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "bridge", "bridge-config.json")));
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "bridge", "router-config.json")));
            return File.Exists(bridge.RootElement.GetProperty("engine").GetString()) &&
                File.Exists(Path.Combine(root, "bridge", "ProtocolBridge.exe")) &&
                config.RootElement.GetProperty("profiles").EnumerateObject().Any();
        }
        catch { return false; }
    }
    public static string ResolveRoot()
    {
        if (Ready(AppContext.BaseDirectory)) return AppContext.BaseDirectory;
        try
        {
            var saved = JsonNode.Parse(File.ReadAllText(Pointer))?["root"]?.GetValue<string>();
            if (saved != null) return saved;
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConversationAccounts", "environment");
    }
    public sealed record Installation(string Executable, string? PackageFamily)
    {
        public override string ToString() => Executable;
    }
    public static List<Installation> DetectInstallations()
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; @(Get-AppxPackage -Name OpenAI.Codex | ForEach-Object { $p=$_; $m=Get-AppxPackageManifest -Package $p.PackageFullName; $a=$m.Package.Applications.Application | Where-Object Id -eq App | Select-Object -First 1; if ($a) { @{exe=(Join-Path $p.InstallLocation $a.Executable);family=$p.PackageFamilyName} } }) | ConvertTo-Json -Compress");
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000)) { process.Kill(); throw new IOException("Codex installation detection timed out."); }
        if (string.IsNullOrWhiteSpace(output)) return [];
        var entries = JsonNode.Parse(output.TrimStart('\uFEFF'))!.AsArray();
        return entries.Select(entry => new Installation(entry!["exe"]!.GetValue<string>(), entry["family"]!.GetValue<string>())).ToList();
    }
    public static string? Configure(string initialRoot)
    {
        if (Assembly.GetExecutingAssembly().GetManifestResourceInfo(Payload) == null)
            throw new IOException("This development build has no installation payload. Use the packaged ConversationAccounts.exe from the release folder.");
        ApplicationConfiguration.Initialize();
        JsonObject? previous = null;
        try { previous = JsonNode.Parse(File.ReadAllText(Path.Combine(initialRoot, "bridge", "router-config.json")))?.AsObject(); } catch { }
        using var form = new Form { Text = "Conversation Accounts — setup", Width = 740, Height = 540, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 2, RowCount = 10 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        form.Controls.Add(panel);
        void Full(Control control) { panel.Controls.Add(control); panel.SetColumnSpan(control, 2); }
        Full(new Label { Text = "Create or recreate your multi-account Codex environment", AutoSize = true, Font = new System.Drawing.Font(System.Drawing.SystemFonts.DefaultFont, System.Drawing.FontStyle.Bold), Margin = new Padding(0, 0, 0, 14) });
        Full(new Label { Text = "Environment folder", AutoSize = true });
        var destination = new TextBox { Text = initialRoot, Dock = DockStyle.Fill };
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { SelectedPath = destination.Text, Description = "Choose the environment folder" }; if (picker.ShowDialog(form) == DialogResult.OK) destination.Text = picker.SelectedPath; };
        panel.Controls.Add(destination); panel.Controls.Add(browse);
        Full(new Label { Text = "Codex desktop installation to use", AutoSize = true, Margin = new Padding(0, 14, 0, 4) });
        var installation = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        var choose = new Button { Text = "Browse…", AutoSize = true };
        choose.Click += (_, _) => { using var picker = new OpenFileDialog { Filter = "Codex desktop executable|ChatGPT.exe;Codex.exe|Executables|*.exe", Title = "Choose the installed desktop app, not its CLI" }; if (picker.ShowDialog(form) == DialogResult.OK) { var item = new Installation(picker.FileName, null); installation.Items.Add(item); installation.SelectedItem = item; } };
        panel.Controls.Add(installation); panel.Controls.Add(choose);
        Full(new Label { Text = "Codex home (existing sign-in, saved accounts, and chat history)", AutoSize = true, Margin = new Padding(0, 14, 0, 4) });
        var codexHome = new TextBox { Text = previous?["sharedStore"]?.GetValue<string>() ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Dock = DockStyle.Fill };
        var homeBrowse = new Button { Text = "Browse…", AutoSize = true };
        homeBrowse.Click += (_, _) => { using var picker = new FolderBrowserDialog { SelectedPath = codexHome.Text }; if (picker.ShowDialog(form) == DialogResult.OK) codexHome.Text = picker.SelectedPath; };
        panel.Controls.Add(codexHome); panel.Controls.Add(homeBrowse);
        Full(new Label { Text = "App files and internal helpers are bundled. No compiler, Python installation, or SDK is needed. Recreate replaces app files and keeps profiles, chat history, account choices, and desktop data. The original Codex installation is not modified.", AutoSize = true, MaximumSize = new System.Drawing.Size(665, 0), Margin = new Padding(0, 16, 0, 10) });
        var status = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(665, 0), Text = "Detecting installed Codex…" }; Full(status);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var create = new Button { Text = "Create / recreate", AutoSize = true, Enabled = false };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        buttons.Controls.Add(create); buttons.Controls.Add(cancel); Full(buttons);
        string? result = null; bool installing = false;
        cancel.Click += (_, _) => form.Close(); form.FormClosing += (_, e) => { if (installing) e.Cancel = true; };
        form.Shown += async (_, _) =>
        {
            try
            {
                foreach (var item in await Task.Run(DetectInstallations)) installation.Items.Add(item);
                if (installation.Items.Count > 0) installation.SelectedIndex = 0;
                string? priorFamily = previous?["packageFamilyName"]?.GetValue<string>();
                string? priorExe = previous?["desktopExecutable"]?.GetValue<string>();
                foreach (Installation item in installation.Items)
                    if ((priorFamily != null && item.PackageFamily == priorFamily) || item.Executable.Equals(priorExe, StringComparison.OrdinalIgnoreCase)) installation.SelectedItem = item;
                status.Text = installation.Items.Count > 0 ? "Ready. Your existing accounts and history will be preserved." : "No registered Codex found. Install Codex, or browse to its desktop executable.";
            }
            catch (Exception error) { status.Text = error.Message + " You can browse to the desktop executable."; }
            create.Enabled = true;
        };
        create.Click += async (_, _) =>
        {
            if (installation.SelectedItem is not Installation selected) { MessageBox.Show(form, "Choose a Codex desktop installation first."); return; }
            installing = true; create.Enabled = cancel.Enabled = browse.Enabled = choose.Enabled = homeBrowse.Enabled = false;
            try
            {
                var progress = new Progress<string>(message => status.Text = message);
                string folder = Path.GetFullPath(destination.Text);
                string homeFolder = Path.GetFullPath(codexHome.Text);
                int accounts = await Task.Run(() => Install(folder, selected, homeFolder, progress));
                Directory.CreateDirectory(Path.GetDirectoryName(Pointer)!);
                File.WriteAllText(Pointer, JsonSerializer.Serialize(new { root = folder }, JsonOptions));
                if (accounts == 0)
                {
                    MessageBox.Show(form, "The environment is installed. No ChatGPT sign-in was found. Open normal Codex and sign in, then run this EXE again with --setup to import that login. No account credentials can be recovered from deleted files.", "Sign-in needed");
                }
                else result = folder;
                installing = false; form.Close();
            }
            catch (Exception error)
            {
                installing = false; create.Enabled = cancel.Enabled = browse.Enabled = choose.Enabled = homeBrowse.Enabled = true;
                status.Text = error.Message; MessageBox.Show(form, error.Message, "Setup could not complete");
            }
        };
        form.ShowDialog(); return result;
    }
    static string Under(string root, string relative)
    {
        string result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid bundled file path.");
        return result;
    }
    public static int Install(string root, Installation installation, string home, IProgress<string>? progress = null)
    {
        root = Path.GetFullPath(root); home = Path.GetFullPath(home);
        string appFolder = Path.GetDirectoryName(installation.Executable)!;
        string engine = Path.Combine(appFolder, "resources", "codex.exe");
        if (!File.Exists(installation.Executable) || !File.Exists(engine)) throw new IOException("Choose the desktop installation containing resources/codex.exe, not a standalone CLI executable.");
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) || root.Equals(home, StringComparison.OrdinalIgnoreCase) || root.StartsWith(appFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || root.Equals(appFolder, StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a separate environment folder, not a drive root, Codex home, or original application folder.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(Path.Combine(root, "bridge", "router-config.json")) && !File.Exists(Path.Combine(root, "conversation-accounts.install.json"))) throw new IOException("Select an empty folder or an existing Conversation Accounts environment.");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Choose a regular folder, not a directory link.");
        Protect(root);
        using var owner = new FileStream(Path.Combine(root, ".setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string marker = Under(root, "conversation-accounts.install.json");
        if (!File.Exists(marker)) File.WriteAllText(marker, "{\"version\":1,\"state\":\"installing\"}");
        progress?.Report("Unpacking bundled tools and runtimes…");
        using var archive = new ZipArchive(Assembly.GetExecutingAssembly().GetManifestResourceStream(Payload) ?? throw new IOException("Installation payload is missing."), ZipArchiveMode.Read);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(manifestStream)!;
        foreach (var (relative, hash) in manifest)
        {
            string path = Under(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                using var current = File.OpenRead(path);
                if (Convert.ToHexString(SHA256.HashData(current)).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
            }
            string temporary = path + ".setup-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var source = archive.GetEntry("blobs/" + hash)!.Open())
                using (var output = File.Create(temporary)) source.CopyTo(output);
                using (var check = File.OpenRead(temporary))
                    if (!Convert.ToHexString(SHA256.HashData(check)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Bundled file checksum mismatch.");
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        progress?.Report("Configuring the environment and importing saved logins…");
        string routerPath = Under(root, "bridge/router-config.json"), bridgePath = Under(root, "bridge/bridge-config.json");
        var config = File.Exists(routerPath) ? JsonNode.Parse(File.ReadAllText(routerPath))!.AsObject() : new JsonObject();
        var profiles = config["profiles"] as JsonObject ?? new JsonObject();
        if (config["profiles"] == null) config["profiles"] = profiles;
        var identities = new HashSet<string>();
        foreach (var profile in profiles)
            try { identities.Add(Identity(File.ReadAllText(Path.Combine(profile.Value!["home"]!.GetValue<string>(), "auth.json"))).Key); } catch { }
        if (profiles.Count == 0 && Directory.Exists(home))
        {
            var authFiles = new[] { Path.Combine(home, "auth.json") }.Concat(Directory.EnumerateFiles(home, "auth.json.*"));
            foreach (string authPath in authFiles.Where(File.Exists))
            {
                try
                {
                    string contents = File.ReadAllText(authPath);
                    var identity = Identity(contents);
                    if (!identities.Add(identity.Key)) continue;
                    int index = 1; while (profiles.ContainsKey("account-" + index)) index++;
                    string key = "account-" + index, profileHome = Under(root, "profiles/" + key);
                    Directory.CreateDirectory(profileHome); File.WriteAllText(Path.Combine(profileHome, "auth.json"), contents);
                    profiles[key] = new JsonObject { ["home"] = profileHome, ["label"] = identity.Email, ["color"] = new[] { "#268cff", "#ffb020", "#b56cff", "#20d990" }[(index - 1) % 4] };
                }
                catch (JsonException) { } catch (KeyNotFoundException) { } catch (FormatException) { } catch (InvalidOperationException) { }
            }
        }
        config["version"] = 1; config["engine"] = engine; config["desktopExecutable"] = installation.Executable;
        config["packageFamilyName"] = installation.PackageFamily;
        config["sharedStore"] = home; config["conversationSource"] = "shared-codex-home";
        config["desktopMode"] = "installed-runtime"; config["retireCopiedDesktop"] = false;
        config["shareResources"] = true; config["syncDesktopTooling"] = false; config["blockModelTurns"] = false;
        if (config["main"] == null && profiles.Count > 0) config["main"] = profiles.First().Key;
        if (config["default"] == null && profiles.Count > 0) config["default"] = profiles.First().Key;
        WriteConfig(root, routerPath, config);
        var bridge = File.Exists(bridgePath) ? JsonNode.Parse(File.ReadAllText(bridgePath))!.AsObject() : new JsonObject();
        bridge["engine"] = Under(root, "python/python.exe"); bridge["engineArgs"] = new JsonArray(Under(root, "bridge/router.py"));
        bridge["guiHome"] = home; bridge["allowedHomes"] = new JsonArray(profiles.Select(p => JsonValue.Create(p.Value!["home"]!.GetValue<string>())).ToArray());
        bridge["blockModelTurns"] = false; bridge["logPath"] = Under(root, "bridge/protocol-metadata.jsonl");
        WriteConfig(root, bridgePath, bridge);
        File.WriteAllText(Under(root, "conversation-accounts.install.json"), JsonSerializer.Serialize(new { version = 1, installedAt = DateTimeOffset.UtcNow, desktop = installation.Executable }, JsonOptions));
        string executable = Environment.ProcessPath!;
        string target = Under(root, "ConversationAccounts.exe");
        if (!Path.GetFullPath(executable).Equals(target, StringComparison.OrdinalIgnoreCase)) File.Copy(executable, target, true);
        return profiles.Count;
    }
    static (string Key, string Email) Identity(string contents)
    {
        using var auth = JsonDocument.Parse(contents.TrimStart('\uFEFF'));
        var tokens = auth.RootElement.GetProperty("tokens");
        if (string.IsNullOrWhiteSpace(tokens.GetProperty("access_token").GetString())) throw new FormatException();
        string[] parts = (tokens.GetProperty("id_token").GetString() ?? "").Split('.');
        if (parts.Length < 2) throw new FormatException("Invalid login identity token");
        string encoded = parts[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
        string email = claims.RootElement.TryGetProperty("email", out var emailValue) ? emailValue.GetString() ?? "" : claims.RootElement.GetProperty("https://api.openai.com/profile").GetProperty("email").GetString() ?? "";
        if (email.Length == 0) throw new FormatException("Missing login email");
        return (email.ToLowerInvariant() + "|" + tokens.GetProperty("account_id").GetString(), email);
    }
    static void WriteConfig(string root, string path, JsonObject config)
    {
        if (File.Exists(path))
        {
            string backup = Under(root, ".app_tmp/setup-backups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "/" + Path.GetFileName(path));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(path, backup);
        }
        string temporary = path + ".setup-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, config.ToJsonString(JsonOptions)); File.Move(temporary, path, true);
    }
    static void Protect(string root)
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var start = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { root, "/inheritance:r", "/grant:r", "*" + sid + ":(OI)(CI)F", "*S-1-5-18:(OI)(CI)F" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Cannot secure the environment folder. Choose a writable folder owned by this Windows account.");
    }
    public static void StartDesktopHelper(string root)
    {
        // Reuse a helper already running in this desktop session (possibly under
        // the Explorer user). Never create a cross-user updater or service.
        string executable = Under(root, "desktop-file-bridge/DesktopFileBridge.exe");
        if (!File.Exists(executable)) return;
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "CodexDesktopFiles-" + Process.GetCurrentProcess().SessionId, System.IO.Pipes.PipeDirection.InOut);
        try { pipe.Connect(100); return; } catch (TimeoutException) { } catch (IOException) { }
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
    }
    public static int VerifyCleanInstall(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new IOException("The setup test requires a new, nonexistent directory.");
        Directory.CreateDirectory(directory);
        string home = Path.Combine(directory, "fake-home"), app = Path.Combine(directory, "fake-app"), environment = Path.Combine(directory, "environment");
        Directory.CreateDirectory(home); Directory.CreateDirectory(Path.Combine(app, "resources"));
        File.WriteAllText(Path.Combine(app, "ChatGPT.exe"), "test fixture, never executed");
        File.WriteAllText(Path.Combine(app, "resources", "codex.exe"), "test fixture, never executed");
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"test@example.invalid\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(Path.Combine(home, "auth.json"), JsonSerializer.Serialize(new { tokens = new { access_token = "fake-test-only", account_id = "fake-test-only", id_token = "x." + encoded + ".x" } }));
        var installation = new Installation(Path.Combine(app, "ChatGPT.exe"), null);
        int accounts = Install(environment, installation, home);
        if (accounts != 1 || !Ready(environment)) throw new IOException("Fresh setup did not create a usable environment.");
        string python = Path.Combine(environment, "python", "python.exe");
        var check = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        check.ArgumentList.Add("-c"); check.ArgumentList.Add("import ssl,sqlite3,asyncio,websockets; print('private-runtime-ok')");
        using (var process = Process.Start(check)!)
        {
            string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0 || !output.Contains("private-runtime-ok")) throw new IOException("Private Python runtime failed: " + error);
        }
        string routes = Path.Combine(environment, "bridge", "thread-routes.json");
        File.WriteAllText(routes, "{\"preserved\":true}");
        string history = Path.Combine(environment, "desktop-data", "history-test");
        Directory.CreateDirectory(Path.GetDirectoryName(history)!); File.WriteAllText(history, "keep this history");
        File.WriteAllText(Path.Combine(environment, "bridge", "accounts-ui.js"), "test corruption");
        int recreated = Install(environment, installation, home);
        if (recreated != 1 || File.ReadAllText(routes) != "{\"preserved\":true}" || File.ReadAllText(history) != "keep this history" || File.ReadAllText(Path.Combine(environment, "bridge", "accounts-ui.js")) == "test corruption") throw new IOException("Recreation did not preserve data and repair app files.");
        // Prove the compiled stdio helper runs from the packaged runtime and
        // handles EOF, using an echo process rather than a model or real account.
        string bridgeConfig = Path.Combine(environment, "bridge", "bridge-config.json");
        string original = File.ReadAllText(bridgeConfig);
        var config = JsonNode.Parse(original)!.AsObject();
        config["engineArgs"] = new JsonArray("-c", "import sys; sys.stdout.write(sys.stdin.read())");
        File.WriteAllText(bridgeConfig, config.ToJsonString());
        try
        {
            var start = new ProcessStartInfo(Path.Combine(environment, "bridge", "ProtocolBridge.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            // Deliberately point away from installed .NET and Python: the package
            // must use its own runtimes on a clean computer.
            start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent-dotnet");
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            start.Environment["PYTHONHOME"] = Path.Combine(directory, "absent-python");
            using var process = Process.Start(start)!;
            process.StandardInput.Write("stdio-roundtrip-ok"); process.StandardInput.Close();
            string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0 || output != "stdio-roundtrip-ok") throw new IOException("Packaged stdio bridge failed: " + error);
        }
        finally { File.WriteAllText(bridgeConfig, original); }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { passed = true, accounts, recreated, privateRuntime = true, stdioBridge = true, preservedHistory = true }, JsonOptions));
        return 0;
    }
}
