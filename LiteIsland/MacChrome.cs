using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LiteIsland;

/// <summary>
/// macOS style window controls: a red close dot and a yellow minimize dot on the left, nothing else.
/// Used by the settings window, the wallpapers window and every tool window.
/// </summary>
internal static class MacChrome
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static readonly Color Red = Color.FromRgb(0xFF, 0x5F, 0x57);
    private static readonly Color Yellow = Color.FromRgb(0xFE, 0xBC, 0x2E);

    /// <summary>The two dots. The glyphs (x and minus) fade in while the mouse is over the pair, like on a Mac.</summary>
    public static StackPanel Lights(Action close, Action minimize)
    {
        var glyphs = new List<UIElement>();
        var ink = new SolidColorBrush(Color.FromArgb(0xC0, 0x45, 0x10, 0x00));

        Grid Dot(Color c, UIElement glyph, Action act)
        {
            var g = new Grid { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            g.Children.Add(new Ellipse { Width = 12.5, Height = 12.5, Fill = new SolidColorBrush(c), Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), StrokeThickness = 0.6 });
            if (glyph is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
            glyph.Opacity = 0;
            glyph.IsHitTestVisible = false;
            g.Children.Add(glyph);
            glyphs.Add(glyph);
            g.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
            return g;
        }

        var x = Icons.Make("close", 7.5, ink, 2.6);
        var minus = new Rectangle { Width = 7, Height = 1.8, RadiusX = 0.9, RadiusY = 0.9, Fill = ink };

        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        sp.Children.Add(Dot(Red, x, close));
        sp.Children.Add(Dot(Yellow, minus, minimize));
        sp.MouseEnter += (_, _) => { foreach (UIElement g in glyphs) g.Opacity = 1; };
        sp.MouseLeave += (_, _) => { foreach (UIElement g in glyphs) g.Opacity = 0; };
        return sp;
    }

    /// <summary>Replaces the system title bar of a XAML window with the Mac style one. Call right after InitializeComponent.</summary>
    public static void Apply(Window w, string title)
    {
        UIElement? old = w.Content as UIElement;
        if (old == null) return;
        w.Content = null;
        w.WindowStyle = WindowStyle.None;
        if (w.ResizeMode == ResizeMode.CanResizeWithGrip) w.ResizeMode = ResizeMode.CanResize;

        var root = new Grid { Background = Brushes.Black };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new Grid { Background = Brushes.Black };
        var lights = Lights(() => w.Close(), () => w.WindowState = WindowState.Minimized);
        lights.Margin = new Thickness(16, 0, 0, 0);
        lights.HorizontalAlignment = HorizontalAlignment.Left;
        bar.Children.Add(lights);
        bar.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xEB, 0xEB, 0xF5)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        });
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || lights.IsMouseOver) return;
            try { w.DragMove(); } catch { }
        };
        root.Children.Add(bar);

        Grid.SetRow(old, 1);
        root.Children.Add(old);
        w.Content = root;

        // the native title bar (about 32 px) is gone, the new one is 40 px
        if (!double.IsNaN(w.Height)) w.Height += 8;

        w.SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(h, 33, ref round, sizeof(int));
            }
            catch { }
        };
    }
}
