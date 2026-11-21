using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>
/// A pill that hangs under the island when Claude Code asks for permission (Allow / Deny) or finishes a task.
/// It never takes focus, so typing in the terminal is not interrupted.
/// </summary>
internal sealed class ClaudeAskWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    private readonly Window _island;
    private readonly TaskCompletionSource<int> _tcs = new();
    private readonly DispatcherTimer _life = new();
    private bool _closing;
    public Task<int> Result => _tcs.Task;

    private ClaudeAskWindow(Window island)
    {
        _island = island;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SourceInitialized += (_, _) =>
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 | 0x00000080); // no-activate + tool window
            }
            catch { }
        };
        SizeChanged += (_, _) => Place();
        _life.Tick += (_, _) => Finish(0);
    }

    private void Place()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            double left = _island.Left + _island.ActualWidth / 2 - ActualWidth / 2;
            Left = Math.Max(wa.Left + 8, Math.Min(left, wa.Right - ActualWidth - 8));
            Top = _island.Top + 58;
        }
        catch { }
    }

    private static (Color bg, Color text, Color accent, Color onAccent) Colors()
    {
        var s = AppSettings.Load();
        Color bg = (Color)ColorConverter.ConvertFromString(s.IslandColor);
        Color tx = (Color)ColorConverter.ConvertFromString(s.TextColor);
        Color ac = (Color)ColorConverter.ConvertFromString(s.AccentColor);
        double lum = (0.299 * ac.R + 0.587 * ac.G + 0.114 * ac.B) / 255.0;
        return (bg, tx, ac, lum > 0.62 ? Color.FromRgb(0x10, 0x10, 0x12) : System.Windows.Media.Colors.White);
    }

    private void Finish(int result)
    {
        if (_closing) return;
        _closing = true;
        _life.Stop();
        _tcs.TrySetResult(result);
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        a.Completed += (_, _) => { try { Close(); } catch { } };
        BeginAnimation(OpacityProperty, a);
    }

    private void Reveal()
    {
        Opacity = 0;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static TextBlock Label(string text, double size, Color c, FontWeight w) => new()
    {
        Text = text, FontSize = size, Foreground = new SolidColorBrush(c), FontWeight = w, TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static Border Btn(string text, Color bg, Color fg, Action click)
    {
        var t = Label(text, 14, fg, FontWeights.SemiBold);
        t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
        var brush = new SolidColorBrush(bg);
        var b = new Border { Height = 36, MinWidth = 96, CornerRadius = new CornerRadius(18), Background = brush, Cursor = Cursors.Hand, Child = t, Padding = new Thickness(16, 0, 16, 0) };
        b.MouseEnter += (_, _) => brush.Opacity = 0.85;
        b.MouseLeave += (_, _) => brush.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    private Border Shell(UIElement content, Color bg)
    {
        return new Border
        {
            Background = new SolidColorBrush(bg), CornerRadius = new CornerRadius(26), Padding = new Thickness(18, 14, 18, 14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), BorderThickness = new Thickness(1),
            Margin = new Thickness(14), Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = System.Windows.Media.Colors.Black }
        };
    }

    /// <summary>Allow / Deny question. Returns 1 allow, 2 deny, 0 = decide in Claude (button or timeout).</summary>
    public static Task<int> Ask(Window island, string project, string tool, string summary, TimeSpan timeout)
    {
        var w = new ClaudeAskWindow(island);
        var (bg, tx, ac, onAc) = Colors();
        Color dim = Color.FromArgb(0xA8, tx.R, tx.G, tx.B);

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var chip = Label(tool, 11.5, dim, FontWeights.SemiBold);
        DockPanel.SetDock(chip, Dock.Right);
        head.Children.Add(chip);
        head.Children.Add(Label("Claude wants to run · " + project, 12.5, dim, FontWeights.SemiBold));

        var body = Label(summary.Length > 0 ? summary : tool, 13, tx, FontWeights.Normal);
        body.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        body.TextWrapping = TextWrapping.Wrap;
        body.TextTrimming = TextTrimming.None;
        body.MaxHeight = 60;
        body.Margin = new Thickness(0, 0, 0, 12);

        var fill = Color.FromArgb(0x30, 255, 255, 255);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var later = Label("Decide in Claude", 12.5, dim, FontWeights.Normal);
        later.VerticalAlignment = VerticalAlignment.Center; later.Cursor = Cursors.Hand;
        later.MouseLeftButtonUp += (_, e) => { e.Handled = true; w.Finish(0); };
        var deny = Btn("Deny", fill, tx, () => w.Finish(2)); deny.Margin = new Thickness(0, 0, 8, 0);
        var allow = Btn("Allow", ac, onAc, () => w.Finish(1));
        Grid.SetColumn(deny, 1); Grid.SetColumn(allow, 2);
        row.Children.Add(later); row.Children.Add(deny); row.Children.Add(allow);

        var stack = new StackPanel { Width = 400 };
        stack.Children.Add(head); stack.Children.Add(body); stack.Children.Add(row);
        w.Content = w.Shell(stack, bg);
        w._life.Interval = timeout;
        w._life.Start();
        w.Reveal();
        return w.Result;
    }

    /// <summary>Short "finished" notice that closes by itself.</summary>
    public static void Done(Window island, string project, string text)
    {
        var w = new ClaudeAskWindow(island);
        var (bg, tx, ac, _) = Colors();
        Color dim = Color.FromArgb(0xA8, tx.R, tx.G, tx.B);
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(ac), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var col = new StackPanel { Width = 330 };
        col.Children.Add(Label("Claude finished · " + project, 13.5, tx, FontWeights.SemiBold));
        if (text.Length > 0)
        {
            string one = text.Replace("\r", " ").Replace("\n", " ");
            var sub = Label(one.Length > 120 ? one.Substring(0, 120) + "…" : one, 12, dim, FontWeights.Normal);
            sub.TextWrapping = TextWrapping.Wrap; sub.MaxHeight = 34; sub.Margin = new Thickness(0, 2, 0, 0);
            col.Children.Add(sub);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot); row.Children.Add(col);
        var shell = w.Shell(row, bg);
        shell.Cursor = Cursors.Hand;
        shell.MouseLeftButtonUp += (_, _) => w.Finish(0);
        w.Content = shell;
        w._life.Interval = TimeSpan.FromSeconds(5);
        w._life.Start();
        w.Reveal();
    }
}
