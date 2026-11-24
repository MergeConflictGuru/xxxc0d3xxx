using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Nightcall;

public static class NightcallTheme
{
    private static readonly BitmapImage[] artwork = new BitmapImage[2];
    private static readonly System.Drawing.Icon[] icons = new System.Drawing.Icon[2];
    private static DispatcherTimer? timer;
    private static bool? forcedNight;
    public static bool IsNight { get; private set; }
    public static event Action? Changed;
    public static void Initialize(bool? previewNight = null)
    {
        forcedNight = previewNight;
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Nightcall;component/Themes/Nightcall.xaml", UriKind.Relative) });
        Update(true);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) => Update(); timer.Start();
        Application.Current.Exit += (_, _) => { timer.Stop(); foreach (var icon in icons) icon?.Dispose(); };
    }
    private static void Update(bool force = false)
    {
        bool night = forcedNight ?? NightcallClock.IsNight(DateTime.Now);
        if (!force && night == IsNight) return;
        IsNight = night; var resources = Application.Current.Resources;
        string[] names = { "Surface", "Panel", "Text", "Muted", "Accent", "Secondary", "Line", "ActionText" };
        string[] values = night
            ? new[] { "#100F24", "#211B36", "#FFF3E8", "#BDAFC7", "#FF53B4", "#49DFDF", "#593854", "#180C24" }
            : new[] { "#FFF2EB", "#FFFBF7", "#173E48", "#526772", "#BD2467", "#087F85", "#DFC6D0", "#FFFFFF" };
        for (int i = 0; i < names.Length; i++) resources["Nightcall" + names[i]] = Brush(values[i]);
        resources["NightcallBackdrop"] = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(night ? "#102D38" : "#DBF5EE"),
            (Color)ColorConverter.ConvertFromString(night ? "#30132D" : "#FFDAE8"), 110);
        resources["NightcallArtwork"] = Artwork;
        Changed?.Invoke();
    }
    public static SolidColorBrush Brush(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
    public static ImageSource Artwork
    {
        get
        {
            int index = IsNight ? 1 : 0;
            if (artwork[index] == null)
            {
                var image = new BitmapImage(); image.BeginInit(); image.UriSource = AssetUri(IsNight ? "night.png" : "day.png"); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); artwork[index] = image;
            }
            return artwork[index];
        }
    }
    public static System.Drawing.Icon TrayIcon
    {
        get
        {
            int index = IsNight ? 1 : 0;
            if (icons[index] == null)
            {
                using var resource = Application.GetResourceStream(AssetUri(IsNight ? "tray-night.ico" : "tray-day.ico")).Stream;
                using var loaded = new System.Drawing.Icon(resource, System.Windows.Forms.SystemInformation.SmallIconSize); icons[index] = (System.Drawing.Icon)loaded.Clone();
            }
            return icons[index];
        }
    }
    private static Uri AssetUri(string name) => new("pack://application:,,,/Nightcall;component/Branding/" + name);
    public static void Bind(FrameworkElement element, DependencyProperty property, string key) => element.SetResourceReference(property, "Nightcall" + key);
    public static void Apply(Window window) => window.Style = (Style)Application.Current.FindResource(typeof(Window));
    public static Border Card(UIElement content, Thickness? padding = null)
    {
        var card = new Border { Child = content, CornerRadius = new CornerRadius(20), Padding = padding ?? new Thickness(20), BorderThickness = new Thickness(1) };
        Bind(card, Border.BackgroundProperty, "Panel"); Bind(card, Border.BorderBrushProperty, "Line"); return card;
    }
    public static FrameworkElement Header(string subtitle, double size = 84)
    {
        var image = ArtworkImage(size);
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(20), ClipToBounds = true, Child = image, Margin = new Thickness(0, 0, 18, 0) };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = "Nightcall", FontSize = 31, FontWeight = FontWeights.Bold });
        var hint = new TextBlock { Text = subtitle, FontSize = 13, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 270 };
        Bind(hint, TextBlock.ForegroundProperty, "Muted"); words.Children.Add(hint);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 24) }; row.Children.Add(tile); row.Children.Add(words); return row;
    }
    public static Image ArtworkImage(double size)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry(new Rect(0, 0, size, size), Math.Min(20, size / 4), Math.Min(20, size / 4)) };
        Bind(image, Image.SourceProperty, "Artwork"); return image;
    }
}
