using Microsoft.Win32;
using System.Drawing.Drawing2D;

namespace CodexQuotaTray;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--desktop-chat-rpc") return DesktopChats.RunHelper(args[1]);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception error) LogCrash(error); };
        if (args.Contains("--test-idle")) { Thread.Sleep(60000); return 0; }
        if (args.Length == 3 && args[0] == "--test-parent")
        {
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args[1], "--test-idle") { UseShellExecute = false, CreateNoWindow = true })!;
            File.WriteAllText(args[2], child.Id.ToString());
            Thread.Sleep(60000); return 0;
        }
        if (args.Contains("--fake-rpc")) return SelfTests.FakeServer();
        if (args.Length == 2 && args[0] == "--export-app-icon") { using var icon = TrayWindow.MakeIcon(args[1]); return 0; }
        if (args.Contains("--self-test")) return SelfTests.Run();
        if (args.Contains("--check-chat-recovery"))
        {
            var store = new Store();
            try
            {
                var candidates = new ChatRecovery(store, new DesktopChats(store.Data.RecoveryControllerThreadId, Path.GetFileName(Path.GetDirectoryName(store.Data.CodexHome.TrimEnd(Path.DirectorySeparatorChar))))).Capture(DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chat-recovery-diagnostic.json"), System.Text.Json.JsonSerializer.Serialize(candidates, Json.Options));
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chat-recovery-diagnostic.json"), System.Text.Json.JsonSerializer.Serialize(new { error = error.Message }));
                return 1;
            }
        }
        if (args.Contains("--check-quota"))
        {
            var store = new Store();
            var identity = Identity.Read(store.AuthPath);
            var account = new Account { Key = identity.Key, Email = identity.Email, Active = true };
            var before = File.ReadAllText(store.AuthPath);
            new QuotaService(store).Refresh(account).GetAwaiter().GetResult();
            var unchanged = File.ReadAllText(store.AuthPath) == before;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "quota-diagnostic.txt"), $"Quota read succeeded: {account.Updated != null}\nAuth unchanged: {unchanged}\n{account.Error}\n");
            return account.Updated != null && unchanged ? 0 : 1;
        }
        if (args.Contains("--render-preview"))
        {
            using var preview = new TrayWindow(true);
            preview.RenderPreview(Path.Combine(AppContext.BaseDirectory, "preview.png"));
            return 0;
        }
        using var mutex = new Mutex(true, "Local\\CodexQuotaTray", out var first);
        if (!first) { MessageBox.Show("Codex Quota Tray is already running. Click its tray icon."); return 0; }
        try { Application.Run(new TrayWindow(args.Contains("--demo"), args.Contains("--tray"))); return 0; }
        catch (Exception e) { LogCrash(e); MessageBox.Show(e.Message, "Codex Quota Tray", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
    static void LogCrash(Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "operations.log"), $"{DateTimeOffset.Now:O} Unhandled {error.GetType().Name} (0x{error.HResult:X8})\n{error.StackTrace}\n");
        }
        catch { }
    }
}

sealed class TrayWindow : Form
{
    static readonly Color Background = Color.FromArgb(15, 17, 23);
    static readonly Color Surface = Color.FromArgb(22, 25, 33);
    static readonly Color Ink = Color.FromArgb(245, 248, 255);
    public static readonly Color FiveColor = Color.FromArgb(86, 212, 180);
    public static readonly Color WeekColor = Color.FromArgb(255, 100, 255);
    static readonly Color FiveText = Color.FromArgb(105, 255, 180);
    static readonly Color WeekText = Color.FromArgb(255, 170, 255);
    readonly Store store;
    readonly bool demo;
    readonly NotifyIcon tray;
    readonly Icon trayIcon;
    readonly SmoothGrid grid = new();
    readonly Label status = new();
    readonly Label current = new();
    readonly ComboBox horizon = new();
    readonly System.Windows.Forms.Timer poll = new() { Interval = 120000 };
    readonly System.Windows.Forms.Timer clock = new() { Interval = 30000 };
    readonly System.Windows.Forms.Timer authDebounce = new() { Interval = 200 };
    FileSystemWatcher? authWatcher;
    bool authPending;
    Screen anchorScreen = Screen.PrimaryScreen!;
    ToolStripMenuItem? showAccountsItem;
    bool shortcutRegistered;
    const int ShortcutId = 0xC001;
    bool busy, quitting, dialogOpen, rendering, menuOpen;
    int recoveryGeneration;
    Func<Task>? queuedAction;
    CancellationTokenSource? refreshCancellation;
    double Hours => horizon.SelectedIndex == 0 ? 6 : 192;

    public TrayWindow(bool demoMode, bool startInTray = false)
    {
        demo = demoMode;
        store = new Store(demo ? Path.Combine(Path.GetTempPath(), "CodexQuotaTray-demo") : null);
        Text = "Codex Quota Tray"; ClientSize = new Size(620, 182);
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        BackColor = Background; ForeColor = Ink; Padding = new Padding(1);
        Font = new Font("Segoe UI", 9); ShowInTaskbar = false; TopMost = true;
        DoubleBuffered = true;
        trayIcon = MakeIcon(); Icon = trayIcon;
        horizon.Items.AddRange(["6h", "8d"]); horizon.SelectedIndex = 0;
        horizon.SelectedIndexChanged += (_, _) => DrawRows();

        grid.Dock = DockStyle.Fill; grid.BackgroundColor = Background; grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersHeight = 20; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersVisible = true;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Background, ForeColor = Ink, Font = new Font("Segoe UI", 8), SelectionBackColor = Background };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Background, ForeColor = Ink, SelectionBackColor = Color.FromArgb(25, 30, 40), SelectionForeColor = Ink, Padding = new Padding(5, 0, 5, 0) };
        grid.RowTemplate.Height = 40; grid.RowHeadersVisible = false; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Surface;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "account", HeaderText = "", Width = 182 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "five", HeaderText = "", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "week", HeaderText = "", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "resets", HeaderText = "", Width = 36 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "timeline", HeaderText = "", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 200 });
        foreach (DataGridViewColumn column in grid.Columns) { column.SortMode = DataGridViewColumnSortMode.NotSortable; column.Resizable = DataGridViewTriState.False; }
        grid.CellPainting += PaintCell;
        grid.RowPostPaint += (_, e) =>
        {
            if (!grid.Rows[e.RowIndex].Selected) return;
            using var selection = new SolidBrush(Color.FromArgb(195, 207, 226));
            e.Graphics.FillRectangle(selection, e.RowBounds.Left + 2, e.RowBounds.Top + 7, 2, e.RowBounds.Height - 14);
        };
        grid.NavigateRow += direction =>
        {
            if (grid.Rows.Count == 0) return;
            var index = grid.SelectedRows.Count == 0 ? 0 : Math.Clamp(grid.SelectedRows[0].Index + direction, 0, grid.Rows.Count - 1);
            grid.CurrentCell = grid.Rows[index].Cells[0];
            grid.ClearSelection(); grid.Rows[index].Selected = true;
        };
        grid.ActivateRow += async () =>
        {
            if (!demo && grid.CurrentRow?.Tag is Account account) await RunAction(() => Switch(account));
        };
        grid.RemoveRow += () =>
        {
            if (!demo) _ = RunAction(() => { RemoveSelectedAccount(); return Task.CompletedTask; });
        };
        grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && !demo) { var account = (Account)grid.Rows[e.RowIndex].Tag!; await RunAction(() => Switch(account)); } };
        grid.CellMouseDown += async (_, e) =>
        {
            if (demo || e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            grid.CurrentCell = row.Cells[0];
            grid.ClearSelection(); row.Selected = true;
            if (e.Button == MouseButtons.Right)
            {
                var account = (Account)row.Tag!;
                await RunAction(async () =>
                {
                    await Switch(account, startDesktop: false);
                    await LaunchCli();
                });
            }
        };
        Controls.Add(grid); grid.BringToFront();
        var menu = new ContextMenuStrip { BackColor = Surface, ForeColor = Ink, ShowImageMargin = false, Renderer = new ToolStripProfessionalRenderer(new PopupMenuColors()) };
        showAccountsItem = (ToolStripMenuItem)menu.Items.Add("Show accounts", null, (_, _) => Reveal());
        menu.Items.Add("Refresh quota", null, async (_, _) => await RunAction(RefreshAll));
        menu.Items.Add("Save current login", null, async (_, _) => await RunAction(async () => { store.CaptureCurrent(readdRemoved: true); store.Save(); await RefreshAll(); }));
        menu.Items.Add("Restart Codex", null, async (_, _) => await RunAction(async () => { ++recoveryGeneration; await CodexProcess.Stop(store.Data); await CodexProcess.Start(store.Data); }));
        menu.Items.Add("Kill Codex", null, async (_, _) => await RunAction(async () => { ++recoveryGeneration; await CodexProcess.Stop(store.Data); status.Text = "Codex desktop stopped."; }));
        menu.Items.Add(new ToolStripSeparator());
        var hoursItem = new ToolStripMenuItem("6 hours") { Checked = true };
        var weekItem = new ToolStripMenuItem("8 days");
        hoursItem.Click += (_, _) => { horizon.SelectedIndex = 0; hoursItem.Checked = true; weekItem.Checked = false; };
        weekItem.Click += (_, _) => { horizon.SelectedIndex = 1; hoursItem.Checked = false; weekItem.Checked = true; };
        void ToggleTimeline()
        {
            horizon.SelectedIndex = 1 - horizon.SelectedIndex;
            hoursItem.Checked = horizon.SelectedIndex == 0;
            weekItem.Checked = horizon.SelectedIndex == 1;
        }
        grid.ToggleTimeline += ToggleTimeline;
        menu.Items.AddRange([hoursItem, weekItem]);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, async (_, _) => await RunAction(async () =>
        {
            dialogOpen = true;
            try { using var dialog = new SettingsDialog(store); if (dialog.ShowDialog(this) == DialogResult.OK) { ConfigureAuthWatcher(); await RefreshAll(); } }
            finally { dialogOpen = false; }
        }));
        menu.Items.Add("Hide", null, (_, _) => Hide());
        menu.Items.Add("Quit", null, (_, _) => { quitting = true; Close(); });
        menu.Opening += (_, _) => { menuOpen = true; };
        menu.Closed += (_, _) =>
        {
            menuOpen = false;
            if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!ContainsFocus && !dialogOpen && !rendering) Hide(); });
        };
        grid.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Middle)
            {
                horizon.SelectedIndex = 1;
                hoursItem.Checked = false;
                weekItem.Checked = true;
            }
        };
        tray = new NotifyIcon { Icon = trayIcon, Text = "Codex Quota Tray", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { if (Visible && WindowState != FormWindowState.Minimized && ContainsFocus) Hide(); else Reveal(); } };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); else AnchorPopup(); };
        DpiChanged += (_, _) => BeginInvoke(AnchorPopup);
        Deactivate += (_, _) => { if (!dialogOpen && !rendering && !menuOpen) Hide(); };
        KeyPreview = true; KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        FormClosed += (_, _) => { poll.Dispose(); clock.Dispose(); tray.Dispose(); trayIcon.Dispose(); };
        poll.Tick += async (_, _) => { if (!busy) await RunAction(RefreshAll); };
        clock.Tick += (_, _) => DrawRows(); clock.Start();
        authDebounce.Tick += (_, _) =>
        {
            if (!authPending) { authDebounce.Stop(); return; }
            if (busy) { refreshCancellation?.Cancel(); return; }
            try
            {
                store.DiscoverBackups();
                var active = store.CaptureCurrent();
                current.Text = active?.Email ?? "";
                store.Save(); DrawRows();
                authPending = false; authDebounce.Stop();
            }
            catch (IOException) { }
            catch (System.Text.Json.JsonException) { }
            catch (InvalidOperationException) { authPending = false; authDebounce.Stop(); }
            catch (Exception) { authPending = false; authDebounce.Stop(); }
        };
        Shown += async (_, _) =>
        {
            AnchorPopup();
            if (startInTray) Hide();
            if (demo) { SeedDemo(); DrawRows(); status.Text = "Preview data • account switching disabled"; }
            else { ConfigureAuthWatcher(); await RunAction(async () => { await CodexProcess.Discover(store.Data); await RefreshAll(); }); poll.Start(); }
        };
    }
    void ConfigureAuthWatcher()
    {
        authWatcher?.Dispose(); authWatcher = null;
        if (demo || !Directory.Exists(store.Data.CodexHome)) return;
        authWatcher = new FileSystemWatcher(store.Data.CodexHome, "auth.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        void Changed(object? sender, FileSystemEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => { if (IsDisposed) return; authPending = true; authDebounce.Stop(); authDebounce.Start(); }); }
            catch (InvalidOperationException) { }
        }
        authWatcher.Changed += Changed; authWatcher.Created += Changed; authWatcher.Deleted += Changed; authWatcher.Renamed += (s, e) => Changed(s, e);
        authWatcher.EnableRaisingEvents = true;
    }
    void RemoveSelectedAccount()
    {
        var account = grid.CurrentRow?.Tag as Account ?? (grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as Account : null);
        if (account == null) return;
        var activeNote = account.Active
            ? "\n\nThis is the current login. Its live auth.json will stay in place, but the tray will hide it."
            : "";
        var answer = MessageBox.Show(this,
            $"Remove {account.Name} ({account.Email}) from the tray and delete its saved login backup?{activeNote}\n\nTo add it again, sign into that account and choose ‘Save current login.’",
            "Remove account", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        var deleted = store.Remove(account);
        DrawRows();
        status.Text = $"Removed {account.Email} from the tray and deleted {deleted} saved backup{(deleted == 1 ? "" : "s")}.";
    }
    void AnchorPopup()
    {
        if (rendering) return;
        var area = anchorScreen.WorkingArea;
        Location = new Point(area.Right - Width, area.Bottom - Height);
    }
    void Reveal() { anchorScreen = Screen.FromPoint(Cursor.Position); AnchorPopup(); Show(); WindowState = FormWindowState.Normal; Activate(); grid.Focus(); }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!demo)
        {
            shortcutRegistered = RegisterHotKey(Handle, ShortcutId, 0x0002 | 0x0004 | 0x4000, (uint)Keys.Q);
            if (showAccountsItem != null) showAccountsItem.ShortcutKeyDisplayString = shortcutRegistered ? "Ctrl+Shift+Q" : "Unavailable";
        }
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (shortcutRegistered) { UnregisterHotKey(Handle, ShortcutId); shortcutRegistered = false; }
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == ShortcutId) { Reveal(); grid.Focus(); return; }
        base.WndProc(ref m);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);
    async Task RunAction(Func<Task> action)
    {
        if (demo) return;
        if (busy) { queuedAction = action; refreshCancellation?.Cancel(); return; }
        busy = true; UseWaitCursor = true;
        try { await action(); }
        catch (Exception e)
        {
            status.Text = e.Message;
            tray.ShowBalloonTip(6000, "Codex Quota Tray", status.Text, ToolTipIcon.Error);
            try { File.AppendAllText(Path.Combine(store.DirectoryPath, "operations.log"), $"{DateTimeOffset.Now:O} Action failed: {e.GetType().Name} (0x{e.HResult:X8})\n{e}\n"); } catch { }
        }
        finally { busy = false; UseWaitCursor = false; DrawRows(); }
        if (queuedAction != null) { var next = queuedAction; queuedAction = null; await RunAction(next); }
    }
    async Task RefreshAll()
    {
        using var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;
        try
        {
        store.DiscoverBackups();
        var active = store.CaptureCurrent();
        current.Text = active == null ? "No current ChatGPT login found. Sign in through Codex, then refresh." : $"Logged in: {active.Email}";
        var service = new QuotaService(store);
        foreach (var account in store.Data.Accounts)
        {
            if (queuedAction != null) break;
            status.Text = $"Reading quota for {account.Email}…";
            await service.Refresh(account, cancellation.Token); DrawRows();
        }
        store.Save();
        status.Text = $"Updated {DateTime.Now:t} • {store.Data.Accounts.Count} saved accounts • refreshes every 2 minutes";
        if (store.Data.Accounts.Any(a => a.Error != null)) status.Text += " • Some reads failed; hover rows for details";
        }
        finally { refreshCancellation = null; }
    }
    async Task Switch(Account target, bool startDesktop = true)
    {
        var generation = ++recoveryGeneration;
        CodexProcess.ValidateLaunch(store.Data);
        store.RepairBackupPaths();
        if (!File.Exists(target.Backup)) throw new InvalidOperationException($"No saved login for {target.Email}. Sign in to that account in Codex, then refresh to save it.");
        if (Identity.Read(target.Backup).Key != target.Key) throw new InvalidOperationException("Saved login identity mismatch.");
        var selectedContents = File.ReadAllText(target.Backup);
        status.Text = $"Switching to {target.Email}…";
        store.CaptureCurrent();
        var original = File.Exists(store.AuthPath) ? File.ReadAllText(store.AuthPath) : null;
        var stopped = false;
        var stage = "stopping Codex";
        try
        {
            await CodexProcess.Stop(store.Data);
            stopped = true;
            stage = "replacing login";
            store.CaptureCurrent();
            original = File.Exists(store.AuthPath) ? File.ReadAllText(store.AuthPath) : original;
            Store.AtomicWrite(store.AuthPath, selectedContents);
            if (Identity.Read(store.AuthPath).Key != target.Key) throw new InvalidOperationException("Authentication replacement could not be verified.");
            foreach (var a in store.Data.Accounts) a.Active = a.Key == target.Key;
            store.Save(); DrawRows();
            if (startDesktop)
            {
                stage = "starting Codex";
                await CodexProcess.Start(store.Data);
            }
        }
        catch (Exception error)
        {
            if (original != null) Store.AtomicWrite(store.AuthPath, original);
            else if (File.Exists(store.AuthPath)) File.Delete(store.AuthPath);
            try { store.CaptureCurrent(); } catch { }
            if (stopped && startDesktop) try { await CodexProcess.Start(store.Data); } catch { }
            throw new InvalidOperationException($"Switch failed while {stage}; the previous auth.json was restored. {error.Message}", error);
        }
        foreach (var a in store.Data.Accounts) a.Active = a.Key == target.Key;
        store.Save(); current.Text = $"Logged in: {target.Email}";
        status.Text = startDesktop
            ? $"Switched to {target.Email}; Codex launch requested."
            : $"Switched to {target.Email}; ready to launch Codex CLI.";
        if (startDesktop && store.Data.ResumeRecentChatsAfterSwitch) _ = RecoverAfterRestart(generation);
    }
    Task LaunchCli()
    {
        var command = DesktopCommand.Parse($"\"{store.Data.CliPath}\" {store.Data.CliArguments}");
        if (string.IsNullOrWhiteSpace(command.Executable) || !File.Exists(command.Executable))
            throw new InvalidOperationException($"Codex CLI was not found at:\n{command.Executable}\nSet its path in Settings.");
        var info = new System.Diagnostics.ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = store.Data.CodexHome,
            CreateNoWindow = false,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        info.Environment["CODEX_HOME"] = store.Data.CodexHome;
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Could not start Codex CLI.");
        status.Text = "Started Codex CLI for the active account: " + string.Join(" ", command.Arguments);
        return Task.CompletedTask;
    }
    async Task RecoverAfterRestart(int generation)
    {
        // All discovery runs after restart and outside the switch action / busy queue.
        await Task.Delay(10000);
        if (IsDisposed || generation != recoveryGeneration) return;
        try
        {
            var readiness = System.Diagnostics.Stopwatch.StartNew();
            ChatRecovery? recovery = null;
            List<RecoveryCandidate> candidates = [];
            for (var attempt = 0; ; attempt++)
            {
                if (IsDisposed || generation != recoveryGeneration) return;
                // Rediscover on each retry: a restart/account change invalidates old pipe names.
                var chats = new DesktopChats(store.Data.RecoveryControllerThreadId, Path.GetFileName(Path.GetDirectoryName(store.Data.CodexHome.TrimEnd(Path.DirectorySeparatorChar))));
                recovery = new ChatRecovery(store, chats);
                try { candidates = await recovery.Capture(DateTimeOffset.UtcNow); break; }
                catch (Exception error) when (readiness.Elapsed < TimeSpan.FromMinutes(2))
                {
                    LogRecoveryInfo($"Waiting for messaging bridge, attempt {attempt + 1}: {error.Message}");
                    await Task.Delay(5000);
                    if (IsDisposed || generation != recoveryGeneration) return;
                }
            }
            if (IsDisposed || generation != recoveryGeneration) return;
            LogRecoveryInfo($"After restart: found {candidates.Count} recent unfinished chats.");
            var resumed = await recovery.Recover(candidates, DateTimeOffset.UtcNow);
            LogRecoveryInfo($"After restart: continued {resumed} chats.");
        }
        catch (Exception error)
        {
            if (IsDisposed || generation != recoveryGeneration) return;
            LogRecovery(error);
            if (!IsDisposed) tray.ShowBalloonTip(6000, "Codex chat recovery", "Codex restarted, but chat recovery failed. See chat-recovery.log beside the switcher.", ToolTipIcon.Warning);
        }
    }
    void LogRecoveryInfo(string message)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "chat-recovery.log"), $"{DateTimeOffset.Now:O} user={Environment.UserName}: {message}\n"); } catch { }
    }
    void LogRecovery(Exception error)
    {
        LogRecoveryInfo(error.ToString());
        try { File.AppendAllText(Path.Combine(store.DirectoryPath, "operations.log"), $"{DateTimeOffset.Now:O} Chat recovery: {error.GetType().Name}: {error.Message}\n"); } catch { }
    }
    void DrawRows()
    {
        var selected = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as Account : null;
        grid.Rows.Clear();
        var now = DateTimeOffset.Now;
        foreach (var a in store.Data.Accounts.OrderByDescending(a => (Hours == 6 ? a.FiveHour : a.Weekly)?.Density(now) ?? double.NegativeInfinity).ThenBy(a => a.Email))
        {
            string Percent(QuotaWindow? w) => w == null ? "—" : w.Remaining.ToString("0.#") + "%";
            var row = grid.Rows[grid.Rows.Add($"{(a.Active ? "● " : "")}{a.Name}\n{a.Email}", Percent(a.FiveHour), Percent(a.Weekly), a.Resets?.ToString() ?? "—", "")];
            row.Tag = a; row.Cells[0].Style.WrapMode = DataGridViewTriState.True;
            var stale = a.Updated == null || DateTimeOffset.Now - a.Updated > TimeSpan.FromMinutes(5);
            var detail = $"{a.Email}\n{(a.Updated == null ? "No quota data yet" : "Last read: " + a.Updated.Value.ToLocalTime().ToString("g"))}{(stale ? " • stale" : "")}\n{a.Error}";
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = detail;
            row.Cells[1].Style.ForeColor = FiveText;
            row.Cells[2].Style.ForeColor = WeekText;
        }
        grid.Invalidate();
        grid.ClearSelection();
        if (selected != null)
        {
            var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ((Account)r.Tag!).Key == selected.Key);
            if (row != null) { grid.CurrentCell = row.Cells[0]; row.Selected = true; }
        }
        ClientSize = new Size(620, grid.ColumnHeadersHeight + Math.Clamp(grid.Rows.Count, 1, 8) * grid.RowTemplate.Height + 3);
        AnchorPopup();
        var active = store.Data.Accounts.FirstOrDefault(a => a.Active);
        tray.Text = active == null ? "Codex Quota Tray" : ("Codex: " + active.Email)[..Math.Min(63, ("Codex: " + active.Email).Length)];
    }
    void PaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex >= 0)
        {
            var account = (Account)grid.Rows[e.RowIndex].Tag!;
            if (account.Active)
            {
                using var rainbow = RainbowBrush(new Rectangle(0, e.CellBounds.Top, grid.ClientSize.Width, e.CellBounds.Height), false);
                e.Graphics!.FillRectangle(rainbow, e.CellBounds);
                using var edge = RainbowBrush(new Rectangle(0, e.CellBounds.Top, grid.ClientSize.Width, e.CellBounds.Height), true);
                e.Graphics.FillRectangle(edge, e.CellBounds.Left, e.CellBounds.Bottom - 2, e.CellBounds.Width, 2);
            }
            else e.PaintBackground(e.ClipBounds, true);
            if (e.ColumnIndex == 0)
            {
                var r0 = e.CellBounds;
                using var name = new Font("Segoe UI Semibold", 8.7f);
                using var email = new Font("Segoe UI", 7.5f);
                TextRenderer.DrawText(e.Graphics!, account.Name, name, new Rectangle(r0.Left + 9, r0.Top + 4, r0.Width - 17, 17), Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics!, account.Email, email, new Rectangle(r0.Left + 9, r0.Top + 21, r0.Width - 17, 14), Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                e.Handled = true; return;
            }
            if (e.ColumnIndex is >= 1 and <= 3)
            {
                var value = e.FormattedValue?.ToString() ?? "—";
                var suffix = e.ColumnIndex == 1 ? "5h" : e.ColumnIndex == 2 ? "wk" : "R";
                using var suffixFont = new Font("Segoe UI", 7);
                var valueFont = e.CellStyle!.Font ?? Font;
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                var valueWidth = TextRenderer.MeasureText(value, valueFont, Size.Empty, flags).Width;
                var suffixWidth = TextRenderer.MeasureText(suffix, suffixFont, Size.Empty, flags).Width;
                var x = e.CellBounds.Left + (e.CellBounds.Width - valueWidth - suffixWidth - 3) / 2;
                TextRenderer.DrawText(e.Graphics!, value, valueFont, new Rectangle(x, e.CellBounds.Top, valueWidth, e.CellBounds.Height), e.CellStyle.ForeColor, flags | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(e.Graphics!, suffix, suffixFont, new Rectangle(x + valueWidth + 3, e.CellBounds.Top + 1, suffixWidth, e.CellBounds.Height), e.CellStyle.ForeColor, flags | TextFormatFlags.VerticalCenter);
                e.Handled = true; return;
            }
            if (e.ColumnIndex != 4) { e.PaintContent(e.ClipBounds); e.Handled = true; return; }
        }
        if (e.ColumnIndex != 4) return;
        if (e.RowIndex < 0) e.PaintBackground(e.ClipBounds, false);
        var r = e.CellBounds; var left = r.Left + 9; var width = Math.Max(1, r.Width - 18);
        var graphics = e.Graphics!;
        var activeRow = e.RowIndex >= 0 && ((Account)grid.Rows[e.RowIndex].Tag!).Active;
        using var gridHue = RainbowBrush(new Rectangle(0, r.Top, grid.ClientSize.Width, r.Height), false, .40);
        using var dim = activeRow ? new Pen(gridHue, 1) : new Pen(Color.FromArgb(61, 72, 90));
        using var brush = new SolidBrush(Ink);
        using var font = new Font("Segoe UI", 8);
        if (e.RowIndex < 0)
        {
            foreach (var (fraction, label) in TimelineTicks())
            {
                if (fraction == 0) continue;
                var x = left + width * (float)fraction;
                var size = graphics.MeasureString(label, font);
                graphics.DrawString(label, font, brush, Math.Clamp(x - (fraction == 0 ? 0 : size.Width / 2), left, left + width - size.Width), r.Top + 2);
            }
        }
        else
        {
            if (Hours == 192)
            {
                using var minorHue = RainbowBrush(new Rectangle(0, r.Top, grid.ClientSize.Width, r.Height), false, .30);
                using var minor = activeRow ? new Pen(minorHue, .45f) : new Pen(Color.FromArgb(33, 39, 49), .45f);
                var smoothing = graphics.SmoothingMode;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var now = DateTimeOffset.Now;
                var tick = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddHours((now.Hour / 6 + 1) * 6);
                for (; (tick - now).TotalHours <= Hours; tick = tick.AddHours(6))
                {
                    if (tick.ToLocalTime().Hour == 0) continue;
                    var x = left + (float)((tick - now).TotalHours / Hours * width);
                    graphics.DrawLine(minor, x, r.Top, x, r.Bottom);
                }
                graphics.SmoothingMode = smoothing;
            }
            foreach (var (fraction, _) in TimelineTicks())
            {
                var x = left + width * (float)fraction;
                graphics.DrawLine(dim, x, r.Top, x, r.Bottom);
            }
            var a = (Account)grid.Rows[e.RowIndex].Tag!;
            DrawReset(a.FiveHour, FiveColor, "5h", 0);
            DrawReset(a.Weekly, WeekColor, "wk", 1);
            void DrawReset(QuotaWindow? window, Color color, string label, int lane)
            {
                if (window?.Reset is not long stamp) return;
                DateTimeOffset reset;
                try { reset = DateTimeOffset.FromUnixTimeSeconds(stamp); } catch { return; }
                var hours = (reset - DateTimeOffset.Now).TotalHours;
                using var marker = new Pen(color, 2);
                using var text = new SolidBrush(label == "5h" ? FiveText : WeekText);
                using var outline = new SolidBrush(Color.FromArgb(225, 5, 7, 12));
                if (hours < 0 || hours > Hours)
                {
                    if (hours < 0) DrawCaption(label + " ↻", left + 5, r.Top + 3 + lane * 18);
                    return;
                }
                var x = left + (float)(hours / Hours * width);
                graphics.DrawLine(marker, x, r.Top, x, r.Bottom);
                var caption = Hours == 192 ? label + " " + reset.ToLocalTime().ToString("ddd HH:mm") : "+" + hours.ToString("0.#") + "h";
                var captionWidth = graphics.MeasureString(caption, font).Width;
                DrawCaption(caption, Math.Clamp(x + 5, left + 4, left + Math.Max(4, width - captionWidth)), r.Top + 3 + lane * 18);
                grid.Rows[e.RowIndex].Cells[4].ToolTipText = $"5h: {ResetText(a.FiveHour)}\nWeekly: {ResetText(a.Weekly)}\n{a.Error}";
                void DrawCaption(string caption, float x, float y)
                {
                    graphics.DrawString(caption, font, outline, x - .75f, y);
                    graphics.DrawString(caption, font, outline, x + .75f, y);
                    graphics.DrawString(caption, font, outline, x, y - .75f);
                    graphics.DrawString(caption, font, outline, x, y + .75f);
                    graphics.DrawString(caption, font, text, x, y);
                }
            }
        }
        e.Handled = true;
    }
    IEnumerable<(double Fraction, string Label)> TimelineTicks()
    {
        var now = DateTimeOffset.Now;
        yield return (0, "now");
        var step = Hours == 6 ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var tick = Hours == 6
            ? new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(1)
            : new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(1);
        for (; (tick - now).TotalHours <= Hours; tick += step)
            yield return ((tick - now).TotalHours / Hours, tick.ToLocalTime().ToString(Hours == 6 ? "HH:mm" : "ddd"));
    }
    static LinearGradientBrush RainbowBrush(Rectangle bounds, bool bright, double intensity = .26)
    {
        var colors = new[] { Color.FromArgb(255, 104, 133), Color.FromArgb(255, 181, 101), Color.FromArgb(225, 217, 110), Color.FromArgb(98, 219, 163), Color.FromArgb(97, 183, 245), Color.FromArgb(168, 128, 243), Color.FromArgb(237, 115, 199) };
        if (!bright) colors = colors.Select(c => Color.FromArgb((int)(Background.R * (1 - intensity) + c.R * intensity), (int)(Background.G * (1 - intensity) + c.G * intensity), (int)(Background.B * (1 - intensity) + c.B * intensity))).ToArray();
        var brush = new LinearGradientBrush(bounds, colors[0], colors[^1], 0f);
        brush.InterpolationColors = new ColorBlend { Colors = colors, Positions = [0f, 1f / 6, 2f / 6, .5f, 4f / 6, 5f / 6, 1f] };
        return brush;
    }
    static string ResetText(QuotaWindow? w) => w?.Reset is long stamp ? DateTimeOffset.FromUnixTimeSeconds(stamp).ToLocalTime().ToString("f") : "Unknown";
    internal static Icon MakeIcon(string? artworkPath = null)
    {
        if (artworkPath == null)
        {
            using var stream = typeof(TrayWindow).Assembly.GetManifestResourceStream("CodexQuotaTray.Scroll.ico");
            if (stream != null) return new Icon(stream, new Size(32, 32));
        }
        using var artwork = new Bitmap(1024, 1024);
        using (var g = Graphics.FromImage(artwork))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
            g.ScaleTransform(32, 32);
            using var outline = new Pen(Color.FromArgb(119, 78, 34), 1.15f) { LineJoin = LineJoin.Round };
            using var paper = new LinearGradientBrush(new Rectangle(7, 4, 19, 24), Color.FromArgb(255, 243, 191), Color.FromArgb(222, 181, 107), 0f);
            using var body = new GraphicsPath();
            body.AddLines([new PointF(7, 6), new PointF(26, 6), new PointF(25, 24)]);
            body.AddBezier(25, 24, 25, 28, 21, 29, 18, 29);
            body.AddLine(18, 29, 6, 29); body.AddLine(6, 29, 7, 6); body.CloseFigure();
            g.FillPath(paper, body); g.DrawPath(outline, body);
            DrawRoll(3, 3, 23, 7, true);
            using var ink = new Pen(Color.FromArgb(147, 104, 50), 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(ink, 11, 13, 22, 13); g.DrawLine(ink, 11, 16.5f, 22, 16.5f); g.DrawLine(ink, 11, 20, 19, 20);
            DrawRoll(3, 23, 18, 6, false);
            void DrawRoll(float x, float y, float width, float height, bool top)
            {
                using var roll = new GraphicsPath();
                roll.AddArc(x, y, height, height, 90, 180);
                roll.AddLine(x + height / 2, y, x + width, y);
                roll.AddArc(x + width - height / 2, y, height, height, 270, 180);
                roll.CloseFigure();
                using var shading = new LinearGradientBrush(new RectangleF(x, y, width + height / 2, height), Color.FromArgb(255, 248, 207), Color.FromArgb(213, 165, 87), 90f);
                g.FillPath(shading, roll); g.DrawPath(outline, roll);
                var curlX = top ? x + width - height / 2 : x;
                using var curl = new SolidBrush(Color.FromArgb(201, 151, 77));
                g.FillEllipse(curl, curlX, y, height, height); g.DrawEllipse(outline, curlX, y, height, height);
                using var inner = new Pen(Color.FromArgb(250, 226, 166), 1);
                g.DrawArc(inner, curlX + 1.8f, y + 1.4f, height - 3, height - 2.8f, 55, 260);
            }
        }
        if (artworkPath != null) artwork.Save(artworkPath, System.Drawing.Imaging.ImageFormat.Png);
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(artwork, 0, 0, 32, 32); }
        var handle = bitmap.GetHicon();
        try { using var native = Icon.FromHandle(handle); return (Icon)native.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    void SeedDemo()
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        store.Data.Accounts = [
            new() { Key = "1", Name = "Personal", Email = "personal@example.com", Active = true, FiveHour = new(36, 300, now + 7200), Weekly = new(68, 10080, now + 220000), Resets = 2, Updated = DateTimeOffset.Now },
            new() { Key = "2", Name = "Work", Email = "work@example.com", FiveHour = new(81, 300, now + 14000), Weekly = new(42, 10080, now + 410000), Resets = 0, Updated = DateTimeOffset.Now },
            new() { Key = "3", Name = "Projects", Email = "projects@example.com", FiveHour = new(5, 300, now + 17000), Weekly = new(12, 10080, now + 580000), Updated = DateTimeOffset.Now }
        ];
        current.Text = "Logged in: personal@example.com  •  Preview";
    }
    public void RenderPreview(string path)
    {
        rendering = true;
        tray.Visible = false; SeedDemo(); DrawRows();
        status.Text = "Preview data • account switching disabled";
        // Build actual native child handles before WM_PRINT renders the form.
        StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000);
        ShowInTaskbar = false; Show(); Application.DoEvents(); PerformLayout(); grid.PerformLayout();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        horizon.SelectedIndex = 1;
        grid.Invalidate(); Application.DoEvents();
        using var weekly = new Bitmap(Width, Height);
        DrawToBitmap(weekly, new Rectangle(0, 0, Width, Height));
        weekly.Save(Path.Combine(Path.GetDirectoryName(path)!, "preview-weekly.png"), System.Drawing.Imaging.ImageFormat.Png);
        horizon.SelectedIndex = 0;
        using var iconPreview = trayIcon.ToBitmap();
        iconPreview.Save(Path.Combine(Path.GetDirectoryName(path)!, "tray-icon.png"), System.Drawing.Imaging.ImageFormat.Png);
        Hide();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { authWatcher?.Dispose(); authDebounce.Dispose(); poll.Dispose(); clock.Dispose(); tray.Dispose(); trayIcon.Dispose(); horizon.Dispose(); status.Dispose(); current.Dispose(); }
        base.Dispose(disposing);
    }
}

sealed class SmoothGrid : DataGridView
{
    public event Action<int>? NavigateRow;
    public event Action? ActivateRow;
    public event Action? RemoveRow;
    public event Action? ToggleTimeline;
    public SmoothGrid() { DoubleBuffered = true; }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Up) { NavigateRow?.Invoke(-1); return true; }
        if (keyData == Keys.Down) { NavigateRow?.Invoke(1); return true; }
        if (keyData == Keys.Enter) { ActivateRow?.Invoke(); return true; }
        if (keyData == Keys.Delete) { RemoveRow?.Invoke(); return true; }
        if (keyData == Keys.Tab) { ToggleTimeline?.Invoke(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

sealed class PopupMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(22, 25, 33);
    public override Color MenuBorder => Color.FromArgb(47, 53, 67);
    public override Color MenuItemSelected => Color.FromArgb(43, 50, 65);
    public override Color MenuItemBorder => Color.FromArgb(60, 71, 91);
    public override Color CheckBackground => Color.FromArgb(43, 50, 65);
    public override Color CheckSelectedBackground => CheckBackground;
    public override Color SeparatorDark => MenuBorder;
    public override Color SeparatorLight => ToolStripDropDownBackground;
}

sealed class SettingsDialog : Form
{
    public SettingsDialog(Store store)
    {
        Text = "Codex Quota Tray settings"; Size = new Size(710, 440); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false;
        Font = new Font("Segoe UI", 10);
        var home = Field("Codex home", store.Data.CodexHome, 20, false);
        var desktop = Field("Codex desktop executable or launcher", store.Data.DesktopPath, 77, true);
        var cli = Field("Codex CLI executable", store.Data.CliPath, 134, true);
        var cliArguments = Field("CLI arguments after switching to the clicked account", store.Data.CliArguments, 191, false);
        var startup = new CheckBox { Text = "Start in the tray when Windows starts", Checked = store.Data.StartWithWindows, AutoSize = true, Location = new Point(20, 248) }; Controls.Add(startup);
        var recovery = new CheckBox { Text = "Continue recent interrupted and quota-stopped chats after switching accounts", Checked = store.Data.ResumeRecentChatsAfterSwitch, AutoSize = true, Location = new Point(20, 278) }; Controls.Add(recovery);
        var note = new Label { Text = "Backups: auth.json.<email> in Codex home. These files contain login credentials.\nSettings: " + Path.Combine(store.DirectoryPath, "settings.json"), AutoSize = true, Location = new Point(20, 310) }; Controls.Add(note);
        var save = new Button { Text = "Save", Location = new Point(570, 365), Size = new Size(100, 30) }; Controls.Add(save); AcceptButton = save;
        save.Click += (_, _) =>
        {
            if (!Directory.Exists(home.Text)) { MessageBox.Show($"Codex home folder does not exist:\n{home.Text}"); return; }
            if (!File.Exists(DesktopCommand.Parse(desktop.Text).Executable)) { MessageBox.Show($"Codex desktop executable or launcher does not exist:\n{DesktopCommand.Parse(desktop.Text).Executable}\nQuote executable paths containing spaces before adding arguments."); return; }
            if (!File.Exists(cli.Text.Trim())) { MessageBox.Show($"Codex CLI executable does not exist:\n{cli.Text}"); return; }
            try { _ = DesktopCommand.Parse($"\"{cli.Text.Trim()}\" {cliArguments.Text}"); }
            catch (Exception error) { MessageBox.Show("Invalid Codex CLI command: " + error.Message); return; }
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (startup.Checked) run.SetValue("CodexQuotaTray", "\"" + Environment.ProcessPath + "\" --tray");
                else run.DeleteValue("CodexQuotaTray", false);
                store.Data.StartWithWindows = startup.Checked;
                store.Data.ResumeRecentChatsAfterSwitch = recovery.Checked;
                if (!string.Equals(desktop.Text, store.Data.DesktopPath, StringComparison.OrdinalIgnoreCase)) store.Data.DesktopAppId = "";
                var previousHome = store.Data.CodexHome;
                store.Data.CodexHome = Path.GetFullPath(home.Text); store.Data.DesktopPath = desktop.Text.Trim();
                store.Data.CliPath = Path.GetFullPath(cli.Text.Trim()); store.Data.CliArguments = cliArguments.Text.Trim();
                // Switching Codex home starts a separate inventory; old backups stay where they were.
                store.Data.Accounts = store.Data.Accounts.Where(a => string.Equals(Path.GetDirectoryName(a.Backup), store.Data.CodexHome, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!string.Equals(Path.GetFullPath(previousHome), store.Data.CodexHome, StringComparison.OrdinalIgnoreCase)) store.Data.RemovedAccountKeys.Clear();
                store.Save(); DialogResult = DialogResult.OK;
            }
            catch { MessageBox.Show("Could not save settings. Check permissions."); }
        };
        TextBox Field(string label, string value, int y, bool browse)
        {
            Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(20, y) });
            var box = new TextBox { Text = value, Location = new Point(20, y + 23), Width = browse ? 565 : 650 }; Controls.Add(box);
            if (browse)
            {
                var button = new Button { Text = "Browse", Location = new Point(593, y + 21), Size = new Size(77, 28) }; Controls.Add(button);
                button.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe" }; if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName; };
            }
            return box;
        }
    }
}





