using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Nightcall;

public sealed class RelayWindow : Window
{
    private readonly RelayHost relay;
    private readonly VirtualMicrophone microphone;
    private readonly StackPanel paired = new();
    private readonly StackPanel downloads = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
    private readonly Image qr = new() { Width = 300, Height = 300, Visibility = Visibility.Collapsed };
    private readonly TextBlock pairingHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
    private readonly CheckBox mute = new() { Content = "Mute phone microphone", Margin = new Thickness(0, 10, 0, 10) };
    private readonly Border qrFrame;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime expires;
    private string phoneList = "";
    public RelayWindow(RelayHost relay, VirtualMicrophone microphone, Func<InstallServer.Address[]>? installUrl = null)
    {
        this.relay = relay; this.microphone = microphone;
        NightcallTheme.Apply(this);
        Title = "Nightcall"; Width = 480; SizeToContent = SizeToContent.Height; MaxHeight = 920;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(NightcallTheme.Header("Your voice. Across the airwaves."));
        var connection = new StackPanel();
        var label = new TextBlock { Text = "THE LINE", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
        NightcallTheme.Bind(label, TextBlock.ForegroundProperty, "Secondary"); connection.Children.Add(label); connection.Children.Add(status);
        var statusCard = NightcallTheme.Card(connection); statusCard.Margin = new Thickness(0, 0, 0, 14); panel.Children.Add(statusCard);
        var controls = new StackPanel(); controls.Children.Add(mute);
        mute.Click += (_, _) => relay.Muted = mute.IsChecked == true;
        var startup = new CheckBox { Content = "Start with Windows", IsChecked = Startup.Enabled, Margin = new Thickness(0, 0, 0, 16) };
        startup.Click += (_, _) => { try { Startup.Enabled = startup.IsChecked == true; } catch (Exception e) { startup.IsChecked = Startup.Enabled; MessageBox.Show(this, e.Message); } };
        controls.Children.Add(startup); var controlCard = NightcallTheme.Card(controls, new Thickness(20, 8, 20, 2)); controlCard.Margin = new Thickness(0, 0, 0, 18); panel.Children.Add(controlCard);
        if (installUrl != null)
        {
            var install = new Button { Content = "Install Android app", Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 8) };
            install.Click += (_, _) =>
            {
                try
                {
                    ShowDownloads(installUrl());
                }
                catch (Exception e) { MessageBox.Show(this, e.Message); }
            };
            panel.Children.Add(install);
        }
        var pair = new Button { Content = "Pair a phone", Padding = new Thickness(10, 7, 10, 7) };
        pair.Click += (_, _) => { try { ShowPairing(); } catch (Exception e) { MessageBox.Show(this, e.Message); } };
        panel.Children.Add(pair);
        var copyTrace = new Button { Content = "Copy connection trace", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 0, 0) };
        copyTrace.Click += (_, _) => { var t = relay.TraceText; Clipboard.SetText(string.IsNullOrEmpty(t) ? "(no trace yet)" : t); };
        panel.Children.Add(copyTrace);
        qrFrame = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(18), Padding = new Thickness(18), Child = qr, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 16, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(pairingHint); panel.Children.Add(downloads); panel.Children.Add(qrFrame); panel.Children.Add(paired);
        var footer = new TextBlock { Text = "Start or finish dictation from either device. When your phone is away, Nightcall uses the PC microphone.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0), FontSize = 12 };
        NightcallTheme.Bind(footer, TextBlock.ForegroundProperty, "Muted"); NightcallTheme.Bind(pairingHint, TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(footer);
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        NightcallTheme.Bind(scroll, Control.BackgroundProperty, "Backdrop"); Content = scroll;
        timer.Tick += (_, _) => Refresh(); timer.Start(); Refresh();
        Closed += (_, _) => { timer.Stop(); relay.EndPairing(); };
    }
    private void ShowDownloads(InstallServer.Address[] addresses)
    {
        relay.EndPairing(); qr.Source = null; qr.Visibility = Visibility.Collapsed; qrFrame.Visibility = Visibility.Collapsed;
        downloads.Children.Clear(); downloads.Visibility = Visibility.Visible;
        if (addresses.Length == 0) throw new InvalidOperationException("No active IPv4 network. Connect the PC to Wi-Fi or enable its hotspot, then try again.");
        foreach (var address in addresses)
        {
            var card = new StackPanel { Margin = new Thickness(0, 16, 0, 8) };
            card.Children.Add(new TextBlock { Text = address.Network, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new TextBox { Text = address.Url, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
            card.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Center, Child = new Image { Width = 260, Height = 260, Source = QrBitmap(address.Url) } });
            downloads.Children.Add(card);
        }
        expires = DateTime.MaxValue;
        pairingHint.Text = "Scan the code for the network shared with your phone. Each address downloads the same Android app. If one cannot connect, try another. After installing, tap Pair a phone.";
    }
    private void ShowPairing()
    {
        downloads.Visibility = Visibility.Collapsed;
        SetQr(relay.CreatePairingUri()); expires = DateTime.UtcNow.AddMinutes(2);
        pairingHint.Text = "Open Nightcall on your phone and tap Scan pairing QR. This code expires in two minutes.";
    }
    private static BitmapImage QrBitmap(string text)
    {
        using var stream = new MemoryStream(InstallServer.Qr(text));
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    private void SetQr(string text)
    {
        qr.Source = QrBitmap(text); qr.Visibility = Visibility.Visible; qrFrame.Visibility = Visibility.Visible;
    }
    public void SavePreview(string path, bool install = false)
    {
        using var installer = install ? new InstallServer(Path.Combine(AppContext.BaseDirectory, "Nightcall.apk"), 0) : null;
        if (installer != null) ShowDownloads(installer.Addresses); else ShowPairing();
        var content = (FrameworkElement)Content;
        content.Measure(new Size(480, 1200)); content.Arrange(new Rect(0, 0, 480, content.DesiredSize.Height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(480, (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private void Refresh()
    {
        status.Text = relay.Status + "\n" + microphone.Status; mute.IsChecked = relay.Muted;
        if (qr.Visibility == Visibility.Visible && DateTime.UtcNow >= expires)
        { qr.Source = null; qr.Visibility = Visibility.Collapsed; qrFrame.Visibility = Visibility.Collapsed; pairingHint.Text = "Pairing code expired. Tap Pair a phone to make another."; relay.EndPairing(); }
        else if (qr.Visibility == Visibility.Visible && expires != DateTime.MaxValue && !relay.PairingActive)
        { qr.Source = null; qr.Visibility = Visibility.Collapsed; qrFrame.Visibility = Visibility.Collapsed; pairingHint.Text = "Phone paired. It will reconnect automatically."; }
        var phones = relay.PairedPhones;
        string signature = string.Join("|", Array.ConvertAll(phones, p => p.Id));
        if (signature == phoneList) return;
        phoneList = signature; paired.Children.Clear();
        foreach (var phone in phones)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var forget = new Button { Content = "Forget", Padding = new Thickness(6, 3, 6, 3) };
            forget.Click += (_, _) => { try { relay.Forget(phone.Id); Refresh(); } catch (Exception e) { MessageBox.Show(this, e.Message); } };
            DockPanel.SetDock(forget, Dock.Right); row.Children.Add(forget); row.Children.Add(new TextBlock { Text = phone.Name, VerticalAlignment = VerticalAlignment.Center }); paired.Children.Add(row);
        }
        if (qr.Visibility == Visibility.Visible) { qr.Source = null; qr.Visibility = Visibility.Collapsed; qrFrame.Visibility = Visibility.Collapsed; pairingHint.Text = "Phone paired. It will reconnect automatically."; }
    }
}

public static class Startup
{
    private const string Key = "Software\\Microsoft\\Windows\\CurrentVersion\\Run", Name = "Nightcall", LegacyName = "DictationOverlay";
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return (key?.GetValue(Name) ?? key?.GetValue(LegacyName)) is string; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value) key.SetValue(Name, "\"" + Environment.ProcessPath + "\""); else key.DeleteValue(Name, false);
            key.DeleteValue(LegacyName, false);
        }
    }
}
