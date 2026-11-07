using System.Linq;
using System.Collections.Generic;
using System;
using System.Windows;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace LiteIsland;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly AppSettings _s;
    private bool _ready;

    public SettingsWindow(MainWindow main, AppSettings settings)
    {
        InitializeComponent();
        Loaded += (_, _) => GlassWindow.ForceForeground(this); // typing in the hex boxes needs real focus
        _main = main;
        _s = settings;

        BuildSwatches(TextSwatches, TextPresets, TextHexBox);
        BuildSwatches(AccSwatches, AccentPresets, AccHexBox);
        BuildPicker(TextSwatches, TextHexBox);
        BuildPicker(AccSwatches, AccHexBox);
        BuildAnimStyles();
        BuildThemePills();
        BuildWeatherPreview();
        BuildShapePills();
        LoadSliders(_s);
        GlassCheck.IsChecked = _s.Glass;
        TextHexBox.Text = _s.TextColor;
        AccHexBox.Text = _s.AccentColor;
        FahrCheck.IsChecked = _s.UseFahrenheit;
        Hour24Check.IsChecked = _s.Use24Hour;
        StartupCheck.IsChecked = StartupManager.IsEnabled;
        ReserveCheck.IsChecked = _s.ReserveTop;
        SilenceBox.Text = _s.SilenceKeep.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        WallCheck.IsChecked = _s.WallpaperOn;
        WallPauseCheck.IsChecked = _s.WallpaperPause;
        ShowWallFile();
        MiloCheck.IsChecked = _s.MiloEnabled;
        MiloNameBox.Text = _s.MiloName;
        MiloVoiceText.Text = VoiceLabel(_s.MiloVoice);
        MiloHotText.Text = _s.MiloHotkey;
        MiloModelBox.Text = _s.MiloModel;
        MiloWakeCheck.IsChecked = _s.MiloWake;
        MiloPhonesCheck.IsChecked = _s.MiloHeadphones;
        MiloKeyBox.Text = MiloKey.Has ? "••••••••••••" : "";
        ShowKeyNames();
        VersionText.Text = "LiteIsland v" + Updater.Current;
        AutoUpdateCheck.IsChecked = _s.AutoUpdate;
        RefreshUpdateUi();
        _main.UpdateStatusChanged += OnUpdateStatus;
        _main.WallpaperStatusChanged += OnWallStatus;
        OnWallStatus();
        Closed += (_, _) => { _main.UpdateStatusChanged -= OnUpdateStatus; _main.WallpaperStatusChanged -= OnWallStatus; try { if (_sample is { HasExited: false }) _sample.Kill(); } catch { } };
        _ready = true;
    }

    // Glass backdrop on Windows 11, dark title bar everywhere
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        GlassWindow.Apply(this);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void LoadSliders(AppSettings s)
    {
        CW.Value = s.CollapsedWidth;
        CH.Value = s.CollapsedHeight;
        CR.Value = s.CollapsedRadius;
        EW.Value = s.ExpandedWidth;
        EH.Value = s.ExpandedHeight;
        ER.Value = s.ExpandedRadius;
        TR.Value = s.TopRadius;
        TG.Value = s.TopGap;
        TS.Value = s.TextScale;
        BO.Value = s.BgOpacity;
        GO.Value = s.GlassOpacity;
        AS.Value = s.AnimSpeed;
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        CWv.Text = $"{(int)CW.Value} px";
        CHv.Text = $"{(int)CH.Value} px";
        CRv.Text = $"{(int)CR.Value} px";
        EWv.Text = $"{(int)EW.Value} px";
        EHv.Text = $"{(int)EH.Value} px";
        ERv.Text = $"{(int)ER.Value} px";
        TRv.Text = $"{(int)TR.Value} px";
        TGv.Text = $"{(int)TG.Value} px";
        TSv.Text = $"{(int)TS.Value}%";
        BOv.Text = $"{(int)BO.Value}%";
        GOv.Text = $"{(int)GO.Value}%";
        ASv.Text = $"{AS.Value:0.0}x";
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;

        _s.CollapsedWidth = Math.Round(CW.Value);
        _s.CollapsedHeight = Math.Round(CH.Value);
        _s.CollapsedRadius = Math.Round(CR.Value);
        _s.ExpandedWidth = Math.Round(EW.Value);
        _s.ExpandedHeight = Math.Round(EH.Value);
        _s.ExpandedRadius = Math.Round(ER.Value);
        _s.TopRadius = Math.Round(TR.Value);
        _s.TopGap = Math.Round(TG.Value);
        _s.TextScale = Math.Round(TS.Value);
        _s.BgOpacity = Math.Round(BO.Value);
        _s.GlassOpacity = Math.Round(GO.Value);
        _s.AnimSpeed = Math.Round(AS.Value, 1);
        UpdateLabels();

        // Speed slider: play the animation so the new speed can be seen
        if (ReferenceEquals(sender, AS))
        {
            _main.PreviewAnimation();
            return;
        }

        // Moving an "expanded" slider shows the big island, any other slider shows the small one
        bool expandedSlider = ReferenceEquals(sender, EW) || ReferenceEquals(sender, EH) || ReferenceEquals(sender, ER)
                              || ReferenceEquals(sender, GO);
        _main.ApplySettings(expandedSlider);
    }

    // Neon "highlighter marker" colors, added to every color list
    private static readonly string[] Highlighters =
    {
        "#FFFF00", "#39FF14", "#FF10F0", "#00F0FF", "#FF5F1F", "#CCFF00", "#B026FF", "#FF3131"
    };

    private static readonly string[] TextPresets = Join(new[]
    {
        "#FFFFFF", "#E5E5EA", "#30D158", "#0A84FF", "#FF9F0A", "#FF453A", "#BF5AF2", "#000000"
    });

    private static readonly string[] AccentPresets = Join(new[]
    {
        "#38BDF8", "#0A84FF", "#30D158", "#FF453A", "#BF5AF2", "#FF9F0A", "#FF375F", "#FFFFFF"
    });

    private static string[] Join(string[] basic)
    {
        return basic;
    }

    private static void BuildSwatches(WrapPanel panel, string[] colors, TextBox target)
    {
        foreach (string hex in colors)
        {
            string h = hex;
            var dot = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(0, 0, 9, 6),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = h
            };
            dot.MouseLeftButtonUp += (_, _) => target.Text = h; // TextChanged applies it
            panel.Children.Add(dot);
        }
    }

    // ---------- Color picker: a palette (saturation / brightness square + hue bar) under the swatches ----------

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static void BuildPicker(WrapPanel swatches, TextBox target)
    {
        if (swatches.Parent is not StackPanel host) return;
        const double W = 232, H = 118, HB = 16;

        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 8), Visibility = Visibility.Collapsed };
        var toggle = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 5, 12, 5), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)),
            Child = new TextBlock { Text = "Pick any color...", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
        };
        toggle.MouseLeftButtonUp += (_, _) => panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        host.Children.Add(toggle);

        Color start = Colors.DodgerBlue;
        try { start = (Color)ColorConverter.ConvertFromString(target.Text.Length == 7 ? target.Text : "#0A84FF"); } catch { }
        var (h0, s0, v0) = ToHsv(start);
        double hue = h0, sat = s0, val = v0;
        bool setting = false;

        // saturation (left to right) and brightness (top to bottom) of the chosen hue
        var sv = new Grid { Width = W, Height = H, Cursor = Cursors.Cross, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        var hueLayer = new Border { CornerRadius = new CornerRadius(10) };
        var white = new Border { CornerRadius = new CornerRadius(10), Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
        var black = new Border { CornerRadius = new CornerRadius(10), Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
        sv.Children.Add(hueLayer); sv.Children.Add(white); sv.Children.Add(black);
        var thumb = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderBrush = Brushes.White, BorderThickness = new Thickness(2.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 } };
        sv.Children.Add(thumb);

        var hb = new Grid { Width = W, Height = HB, Margin = new Thickness(0, 8, 0, 0), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
        var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) grad.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));
        hb.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = grad });
        var hthumb = new Border { Width = 8, Height = HB + 4, CornerRadius = new CornerRadius(4), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 } };
        hb.Children.Add(hthumb);

        void Paint()
        {
            hueLayer.Background = new SolidColorBrush(FromHsv(hue, 1, 1));
            thumb.Margin = new Thickness(sat * W - 8, (1 - val) * H - 8, 0, 0);
            hthumb.Margin = new Thickness(hue / 360.0 * (W - 8), 0, 0, 0);
            thumb.Background = new SolidColorBrush(FromHsv(hue, sat, val));
        }
        void Push()
        {
            Paint();
            Color c = FromHsv(hue, sat, val);
            setting = true;
            target.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; // TextChanged applies it
            setting = false;
        }
        void PickSv(MouseEventArgs e)
        {
            Point p = e.GetPosition(sv);
            sat = Math.Clamp(p.X / W, 0, 1);
            val = 1 - Math.Clamp(p.Y / H, 0, 1);
            Push();
        }
        void PickHue(MouseEventArgs e)
        {
            hue = Math.Clamp(e.GetPosition(hb).X / W, 0, 1) * 359.9;
            Push();
        }
        bool dragSv = false, dragHue = false;
        sv.MouseLeftButtonDown += (_, e) => { dragSv = true; sv.CaptureMouse(); PickSv(e); };
        sv.MouseMove += (_, e) => { if (dragSv) PickSv(e); };
        sv.MouseLeftButtonUp += (_, _) => { dragSv = false; sv.ReleaseMouseCapture(); };
        hb.MouseLeftButtonDown += (_, e) => { dragHue = true; hb.CaptureMouse(); PickHue(e); };
        hb.MouseMove += (_, e) => { if (dragHue) PickHue(e); };
        hb.MouseLeftButtonUp += (_, _) => { dragHue = false; hb.ReleaseMouseCapture(); };

        // a swatch or a typed hex moves the picker too
        target.TextChanged += (_, _) =>
        {
            if (setting || !Regex.IsMatch(target.Text.Trim(), "^#[0-9A-Fa-f]{6}$")) return;
            try
            {
                var (h, s, v) = ToHsv((Color)ColorConverter.ConvertFromString(target.Text.Trim()));
                if (s > 0.02 && v > 0.02) hue = h;
                sat = s; val = v;
                Paint();
            }
            catch { }
        };

        panel.Children.Add(sv);
        panel.Children.Add(hb);
        host.Children.Add(panel);
        Paint();
    }

    // ---------- Animation style buttons ----------

    private static readonly string[] AnimDescriptions =
    {
        "Calm and soft, no bounce. Looks like Apple's own motion.",
        "Lively spring with a small bounce, like the iPhone Dynamic Island.",
        "Stretches down first, then widens. Closing pulls the island back up.",
        "Grows evenly while the content zooms in.",
        "Very fast and crisp. The lightest option for old PCs.",
        "Soft spring that settles gently, like a MacBook notch app. Clean and premium.",
        "Plain and quiet: a calm fade and glide, no bounce, no zoom.",
        "Height leads, the width follows a moment later. Opening and closing flow like liquid."
    };

    private void BuildAnimStyles()
    {
        for (int i = 0; i < AppSettings.AnimStyles.Length; i++)
        {
            string name = AppSettings.AnimStyles[i];
            var text = new TextBlock { Text = name, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
            var pill = new Border
            {
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(13, 6, 13, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Child = text,
                Tag = name
            };
            pill.MouseLeftButtonUp += (_, _) =>
            {
                _s.AnimStyle = name;
                UpdateAnimStyleUi();
                _main.PreviewAnimation(); // show the new animation right away
            };
            AnimStyles.Children.Add(pill);
        }
        UpdateAnimStyleUi();
    }

    private void UpdateAnimStyleUi()
    {
        for (int i = 0; i < AnimStyles.Children.Count; i++)
        {
            var pill = (Border)AnimStyles.Children[i];
            bool on = (string)pill.Tag == _s.AnimStyle;
            pill.Background = new SolidColorBrush(on ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            ((TextBlock)pill.Child).Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD1, 0xD1, 0xD6));
        }

        int idx = Array.IndexOf(AppSettings.AnimStyles, _s.AnimStyle);
        AnimDesc.Text = idx >= 0 ? AnimDescriptions[idx] : "";
    }

    // ---------- Dark / Light mode ----------

    private static readonly string[] Themes = { "Dark", "Light" };

    private void BuildThemePills()
    {
        foreach (string name in Themes)
        {
            string n = name;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(18, 6, 18, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = n, FontSize = 12.5, FontWeight = FontWeights.SemiBold },
                Tag = n
            };
            pill.MouseLeftButtonUp += (_, _) => SetTheme(n);
            ThemePills.Children.Add(pill);
        }
        UpdateThemeUi();
    }

    private void BuildWeatherPreview()
    {
        (string Name, int Code, bool Day)[] list =
        {
            ("Sunny", 0, true), ("Clear night", 0, false), ("Partly cloudy", 2, true), ("Cloudy", 3, true),
            ("Fog", 45, true), ("Rain", 61, true), ("Snow", 71, true), ("Storm", 95, true), ("Real weather", -1, true)
        };
        foreach (var (name, code, day) in list)
        {
            int c = code; bool d = day;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(13, 5, 13, 5), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold }
            };
            pill.MouseLeftButtonUp += (_, _) => _main.PreviewWeather(c, d);
            WxPreviewPills.Children.Add(pill);
        }
    }

    private void UpdateThemeUi()
    {
        foreach (Border pill in ThemePills.Children)
        {
            bool on = (string)pill.Tag == _s.Theme;
            pill.Background = new SolidColorBrush(on ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            ((TextBlock)pill.Child).Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD1, 0xD1, 0xD6));
        }
    }

    private void SetTheme(string name)
    {
        bool light = name == "Light";
        _s.Theme = name;
        _s.IslandColor = light ? "#F2F2F7" : "#000000";
        _s.TextColor = light ? "#1C1C1E" : "#FFFFFF";

        _ready = false; // fill the hex boxes without applying twice
        TextHexBox.Text = _s.TextColor;
        _ready = true;

        UpdateThemeUi();
        _main.ApplySettings(false);
        _main.PreviewAnimation(); // show the new look on the open island
    }

    // ---------- Shape: MacBook notch / iPhone island ----------

    private static readonly string[] Shapes = { "Notch", "Island" };

    private void BuildShapePills()
    {
        foreach (string name in Shapes)
        {
            string n = name;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = n == "Notch" ? "MacBook notch" : "iPhone island", FontSize = 12.5, FontWeight = FontWeights.SemiBold },
                Tag = n
            };
            pill.MouseLeftButtonUp += (_, _) => SetShape(n);
            ShapePills.Children.Add(pill);
        }
        UpdateShapeUi();
    }

    private void UpdateShapeUi()
    {
        bool island = _s.IslandShape == "Island";
        foreach (Border pill in ShapePills.Children)
        {
            bool on = (string)pill.Tag == _s.IslandShape;
            pill.Background = new SolidColorBrush(on ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            ((TextBlock)pill.Child).Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD1, 0xD1, 0xD6));
        }
        TRPanel.Visibility = TRDiv.Visibility = island ? Visibility.Collapsed : Visibility.Visible;
        TGPanel.Visibility = TGDiv.Visibility = island ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetShape(string name)
    {
        if (_s.IslandShape == name) return;
        _s.IslandShape = name;

        // A matching starting size (every slider can still be changed afterwards)
        if (name == "Island") { _s.CollapsedWidth = 190; _s.CollapsedHeight = 34; _s.TopGap = 10; }
        else { _s.CollapsedWidth = 300; _s.CollapsedHeight = 36; _s.CollapsedRadius = 20; }

        _ready = false;
        LoadSliders(_s);
        _ready = true;
        UpdateShapeUi();
        _main.ApplySettings(false);
        _main.PreviewAnimation();
    }

    // ---------- Milo ----------
    private void MiloApply()
    {
        _s.Save();
        _main.RestartMilo();
    }

    private void Milo_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.MiloEnabled = MiloCheck.IsChecked == true;
        MiloApply();
    }

    private void MiloWake_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.MiloWake = MiloWakeCheck.IsChecked == true;
        MiloApply();
    }

    private void MiloPhones_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.MiloHeadphones = MiloPhonesCheck.IsChecked == true;
        MiloApply();
    }

    // ---------- Live wallpaper ----------
    private void ShowWallFile()
    {
        WallFileText.Text = string.IsNullOrEmpty(_s.WallpaperPath) ? "No video chosen" : System.IO.Path.GetFileName(_s.WallpaperPath);
    }

    private void OnWallStatus()
    {
        Dispatcher.BeginInvoke(new Action(() => WallStatusText.Text = _main.WallpaperStatus));
    }

    private void Wall_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (WallCheck.IsChecked == true && string.IsNullOrEmpty(_s.WallpaperPath))
        {
            WallPick_Click(sender, e);
            if (string.IsNullOrEmpty(_s.WallpaperPath)) { _ready = false; WallCheck.IsChecked = false; _ready = true; return; }
        }
        _s.WallpaperOn = WallCheck.IsChecked == true;
        _s.Save();
        _main.ApplyWallpaper();
    }

    private void WallPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a wallpaper video",
            Filter = "Video|*.mp4;*.mkv;*.webm;*.mov;*.avi;*.m4v|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        _s.WallpaperPath = dlg.FileName;
        ShowWallFile();
        _s.Save();
        if (_s.WallpaperOn) _main.ApplyWallpaper();
        else if (sender == WallPickBtn) { _ready = false; WallCheck.IsChecked = true; _ready = true; _s.WallpaperOn = true; _s.Save(); _main.ApplyWallpaper(); }
    }

    private void WallPause_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.WallpaperPause = WallPauseCheck.IsChecked == true;
        _s.Save();
        _main.ApplyWallpaper();
    }

    private void Silence_LostFocus(object sender, RoutedEventArgs e)
    {
        string t = SilenceBox.Text.Trim().Replace(',', '.');
        double v = double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) ? x : 0.05;
        v = Math.Clamp(v, 0, 5);
        _s.SilenceKeep = v;
        _s.Save();
        SilenceBox.Text = v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void MiloName_LostFocus(object sender, RoutedEventArgs e)
    {
        string n = MiloNameBox.Text.Trim();
        if (n.Length == 0) n = "Milo";
        if (n == _s.MiloName) return;
        _s.MiloName = n;
        MiloApply();
    }

    private static readonly (string, string)[] Voices =
    {
        ("Puck", "Upbeat"), ("Kore", "Firm"), ("Charon", "Informative"), ("Fenrir", "Excitable"), ("Aoede", "Breezy"),
        ("Leda", "Youthful"), ("Orus", "Firm"), ("Zephyr", "Bright"), ("Callirrhoe", "Easy-going"), ("Autonoe", "Bright"),
        ("Enceladus", "Breathy"), ("Iapetus", "Clear"), ("Umbriel", "Easy-going"), ("Algieba", "Smooth"), ("Despina", "Smooth"),
        ("Erinome", "Clear"), ("Algenib", "Gravelly"), ("Rasalgethi", "Informative"), ("Laomedeia", "Upbeat"), ("Achernar", "Soft"),
        ("Alnilam", "Firm"), ("Schedar", "Even"), ("Gacrux", "Mature"), ("Pulcherrima", "Forward"), ("Achird", "Friendly"),
        ("Zubenelgenubi", "Casual"), ("Vindemiatrix", "Gentle"), ("Sadachbia", "Lively"), ("Sadaltager", "Knowledgeable"), ("Sulafat", "Warm")
    };

    private static readonly (string, string)[] Hotkeys =
    {
        ("Ctrl+Alt+Space", ""), ("F2", ""), ("F3", ""), ("F4", ""), ("F8", ""), ("Ctrl+Alt+M", ""), ("Win+Shift+M", "")
    };

    private static string VoiceLabel(string v)
    {
        foreach (var (n, d) in Voices) if (n == v) return n + " · " + d;
        return v;
    }

    /// <summary>A small dark drop-down under a button: pick one entry.</summary>
    private void PickFrom(FrameworkElement anchor, (string Value, string Note)[] items, string current, Action<string> chosen)
    {
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade
        };
        var list = new StackPanel();
        foreach (var (value, note) in items)
        {
            string v = value;
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var g = new Grid();
            g.Children.Add(new TextBlock { Text = note.Length > 0 ? v + " · " + note : v, Foreground = Brushes.White });
            if (v == current)
                g.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            row.Child = g;
            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; popup.IsOpen = false; chosen(v); };
            list.Children.Add(row);
        }
        popup.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)), CornerRadius = new CornerRadius(12), Padding = new Thickness(4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 10), MinWidth = Math.Max(160, anchor.ActualWidth), MaxHeight = 260,
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list }
        };
        popup.IsOpen = true;
    }

    private void MiloVoice_Click(object sender, MouseButtonEventArgs e)
    {
        PickFrom(MiloVoiceBtn, Voices, _s.MiloVoice, v =>
        {
            _s.MiloVoice = v;
            MiloVoiceText.Text = VoiceLabel(v);
            MiloApply();
            PlaySample(v);
        });
    }

    private System.Diagnostics.Process? _sample;

    /// <summary>Says one short line in the chosen voice with the name of the assistant (runs in its own small process).</summary>
    private void PlaySample(string voice)
    {
        try
        {
            if (!MiloKey.Has)
            {
                MiloVoiceText.Text = "Add your API key first";
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
                t.Tick += (_, _) => { t.Stop(); MiloVoiceText.Text = VoiceLabel(_s.MiloVoice); };
                t.Start();
                return;
            }
            try { if (_sample is { HasExited: false }) _sample.Kill(); } catch { }
            var (file, pre) = Proc.SelfCommand();
            var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string p in pre) psi.ArgumentList.Add(p);
            psi.ArgumentList.Add("--milo-sample");
            psi.ArgumentList.Add(voice);
            psi.ArgumentList.Add(string.IsNullOrWhiteSpace(_s.MiloName) ? "Milo" : _s.MiloName.Trim());
            psi.ArgumentList.Add(_s.MiloModel ?? "");
            _sample = System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    private void MiloHot_Click(object sender, MouseButtonEventArgs e)
    {
        PickFrom(MiloHotBtn, Hotkeys, _s.MiloHotkey, v =>
        {
            _s.MiloHotkey = v;
            MiloHotText.Text = v;
            MiloApply();
        });
    }

    private void MiloGetKey_Click(object sender, MouseButtonEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true })?.Dispose(); }
        catch { }
    }

    private void MiloModel_LostFocus(object sender, RoutedEventArgs e)
    {
        string v = MiloModelBox.Text.Trim();
        if (v.Length == 0) v = "gemini-2.5-flash-native-audio-preview-09-2025";
        if (v == _s.MiloModel) return;
        _s.MiloModel = v;
        MiloApply();
    }

    private void MiloKey_LostFocus(object sender, RoutedEventArgs e)
    {
        string k = MiloKeyBox.Text.Trim();
        if (k.Length == 0 || k.StartsWith("•")) return;
        MiloKey.Save(k);
        MiloKeyBox.Text = "••••••••••••";
        MiloApply();
    }

    private void MiloLog_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(MiloPaths.Dir);
            if (!System.IO.File.Exists(MiloPaths.Log)) System.IO.File.WriteAllText(MiloPaths.Log, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MiloPaths.Log) { UseShellExecute = true })?.Dispose();
        }
        catch { }
    }

    private void Reserve_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.ReserveTop = ReserveCheck.IsChecked == true;
        _s.Save();
        _main.ApplySettings(false);
    }


    private void Glass_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.Glass = GlassCheck.IsChecked == true;
        _main.ApplySettings(false);
        _main.PreviewAnimation(); // plays open + the glass shine
    }

    private void TextHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        string t = TextHexBox.Text.Trim();
        if (!Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$")) return;
        _s.TextColor = t.ToUpperInvariant();
        _main.ApplySettings(false);
    }

    private void AccHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        string t = AccHexBox.Text.Trim();
        if (!Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$")) return;
        _s.AccentColor = t.ToUpperInvariant();
        _main.ApplyExtraSettings();
    }

    private void Weather_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.UseFahrenheit = FahrCheck.IsChecked == true;
        _main.ApplyExtraSettings();
    }

    private void Hour24_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.Use24Hour = Hour24Check.IsChecked == true;
        _main.RefreshClock();
    }

    // ---------- File ring key ----------

    private int _captureTarget; // 0 = none, 1 = file ring key, 2 = apps ring key

    private static string KeyName(int vk)
    {
        switch (vk)
        {
            case 0xA0: return "Left Shift";
            case 0xA1: return "Right Shift";
            case 0xA2: return "Left Ctrl";
            case 0xA3: return "Right Ctrl";
            case 0xA4: return "Left Alt";
            case 0xA5: return "Right Alt";
            case 0x5B: return "Left Win";
            case 0x5C: return "Right Win";
            case 0x14: return "Caps Lock";
            case 0x20: return "Space";
            case 0xC0: return "` (backtick)";
        }
        string n = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk).ToString();
        if (n.Length == 2 && n[0] == 'D' && char.IsDigit(n[1])) n = n.Substring(1);
        return n == "None" ? "Key " + vk : n;
    }

    private void RingKey_Click(object sender, MouseButtonEventArgs e)
    {
        _captureTarget = 1;
        RingKeyText.Text = "Press a key...";
        Focus();
    }

    private void AppsKey_Click(object sender, MouseButtonEventArgs e)
    {
        _captureTarget = 2;
        AppsKeyText.Text = "Press a key...";
        Focus();
    }

    private void ShowKeyNames()
    {
        RingKeyText.Text = KeyName(_s.RingKey);
        AppsKeyText.Text = KeyName(_s.AppsKey);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_captureTarget != 0)
        {
            e.Handled = true;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if (k == Key.None) return;
            int target = _captureTarget;
            _captureTarget = 0;
            if (k == Key.Escape) { ShowKeyNames(); return; }

            int vk = KeyInterop.VirtualKeyFromKey(k);
            int other = target == 1 ? _s.AppsKey : _s.RingKey;
            if (vk < 8 || vk > 254 || k == Key.Tab || k == Key.Enter || vk == other)
            {
                ShowKeyNames(); // not allowed, or the same key as the other shortcut
                return;
            }
            if (target == 1) _s.RingKey = vk; else _s.AppsKey = vk;
            _s.Save();
            _main.ApplyTaskSettings();
            ShowKeyNames();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_captureTarget != 0) { _captureTarget = 0; ShowKeyNames(); }
    }

    private void OnUpdateStatus() => Dispatcher.BeginInvoke(new Action(RefreshUpdateUi));

    private void RefreshUpdateUi()
    {
        UpdateText.Text = string.IsNullOrEmpty(_main.UpdateStatus) ? "Version " + Updater.Current : _main.UpdateStatus;
        UpdateBtnText.Text = _main.UpdateReady ? "Restart now" : "Check now";
    }

    private async void Update_Click(object sender, MouseButtonEventArgs e)
    {
        if (_main.UpdateReady) { Updater.Restart(); return; }
        await _main.CheckUpdateAsync(true);
        RefreshUpdateUi();
    }

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        _s.Save();
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        StartupManager.Set(StartupCheck.IsChecked == true);
    }

    private void OpenLink(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser: nothing to do */ }
    }

    private void Discord_Click(object sender, MouseButtonEventArgs e) => OpenLink("https://discord.com/users/1240931630928105562");

    private void Insta_Click(object sender, MouseButtonEventArgs e) => OpenLink("https://instagram.com/hussaintaqvii");

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        _ready = false;
        LoadSliders(d);
        TextHexBox.Text = d.TextColor;
        GlassCheck.IsChecked = d.Glass;
        AccHexBox.Text = d.AccentColor;
        FahrCheck.IsChecked = d.UseFahrenheit;
        _ready = true;

        _s.CopyFrom(d);
        UpdateAnimStyleUi();
        UpdateThemeUi();
        UpdateShapeUi();
        _main.ApplySettings(false);
        _main.ApplyHudSettings();
    }
}
