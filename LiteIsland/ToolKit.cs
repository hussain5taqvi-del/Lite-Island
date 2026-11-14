using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace LiteIsland;

/// <summary>
/// Shared look of the small tool windows (Calculator, QR) and the screen tools.
/// Apple style: dark or light frosted glass, big rounded corners, thin bright rim, soft shadow, springy buttons.
/// Only used inside the short-lived "--tool" helper process, never in the island itself.
/// </summary>
internal static class Kit
{
    public static bool Dark = true;
    public static Color AccentColor = Color.FromRgb(0x0A, 0x84, 0xFF);

    public static Brush Text = null!, Dim = null!, Faint = null!, Fill = null!, FillHover = null!, Accent = null!, OnAccent = null!;
    public static Brush Glass = null!, Rim = null!, Sep = null!;

    public static void Init()
    {
        try
        {
            var s = AppSettings.Load();
            Dark = !string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(s.AccentColor))
                AccentColor = (Color)ColorConverter.ConvertFromString(s.AccentColor);
        }
        catch { }

        Text = Frozen(Dark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F));
        Dim = Frozen(Dark ? Color.FromArgb(0xA0, 0xEB, 0xEB, 0xF5) : Color.FromArgb(0xA0, 0x3C, 0x3C, 0x43));
        Faint = Frozen(Dark ? Color.FromArgb(0x60, 0xEB, 0xEB, 0xF5) : Color.FromArgb(0x60, 0x3C, 0x3C, 0x43));
        Fill = Frozen(Dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        FillHover = Frozen(Dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00));
        Sep = Frozen(Dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        Accent = Frozen(AccentColor);
        // readable text on the accent color
        double lum = (0.299 * AccentColor.R + 0.587 * AccentColor.G + 0.114 * AccentColor.B) / 255.0;
        OnAccent = Frozen(lum > 0.62 ? Color.FromRgb(0x10, 0x10, 0x12) : Colors.White);

        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.35, 1) };
        if (Dark)
        {
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xEE, 0x2C, 0x2C, 0x30), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0x1A, 0x1A, 0x1D), 1));
        }
        else
        {
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xF4, 0xFF, 0xFF, 0xFF), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, 0xEC, 0xEC, 0xF1), 1));
        }
        g.Freeze();
        Glass = g;

        var r = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        r.GradientStops.Add(new GradientStop(Dark ? Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00), 0));
        r.GradientStops.Add(new GradientStop(Dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00), 1));
        r.Freeze();
        Rim = r;
    }

    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static SolidColorBrush Hex(string hex) => Frozen((Color)ColorConverter.ConvertFromString(hex));

    // ---------- text ----------

    public static TextBlock T(string text, double size, bool bold = false, Brush? color = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = color ?? Text,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    // ---------- press feedback ----------

    /// <summary>Shrinks a little while pressed and springs back (like iOS).</summary>
    public static void Press(FrameworkElement b, double to = 0.94)
    {
        var st = new ScaleTransform(1, 1);
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        b.RenderTransform = st;
        b.PreviewMouseLeftButtonDown += (_, _) => Anim(st, to, 80, false);
        b.PreviewMouseLeftButtonUp += (_, _) => Anim(st, 1, 280, true);
        b.MouseLeave += (_, _) => { if (st.ScaleX < 0.999) Anim(st, 1, 200, true); };
    }

    private static void Anim(ScaleTransform st, double to, int ms, bool spring)
    {
        IEasingFunction ease = spring ? new SpringEase { Damping = 0.55 } : new CubicEase { EasingMode = EasingMode.EaseOut };
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Round or pill button with an icon and/or label. fill = null: soft glass fill.</summary>
    public static Border Button(string? icon, string? label, Action click, bool accent = false, double height = 34, string? tip = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Brush fg = accent ? OnAccent : Text;
        if (icon != null)
        {
            var ic = Icons.Make(icon, 16, fg, 1.8);
            ic.VerticalAlignment = VerticalAlignment.Center;
            if (label != null) ic.Margin = new Thickness(0, 0, 7, 0);
            row.Children.Add(ic);
        }
        if (label != null)
        {
            var t = T(label, 13, true, fg);
            t.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(t);
        }
        var b = new Border
        {
            Height = height,
            MinWidth = label == null ? height : 0,
            CornerRadius = new CornerRadius(height / 2),
            Padding = new Thickness(label == null ? 0 : 15, 0, label == null ? 0 : 15, 0),
            Background = accent ? Accent : Fill,
            Cursor = Cursors.Hand,
            Child = row,
            ToolTip = tip
        };
        b.MouseEnter += (_, _) => { if (!accent) b.Background = FillHover; else b.Opacity = 0.88; };
        b.MouseLeave += (_, _) => { if (!accent) b.Background = Fill; else b.Opacity = 1; };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        Press(b);
        return b;
    }

    // ---------- clipboard (survives the helper closing) ----------

    public static bool SetClipText(string text)
    {
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch { Thread.Sleep(40); }
        }
        return false;
    }

    public static bool SetClipImage(System.Windows.Media.Imaging.BitmapSource bmp)
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                var d = new DataObject();
                d.SetImage(bmp);
                Clipboard.SetDataObject(d, true);
                return true;
            }
            catch { Thread.Sleep(40); }
        }
        return false;
    }

    // ---------- glass surfaces ----------

    public static Border GlassCard(double radius, UIElement child, bool shadow = true)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = Glass,
            BorderBrush = Rim,
            BorderThickness = new Thickness(1),
            Child = child,
            SnapsToDevicePixels = true
        };
        if (shadow)
            b.Effect = new DropShadowEffect { BlurRadius = 38, ShadowDepth = 12, Direction = 270, Opacity = Dark ? 0.55 : 0.28, Color = Colors.Black };
        return b;
    }

    /// <summary>Small floating pill used for hints and short results.</summary>
    public static Border Pill(UIElement content, double radius = 22, Thickness? pad = null)
    {
        var b = GlassCard(radius, content);
        b.Padding = pad ?? new Thickness(16, 9, 16, 9);
        return b;
    }

    public static void FadeIn(UIElement e, int ms = 180, double fromScale = 0.96)
    {
        e.Opacity = 0;
        var sc = new ScaleTransform(fromScale, fromScale);
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = sc;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        var sa = new DoubleAnimation(fromScale, 1, TimeSpan.FromMilliseconds(ms + 120)) { EasingFunction = ease };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
    }
}

/// <summary>A frameless, floating glass window with a round close dot, drag by the background, Esc to close.</summary>
internal class ToolShell : Window
{
    protected readonly Border Card;
    protected readonly Grid Body = new();
    private bool _closing;

    protected ToolShell(string title, double width)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = true;
        Title = title;
        Width = width + 56;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // header: red close dot, yellow minimize dot, title
        var head = new Grid { Height = 44, Background = Brushes.Transparent };
        var lights = MacChrome.Lights(CloseSoft, () => WindowState = WindowState.Minimized);
        lights.Margin = new Thickness(18, 0, 0, 0);
        lights.HorizontalAlignment = HorizontalAlignment.Left;
        head.Children.Add(lights);
        var tt = Kit.T(title, 13, true, Kit.Dim);
        tt.HorizontalAlignment = HorizontalAlignment.Center;
        tt.VerticalAlignment = VerticalAlignment.Center;
        tt.IsHitTestVisible = false;
        head.Children.Add(tt);
        // dragging must not start on the dots, or the mouse-up never reaches them and they cannot be clicked
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || lights.IsMouseOver) return;
            try { DragMove(); } catch { }
        };
        inner.Children.Add(head);

        Body.Margin = new Thickness(20, 0, 20, 20);
        Grid.SetRow(Body, 1);
        inner.Children.Add(Body);

        Card = Kit.GlassCard(28, inner);
        Card.Margin = new Thickness(28, 22, 28, 34);
        Content = Card;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseSoft(); } };
        Loaded += (_, _) =>
        {
            PlaceStart();
            Kit.FadeIn(Card, 200, 0.95);
            GlassWindow.ForceForeground(this);
            OnShown();
        };
        Closed += (_, _) => Application.Current?.Shutdown();
    }

    protected virtual void OnShown() { }

    private void PlaceStart()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + Math.Max(40, (wa.Height - ActualHeight) / 2 - 60);
    }

    public void CloseSoft()
    {
        if (_closing) return;
        _closing = true;
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        a.Completed += (_, _) => { try { Close(); } catch { } };
        Card.BeginAnimation(UIElement.OpacityProperty, a);
    }
}

/// <summary>Entry point of the tool helper process: "LiteIsland.exe --tool name".</summary>
internal static class ToolHost
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static int Run(string[] args)
    {
        string tool = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        if (tool.Length == 0) return 2;

        // one window per tool: pressing the shortcut again while it is open does nothing
        using var mutex = new Mutex(true, @"Local\LiteIsland_Tool_" + tool, out bool first);
        if (!first) return 0;

        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per monitor v2: screen pixels are real pixels

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var inter = new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Inter, Segoe UI Variable Text, Segoe UI");
            System.Windows.Documents.TextElement.FontFamilyProperty.OverrideMetadata(typeof(System.Windows.Documents.TextElement), new FrameworkPropertyMetadata(inter));
            TextBlock.FontFamilyProperty.OverrideMetadata(typeof(TextBlock), new FrameworkPropertyMetadata(inter));
            Control.FontFamilyProperty.OverrideMetadata(typeof(Control), new FrameworkPropertyMetadata(inter));
        }
        catch { }

        Kit.Init();
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; try { app.Shutdown(); } catch { } };

        app.Startup += (_, _) =>
        {
            try
            {
                switch (tool)
                {
                    case "ocr": ScreenTools.Start(ScreenTools.Mode.Text); break;
                    case "color": ScreenTools.Start(ScreenTools.Mode.Color); break;
                    case "draw": ScreenTools.Start(ScreenTools.Mode.Draw); break;
                    case "lens": ScreenTools.Start(ScreenTools.Mode.Lens); break;
                    case "font": ScreenTools.Start(ScreenTools.Mode.Font); break;
                    case "calc": new CalcWindow().Show(); break;
                    case "qr": new QrWindow().Show(); break;
                    default: app.Shutdown(); break;
                }
            }
            catch { app.Shutdown(); }
        };
        return app.Run();
    }
}
