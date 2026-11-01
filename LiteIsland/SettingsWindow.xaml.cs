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

        BuildSwatches(Swatches, Presets, HexBox);
        BuildSwatches(TextSwatches, TextPresets, TextHexBox);
        BuildSwatches(EqSwatches, EqPresets, EqHexBox);
        BuildSwatches(AccSwatches, AccentPresets, AccHexBox);
        BuildAnimStyles();
        BuildThemePills();
        BuildShapePills();
        LoadSliders(_s);
        GlassCheck.IsChecked = _s.Glass;
        HexBox.Text = _s.IslandColor;
        TextHexBox.Text = _s.TextColor;
        EqHexBox.Text = _s.EqColor;
        AccHexBox.Text = _s.AccentColor;
        FahrCheck.IsChecked = _s.UseFahrenheit;
        Hour24Check.IsChecked = _s.Use24Hour;
        StartupCheck.IsChecked = StartupManager.IsEnabled;
        SnapCheck.IsChecked = _s.WindowSnap;
        ShowKeyNames();
        VersionText.Text = "LiteIsland v" + Updater.Current;
        AutoUpdateCheck.IsChecked = _s.AutoUpdate;
        RefreshUpdateUi();
        _main.UpdateStatusChanged += OnUpdateStatus;
        Closed += (_, _) => _main.UpdateStatusChanged -= OnUpdateStatus;
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

    private static readonly string[] Presets = Join(new[]
    {
        "#000000", "#1C1C1E", "#0A84FF", "#30D158", "#FF453A", "#BF5AF2", "#FF9F0A", "#FFFFFF"
    });

    private static readonly string[] TextPresets = Join(new[]
    {
        "#FFFFFF", "#E5E5EA", "#30D158", "#0A84FF", "#FF9F0A", "#FF453A", "#BF5AF2", "#000000"
    });

    private static readonly string[] EqPresets = Join(new[]
    {
        "#30D158", "#0A84FF", "#FF453A", "#BF5AF2", "#FF9F0A", "#FF375F", "#64D2FF", "#FFFFFF"
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
        HexBox.Text = _s.IslandColor;
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

    private void Snap_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.WindowSnap = SnapCheck.IsChecked == true;
        _s.Save();
        _main.ApplySettings(false);
    }

    private void Mirror_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => _main.OpenMirror();

    private void Glass_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.Glass = GlassCheck.IsChecked == true;
        _main.ApplySettings(false);
        _main.PreviewAnimation(); // plays open + the glass shine
    }

    private void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        string t = HexBox.Text.Trim();
        if (!Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$")) return; // wait until a full color is typed
        _s.IslandColor = t.ToUpperInvariant();
        _main.ApplySettings(false);
    }

    private void TextHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        string t = TextHexBox.Text.Trim();
        if (!Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$")) return;
        _s.TextColor = t.ToUpperInvariant();
        _main.ApplySettings(false);
    }

    private void EqHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        string t = EqHexBox.Text.Trim();
        if (!Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$")) return;
        _s.EqColor = t.ToUpperInvariant();
        _main.ApplyExtraSettings();
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
        HexBox.Text = d.IslandColor;
        TextHexBox.Text = d.TextColor;
        GlassCheck.IsChecked = d.Glass;
        EqHexBox.Text = d.EqColor;
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
