using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Nightcall;

public static class Program
{
    internal static bool RestartRequested;
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--serve-install")
            {
                using var download = new InstallServer(Path.Combine(AppContext.BaseDirectory, "Nightcall.apk"));
                var addresses = download.Addresses;
                File.WriteAllBytes(args[1], InstallServer.Qr(download.Url));
                for (int i = 0; i < addresses.Length; i++)
                    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, Path.GetFileNameWithoutExtension(args[1]) + "-" + (i + 1) + ".png"), InstallServer.Qr(addresses[i].Url));
                File.WriteAllText(Path.ChangeExtension(args[1], ".json"), JsonSerializer.Serialize(addresses));
                // This explicit distribution mode serves the APK for one hour, then exits.
                Task.Delay(TimeSpan.FromHours(1)).GetAwaiter().GetResult(); return 0;
            }
            if (args.Length == 2 && args[0] == "--transcribe-file")
            {
                using var service = new Transcriber();
                var text = service.Transcribe(File.ReadAllBytes(args[1]), Settings.Load(), CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(new { text })); return 0;
            }
            if (args.Length >= 2 && args[0] is "--render-preview" or "--render-relay-preview" or "--render-install-preview")
            {
                var previewApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                NightcallTheme.Initialize(args.Contains("--night") ? true : args.Contains("--day") ? false : null);
                if (args[0] == "--render-preview") { using var preview = new Overlay(Settings.Load()); preview.SavePreview(args[1]); }
                else
                {
                    string temporaryStore = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "preview-pairing.dat");
                    try
                    {
                        using var host = new RelayHost(port: 0, storePath: temporaryStore, advertise: false); host.Start().GetAwaiter().GetResult();
                        using var microphone = new VirtualMicrophone(); var window = new RelayWindow(host, microphone);
                        window.SavePreview(args[1], args[0] == "--render-install-preview"); window.Close();
                    }
                    finally { if (File.Exists(temporaryStore)) File.Delete(temporaryStore); }
                }
                previewApp.Shutdown(); return 0;
            }
            if (args.Length == 2 && args[0] == "--verify-paste-actions")
            {
                int result = 1, enters = 0;
                var verifyApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                NightcallTheme.Initialize();
                using var verifyOverlay = new Overlay(Settings.Load());
                var editor = new TextBox { AcceptsReturn = true, Margin = new Thickness(20) };
                var window = new Window { Title = "Nightcall paste verification", Width = 460, Height = 180, Content = editor };
                editor.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { enters++; e.Handled = true; } };
                window.Loaded += async (_, _) => {
                    try {
                        window.Activate(); editor.Focus(); await Task.Delay(300);
                        var handle = new WindowInteropHelper(window).Handle;
                        await verifyOverlay.VerifyPaste(handle, "P\u0159\u00edli\u0161 \u017elu\u0165ou\u010dk\u00fd k\u016f\u0148. \u65e5\u672c\u8a9e \ud83c\udf34");
                        bool ordinary = editor.Text == "P\u0159\u00edli\u0161 \u017elu\u0165ou\u010dk\u00fd k\u016f\u0148. \u65e5\u672c\u8a9e \ud83c\udf34" && enters == 0;
                        editor.Clear(); editor.Focus();
                        await verifyOverlay.VerifyPaste(handle, "L\u00e1zn\u011b, fran\u00e7ais, \u65e5\u672c\u8a9e", true);
                        bool submit = editor.Text == "L\u00e1zn\u011b, fran\u00e7ais, \u65e5\u672c\u8a9e" && enters == 1;
                        File.WriteAllText(args[1], JsonSerializer.Serialize(new { ordinary, submit, enters })); result = ordinary && submit ? 0 : 1;
                    } catch (Exception e) { File.WriteAllText(args[1], e.ToString()); }
                    finally { window.Close(); verifyApp.Shutdown(); }
                };
                verifyApp.Run(window); return result;
            }
            using var instance = new Mutex(true, "Local\\DictationOverlay." + Environment.UserName, out bool first);
            if (!first) { MessageBox.Show("Nightcall or the previous Dictation Overlay is already running. Exit its tray app before starting this version.", "Nightcall"); return 0; }
            // Older builds may be running under another Windows account, outside
            // the per-user mutex. Do not open a second, unusable relay window.
            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, RelayProtocol.Port);
                probe.Server.ExclusiveAddressUse = true;
                try { probe.Start(); } finally { probe.Stop(); }
            }
            catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse || e.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
            {
                MessageBox.Show("Nightcall's phone port (47831) is already in use. Another copy may still be running, including one started as administrator. Exit that copy from its tray menu before starting this one.", "Nightcall is already running");
                return 0;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            NightcallTheme.Initialize();
            var overlay = new Overlay(Settings.Load());
            app.Exit += (_, _) => overlay.Dispose();
            if (args.Length == 2 && args[0] == "--test-paste")
            {
                int result = 1;
                var editor = new TextBox { AcceptsReturn = true, Margin = new Thickness(20) };
                var testWindow = new Window { Title = "Dictation paste verification", Width = 450, Height = 160, Content = editor };
                testWindow.Loaded += async (_, _) =>
                {
                    try
                    {
                        testWindow.Activate(); editor.Focus(); await Task.Delay(300);
                        using var service = new Transcriber();
                        string text = await service.Transcribe(File.ReadAllBytes(args[1]), Settings.Load(), CancellationToken.None);
                        await overlay.VerifyPaste(new WindowInteropHelper(testWindow).Handle, text);
                        bool pasted = editor.Text == text;
                        Console.WriteLine(JsonSerializer.Serialize(new { pasted, text })); result = pasted ? 0 : 1;
                    }
                    catch (Exception error) { Console.Error.WriteLine(error.Message); }
                    finally { testWindow.Close(); app.Shutdown(); }
                };
                app.Run(testWindow); return result;
            }
            overlay.Initialize();
            if (args.Length > 0 && args[0] == "--smoke-test")
            {
                overlay.Preview();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { timer.Stop(); app.Shutdown(); }; timer.Start();
            }
            app.Run();
            instance.ReleaseMutex(); instance.Dispose();
            if (RestartRequested && Environment.ProcessPath is string exe && File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length > 0) Console.Error.WriteLine(error.Message);
            else MessageBox.Show(error.Message, "Nightcall", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}

public sealed class Overlay : Window, IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }

    private readonly Settings settings;
    private readonly Transcriber transcriber = new();
    private readonly TextBlock title = new() { FontSize = 17, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Waveform waveform = new() { Height = 50, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox transcript = new() { Visibility = Visibility.Collapsed, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private Forms.NotifyIcon? tray;
    private Recorder? recorder;
    private CancellationTokenSource? cancellation;
    private byte[]? pendingAudio;
    private IntPtr target, hwnd;
    private readonly object levelGate = new();
    private double level;
    private bool busy, disposed;
    private DateTime started;
    private Window? settingsWindow;
    private RelayWindow? relayWindow;
    private RelayHost? relay;
    private InstallServer? installServer;
    private readonly VirtualMicrophone virtualMicrophone = new();
    private readonly MouseTriggerHook mouseTrigger = new();
    private string currentState = "idle";
    private string state { get => currentState; set { currentState = value; relay?.SetState(value); } }
    private int cancellationVersion;
    private bool submitAfterPaste;

    public Overlay(Settings settings)
    {
        this.settings = settings;
        NightcallTheme.Apply(this); Title = "Nightcall · Dictation";
        Width = 440; SizeToContent = SizeToContent.Height; WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        var identity = new TextBlock { Text = "NIGHTCALL", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
        NightcallTheme.Bind(identity, TextBlock.ForegroundProperty, "Secondary"); NightcallTheme.Bind(hint, TextBlock.ForegroundProperty, "Muted");
        var heading = new StackPanel(); heading.Children.Add(identity); heading.Children.Add(title);
        var mark = NightcallTheme.ArtworkImage(46); mark.Margin = new Thickness(0, 0, 14, 0);
        var header = new DockPanel(); header.Children.Add(mark); header.Children.Add(heading);
        panel.Children.Add(header); panel.Children.Add(waveform); panel.Children.Add(hint);
        panel.Children.Add(transcript); panel.Children.Add(actions);
        var shell = new Border { CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1.5), Child = panel };
        NightcallTheme.Bind(shell, Border.BackgroundProperty, "Panel"); NightcallTheme.Bind(shell, Border.BorderBrushProperty, "Accent"); Content = shell;
        timer.Tick += (_, _) =>
        {
            if (state != "recording") return;
            double current; lock (levelGate) { current = level; level *= .84; }
            waveform.Push(current);
            var duration = DateTime.UtcNow - started;
            hint.Text = $"{duration:mm\\:ss}  ·  {settings.Shortcut} to finish and paste";
            if (duration.TotalMinutes >= 5) _ = Finish();
        };
        actions.Children.Add(ActionButton("Cancel", Cancel));
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            SetStyle(hwnd, -20, new IntPtr(GetStyle(hwnd, -20).ToInt64() | 0x08000000 | 0x80));
            HwndSource.FromHwnd(hwnd).AddHook(Message);
        };
        Closing += (_, e) => { if (!disposed) { e.Cancel = true; Cancel(); } };
        IsVisibleChanged += (_, _) =>
        {
            if (hwnd == IntPtr.Zero) return;
            if (IsVisible) RegisterHotKey(hwnd, 2, 0x4000, 0x1B);
            else UnregisterHotKey(hwnd, 2);
        };
    }
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action(); return button;
    }
    private void Position()
    {
        var screen = Forms.Screen.FromHandle(target == IntPtr.Zero ? GetForegroundWindow() : target).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = screen.Left / dpi.DpiScaleX + (screen.Width / dpi.DpiScaleX - Width) / 2;
        Top = (screen.Bottom / dpi.DpiScaleY) - Math.Max(ActualHeight, 175) - 45;
    }
    public void Initialize()
    {
        new WindowInteropHelper(this).EnsureHandle();
        bool registered = RegisterHotKey(hwnd, 1, settings.Modifiers | 0x4000, settings.Key);
        mouseTrigger.Triggered += () => Dispatcher.BeginInvoke(() => _ = Toggle());
        mouseTrigger.Configure(settings.MouseTrigger);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Phone / connection…", null, (_, _) => Dispatcher.Invoke(OpenRelay));
        menu.Items.Add("Settings…", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("Start / stop dictation", null, (_, _) => Dispatcher.Invoke(() => _ = Toggle()));
        menu.Items.Add("Restart", null, (_, _) => Dispatcher.Invoke(() => { Program.RestartRequested = true; Application.Current.Shutdown(); }));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() => Application.Current.Shutdown()));
        tray = new Forms.NotifyIcon { Icon = NightcallTheme.TrayIcon, Visible = true, Text = "Nightcall — " + settings.Shortcut, ContextMenuStrip = menu };
        NightcallTheme.Changed += UpdateTrayIcon;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenRelay);
        timer.Start();
        relay = new RelayHost { SignalingServers = settings.SignalingServers, IceServersProvider = ct => settings.ResolveIceServersJson(ct) };
        relay.StereoAudio += virtualMicrophone.PushStereo;
        relay.Command += command => Dispatcher.BeginInvoke(async () =>
        {
            if (command == "cancel") Cancel();
            else if (command == "toggle") await Toggle();
            else if (command == "start") StartFromPhone();
            else if (command == "finish" && state == "recording") await Finish();
            else if (command == "finish" && state == "ready") await Paste(false);
            else if (command == "finishSend" && state == "recording") await Finish(true);
            else if (command == "finishSend" && state == "ready") await Paste(false, true);
        });
        relay.Changed += () => Dispatcher.BeginInvoke(async () =>
        {
            if (disposed) return;
            if (relay.Connected) await Task.Run(virtualMicrophone.Start);
            else
            {
                await Task.Run(virtualMicrophone.Stop);
                if (recorder?.IsPhone == true) await Finish();
            }
        });
        _ = relay.Start();
        if (Startup.Enabled) Startup.Enabled = true; // Keep an existing startup entry pointed at the renamed executable.
        if (!registered) { OpenSettings(); MessageBox.Show("The shortcut is already in use. Choose another in Settings.", "Nightcall"); }
        else { tray.BalloonTipTitle = "Nightcall is ready"; tray.BalloonTipText = settings.Shortcut + " starts recording. Press again to transcribe and paste."; tray.ShowBalloonTip(4000); }
    }
    private void UpdateTrayIcon() { if (tray != null) tray.Icon = NightcallTheme.TrayIcon; }
    private IntPtr Message(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == 1) { handled = true; _ = Toggle(); }
        if (message == 0x0312 && wParam.ToInt32() == 2 && IsVisible) { handled = true; Cancel(); }
        return IntPtr.Zero;
    }
    private async Task Toggle()
    {
        if (busy) return;
        if (state == "recording") { await Finish(true); return; }
        if (state == "error")
        {
            if (pendingAudio != null) await Recognize();
            else if (transcript.Text.Length > 0) await Paste(true);
            return;
        }
        if (state == "ready") { await Paste(true, true); return; }
        StartRecording();
    }
    private void StartFromPhone()
    {
        if (busy || relay?.Connected != true) return;
        // A wake is an idempotent start, never a finish of an existing PC overlay.
        if (state == "recording" && recorder?.IsPhone == true) return;
        bool preserveTarget = state != "idle" && IsWindow(target);
        recorder?.Dispose(); recorder = null;
        StartRecording(preserveTarget);
    }
    private void StartRecording(bool preserveTarget = false)
    {
        if (!preserveTarget) target = GetForegroundWindow();
        pendingAudio = null; transcript.Clear(); transcript.Visibility = Visibility.Collapsed;
        submitAfterPaste = false;
        try
        {
            lock (levelGate) level = 0;
            recorder = new Recorder(relay); recorder.Level += value => { double scaled = Math.Sqrt(Math.Min(1, value * 12)); lock (levelGate) level = Math.Max(level, scaled); };
            recorder.Start(); started = DateTime.UtcNow; state = "recording";
            title.Text = recorder.IsPhone ? "●  Listening · phone" : "●  Listening · PC microphone"; waveform.Clear(); waveform.Visibility = Visibility.Visible;
            RecordingActions(); Position(); Show();
        }
        catch (Exception error) { recorder?.Dispose(); recorder = null; ShowError(error.Message); }
    }
    private async Task Finish(bool submit = false)
    {
        if (busy || recorder == null) return;
        submitAfterPaste = submit;
        busy = true;
        var capture = recorder; recorder = null;
        try
        {
            pendingAudio = capture.Stop();
            if (capture.Duration.TotalSeconds < .35 || capture.Peak < .002) { Cancel(); return; }
        }
        finally { capture.Dispose(); busy = false; }
        await Recognize();
    }
    private async Task Recognize()
    {
        if (pendingAudio == null || busy) return;
        busy = true; state = "transcribing"; title.Text = "Transcribing…";
        hint.Text = "Your recording is being converted to text.";
        cancellation?.Dispose(); cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        try
        {
            var text = await transcriber.Transcribe(pendingAudio, settings, token);
            if (token.IsCancellationRequested || disposed) return;
            if (string.IsNullOrWhiteSpace(text)) { ShowError("No speech was recognized. Cancel to record again."); return; }
            pendingAudio = null; transcript.Text = text;
            if (IsWindow(target) && GetForegroundWindow() == target)
            {
                await Paste(false, submitAfterPaste);
            }
            else
            {
                state = "ready"; title.Text = "Text is ready"; waveform.Visibility = Visibility.Collapsed;
                submitAfterPaste = false; // Focus changes discard pending Enter; it requires another explicit action.
                transcript.Visibility = Visibility.Visible;
                hint.Text = "Focus changed, so nothing was pasted. Copy the text or paste into the original window.";
                SetActions(ActionButton("Copy", () => Clipboard.SetText(transcript.Text)), ActionButton("Paste & send to original", () => _ = Paste(true, true)), ActionButton("Paste only", () => _ = Paste(true)), ActionButton("Dismiss", Cancel)); Position();
            }
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) ShowError("Transcription timed out. Retry or cancel."); }
        catch (Exception error) { if (!token.IsCancellationRequested) ShowError(error.Message); }
        finally { busy = false; }
    }
    private async Task Paste(bool activate, bool submit = false)
    {
        int version = cancellationVersion;
        for (int i = 0; i < 100 && ModifiersPressed(); i++) await Task.Delay(30);
        if (version != cancellationVersion) return;
        if (ModifiersPressed()) { ShowError("Release the shortcut keys, then retry. Your text is kept here."); return; }
        if (!IsWindow(target)) { ShowError("The original window closed. Your text is still available to copy."); return; }
        if (activate) { SetForegroundWindow(target); await Task.Delay(150); }
        if (version != cancellationVersion) return;
        if (GetForegroundWindow() != target) { ShowError("Cannot focus the original window. Copy your text instead."); return; }
        string text = transcript.Text;
        DataObject? oldData = SnapshotClipboard();
        Clipboard.SetText(text);
        uint sequence = GetClipboardSequenceNumber();
        static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
        var input = new[] { Key(0x11), Key(0x56), Key(0x56, true), Key(0x11, true) };
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length)
        {
            ShowError("Windows blocked paste. The text is on your clipboard. Elevated apps may require matching permissions."); return;
        }
        if (submit)
        {
            // Give the target's paste handler time to consume the clipboard before Enter.
            await Task.Delay(150);
            if (version != cancellationVersion || GetForegroundWindow() != target || ModifiersPressed())
            {
                submitAfterPaste = false; ShowError("Text was pasted, but focus changed. Enter was not sent."); return;
            }
            var enter = new[] { Key(0x0D), Key(0x0D, true) };
            if (SendInput((uint)enter.Length, enter, Marshal.SizeOf<Input>()) != enter.Length)
            { submitAfterPaste = false; ShowError("Text was pasted, but Windows blocked Enter."); return; }
        }
        submitAfterPaste = false;
        state = "idle"; Hide();
        await Task.Delay(1000);
        if (oldData != null && GetClipboardSequenceNumber() == sequence) try { Clipboard.SetDataObject(oldData, true); } catch { }
    }
    private static bool ModifiersPressed() => (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
    private static DataObject? SnapshotClipboard()
    {
        try
        {
            var source = Clipboard.GetDataObject();
            if (source == null) return null;
            var copy = new DataObject();
            foreach (string format in source.GetFormats(false))
            {
                object? data = source.GetData(format, false);
                if (data is Stream stream)
                {
                    var bytes = new MemoryStream(); long position = stream.CanSeek ? stream.Position : 0;
                    if (stream.CanSeek) stream.Position = 0;
                    stream.CopyTo(bytes); bytes.Position = 0;
                    if (stream.CanSeek) stream.Position = position;
                    data = bytes;
                }
                else if (data is BitmapSource bitmap) { var clone = bitmap.Clone(); clone.Freeze(); data = clone; }
                else if (data is System.Drawing.Image image) data = image.Clone();
                if (data != null) copy.SetData(format, data, false);
            }
            return copy;
        }
        catch { return null; }
    }
    public async Task VerifyPaste(IntPtr window, string text, bool submit = false) { target = window; transcript.Text = text; await Paste(false, submit); }
    private static Button EnterButton(Action action)
    {
        var button = ActionButton("↵", action); button.ToolTip = "Paste and press Enter"; button.FontSize = 20; button.MinWidth = 44; button.Padding = new Thickness(10, 0, 10, 0);
        System.Windows.Automation.AutomationProperties.SetName(button, "Paste and press Enter"); return button;
    }
    private void RecordingActions() => SetActions(ActionButton("Finish & send \u21b5", () => _ = Finish(true)), ActionButton("Paste only", () => _ = Finish()), ActionButton("Cancel", Cancel));
    private void SetActions(params Button[] buttons) { actions.Children.Clear(); foreach (var button in buttons) actions.Children.Add(button); }
    private void ShowError(string message)
    {
        submitAfterPaste = false;
        state = "error"; title.Text = "Dictation needs attention"; hint.Text = message;
        SetActions(ActionButton("Retry", () => _ = Recognize()), ActionButton("Settings", OpenSettings),
            ActionButton("Copy text", () => { if (transcript.Text.Length > 0) Clipboard.SetText(transcript.Text); }), ActionButton("Cancel", Cancel));
        Position(); Show();
    }
    private void Cancel()
    {
        cancellationVersion++;
        cancellation?.Cancel(); recorder?.Dispose(); recorder = null; pendingAudio = null;
        state = "idle"; transcript.Clear(); Hide();
        submitAfterPaste = false;
    }
    public void Preview()
    {
        RecordingActions();
        title.Text = "●  Listening"; hint.Text = settings.Shortcut + " to finish, paste and press Enter";
        for (int i = 0; i < 62; i++) waveform.Push(.07 + Math.Abs(Math.Sin(i * .37) * Math.Sin(i * .13)) * .75);
        Position(); Show();
    }
    public void SavePreview(string path)
    {
        RecordingActions();
        title.Text = "●  Listening"; hint.Text = settings.Shortcut + " to finish, paste and press Enter";
        for (int i = 0; i < 62; i++) waveform.Push(.07 + Math.Abs(Math.Sin(i * .37) * Math.Sin(i * .13)) * .75);
        var content = (FrameworkElement)Content;
        content.Measure(new Size(440, 300)); content.Arrange(new Rect(0, 0, 440, content.DesiredSize.Height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(440, (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        var window = new Window { Title = "Nightcall · Settings", Width = 540, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        settingsWindow = window; window.Closed += (_, _) => settingsWindow = null;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(NightcallTheme.Header("Make yourself heard.", 68));
        panel.Children.Add(new TextBlock { Text = "Global shortcut", FontWeight = FontWeights.Bold });
        var shortcut = new TextBox { Text = settings.Shortcut, IsReadOnly = true, Margin = new Thickness(0, 6, 0, 14), Padding = new Thickness(8) };
        uint modifiers = settings.Modifiers, key = settings.Key;
        shortcut.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var pressed = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (pressed is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return;
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None) { shortcut.Text = "Include Ctrl, Alt, Shift, or Win"; return; }
            modifiers = ((mods & ModifierKeys.Control) != 0 ? 2u : 0) | ((mods & ModifierKeys.Alt) != 0 ? 1u : 0) | ((mods & ModifierKeys.Shift) != 0 ? 4u : 0) | ((mods & ModifierKeys.Windows) != 0 ? 8u : 0);
            key = (uint)KeyInterop.VirtualKeyFromKey(pressed);
            shortcut.Text = new Settings { Modifiers = modifiers, Key = key }.Shortcut;
        };
        panel.Children.Add(shortcut);
        panel.Children.Add(new TextBlock { Text = "Mouse trigger (swallows release on hold/chord so apps don't navigate)" });
        var mouseModes = new (string Id, string Label)[] { ("hold-forward", "Long-press Forward (upper side button)"), ("hold-back", "Long-press Back (lower side button)"), ("chord", "Back + Forward together"), ("off", "Off") };
        var mouseBox = new ComboBox { Margin = new Thickness(0, 6, 0, 14), Padding = new Thickness(6), ItemsSource = mouseModes.Select(m => m.Label).ToArray() };
        mouseBox.SelectedIndex = Math.Max(0, Array.FindIndex(mouseModes, m => m.Id == settings.MouseTrigger));
        panel.Children.Add(mouseBox);
        panel.Children.Add(new TextBlock { Text = "Codex login file (credentials stay in this file)" });
        var auth = new TextBox { Text = settings.AuthFile, Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(6) };
        panel.Children.Add(auth);
        panel.Children.Add(ActionButton("Browse…", () => { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Codex login|auth.json|JSON files|*.json" }; if (dialog.ShowDialog(window) == true) auth.Text = dialog.FileName; }));
        panel.Children.Add(new TextBlock { Text = "Language hint (optional: en, cs, …; blank detects automatically)", Margin = new Thickness(0, 14, 0, 6) });
        var language = new TextBox { Text = settings.Language, Padding = new Thickness(6) }; panel.Children.Add(language);
        panel.Children.Add(new TextBlock { Text = "Cloudflare TURN Key ID (optional relay fallback)", Margin = new Thickness(0, 14, 0, 4) });
        var cfKeyId = new TextBox { Text = settings.CloudflareTurnKeyId, Padding = new Thickness(6) }; panel.Children.Add(cfKeyId);
        panel.Children.Add(new TextBlock { Text = "Cloudflare TURN API Token", Margin = new Thickness(0, 8, 0, 4) });
        var cfToken = new TextBox { Text = settings.CloudflareTurnApiToken, Padding = new Thickness(6) }; panel.Children.Add(cfToken);
        panel.Children.Add(new TextBlock { Text = "Or static TURN URL (optional, e.g. turn:host:3478)", Margin = new Thickness(0, 8, 0, 4) });
        var turnUrl = new TextBox { Text = settings.TurnUrl, Padding = new Thickness(6) }; panel.Children.Add(turnUrl);
        var turnCreds = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        turnCreds.ColumnDefinitions.Add(new ColumnDefinition());
        turnCreds.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        turnCreds.ColumnDefinitions.Add(new ColumnDefinition());
        var turnUser = new TextBox { Text = settings.TurnUsername, Padding = new Thickness(6), ToolTip = "TURN username" }; Grid.SetColumn(turnUser, 0);
        var turnPass = new TextBox { Text = settings.TurnCredential, Padding = new Thickness(6), ToolTip = "TURN credential" }; Grid.SetColumn(turnPass, 2);
        turnCreds.Children.Add(turnUser); turnCreds.Children.Add(turnPass); panel.Children.Add(turnCreds);
        var note = new TextBlock { Text = "Press once to record, and again to paste. Recordings stay in memory; transcription is sent to OpenAI. Keep Nightcall running in the tray.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 12) };
        NightcallTheme.Bind(note, TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(note);
        panel.Children.Add(ActionButton("Save", () =>
        {
            if (!File.Exists(auth.Text)) { MessageBox.Show(window, "Choose an existing Codex auth.json file."); return; }
            UnregisterHotKey(hwnd, 1);
            if (!RegisterHotKey(hwnd, 1, modifiers | 0x4000, key))
            {
                RegisterHotKey(hwnd, 1, settings.Modifiers | 0x4000, settings.Key);
                MessageBox.Show(window, "That shortcut is unavailable. Choose another combination."); return;
            }
            settings.Modifiers = modifiers; settings.Key = key; settings.MouseTrigger = mouseModes[Math.Max(0, mouseBox.SelectedIndex)].Id; mouseTrigger.Configure(settings.MouseTrigger); settings.AuthFile = auth.Text; settings.Language = language.Text; settings.CloudflareTurnKeyId = cfKeyId.Text.Trim(); settings.CloudflareTurnApiToken = cfToken.Text.Trim(); settings.TurnUrl = turnUrl.Text.Trim(); settings.TurnUsername = turnUser.Text.Trim(); settings.TurnCredential = turnPass.Text.Trim(); _ = relay?.RefreshIceServers();
            try { settings.Save(); if (tray != null) tray.Text = "Nightcall — " + settings.Shortcut; window.Close(); }
            catch (Exception error) { MessageBox.Show(window, "Could not save settings: " + error.Message); }
        }));
        window.Content = panel; window.Show(); window.Activate();
    }
    private void OpenRelay()
    {
        if (relay == null) return;
        if (relayWindow != null) { relayWindow.Activate(); return; }
        relayWindow = new RelayWindow(relay, virtualMicrophone, () =>
        {
            installServer ??= new InstallServer(Path.Combine(AppContext.BaseDirectory, "Nightcall.apk"));
            return installServer.Addresses;
        });
        relayWindow.Closed += (_, _) => relayWindow = null;
        relayWindow.Show();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); cancellation?.Cancel();
        relayWindow?.Close(); installServer?.Dispose(); relay?.Dispose(); virtualMicrophone.Dispose(); mouseTrigger.Dispose();
        recorder?.Dispose(); transcriber.Dispose(); cancellation?.Dispose();
        if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, 1);
        if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, 2);
        NightcallTheme.Changed -= UpdateTrayIcon; tray?.Dispose(); settingsWindow?.Close(); Close();
    }
}

public sealed class Waveform : FrameworkElement
{
    private readonly double[] levels = new double[62];
    public void Push(double value) { Array.Copy(levels, 1, levels, 0, levels.Length - 1); levels[^1] = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    public void Clear() { Array.Clear(levels); InvalidateVisual(); }
    protected override void OnRender(DrawingContext context)
    {
        var brush = new LinearGradientBrush(((SolidColorBrush)FindResource("NightcallSecondary")).Color, ((SolidColorBrush)FindResource("NightcallAccent")).Color, 0);
        double step = ActualWidth / levels.Length;
        for (int i = 0; i < levels.Length; i++)
        {
            double height = 3 + levels[i] * (ActualHeight - 5);
            context.DrawRoundedRectangle(brush, null, new Rect(i * step + 1, (ActualHeight - height) / 2, Math.Max(1, step - 2), height), 2, 2);
        }
    }
}
