using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LiteIsland;

// Dark / Light theme and the Liquid Glass look.
// Glass = see-through gradient fill + soft top light + thin bright rim + a shine that sweeps
// across when the island opens. (It does not blur the desktop behind it: plain WPF cannot do
// that without a lot of CPU / RAM, so this is the light-weight version.)
public partial class MainWindow
{
    private string _themeApplied = "";
    private bool _glassShown;
    private int _glassToken, _sheenToken;

    private static readonly Brush DarkRim = MakeFrozen(MakeRim(0xD8, 0xFF, 0x3C, 0xFF));
    private static readonly Brush LightRim = MakeFrozen(MakeRim(0xF0, 0xFF, 0x30, 0x00));

    private static LinearGradientBrush Vert(Color top, Color bottom)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(top, 0));
        b.GradientStops.Add(new GradientStop(bottom, 1));
        return b;
    }

    private static readonly Brush GlassTile = MakeFrozen(Vert(Color.FromArgb(0x2E, 255, 255, 255), Color.FromArgb(0x14, 255, 255, 255)));
    private static readonly Brush GlassTileHover = MakeFrozen(Vert(Color.FromArgb(0x40, 255, 255, 255), Color.FromArgb(0x1F, 255, 255, 255)));
    private static readonly Brush GlassTileStroke = MakeFrozen(MakeStroke());

    private static LinearGradientBrush MakeStroke()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 255, 255, 255), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, 255, 255, 255), 0.45));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x0D, 255, 255, 255), 0.6));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 255, 255, 255), 1));
        return b;
    }

    private static LinearGradientBrush MakeRim(byte edgeA, byte edgeC, byte midA, byte midC)
    {
        Color edge = Color.FromArgb(edgeA, edgeC, edgeC, edgeC);
        Color mid = Color.FromArgb(midA, midC, midC, midC);
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        b.GradientStops.Add(new GradientStop(edge, 0));
        b.GradientStops.Add(new GradientStop(mid, 0.38));
        b.GradientStops.Add(new GradientStop(mid, 0.62));
        b.GradientStops.Add(new GradientStop(edge, 1));
        return b;
    }

    /// <summary>Applies the Dark / Light theme to the colors that are not user-chosen.</summary>
    private void ApplyTheme()
    {
        if (_themeApplied == _s.Theme) return;
        _themeApplied = _s.Theme;
        bool light = _s.Theme == "Light";

        Resources["TrackBrush"] = MakeFrozen(new SolidColorBrush(
            light ? Color.FromRgb(0xD1, 0xD1, 0xD6) : Color.FromRgb(0x2E, 0x2E, 0x30)));
        Resources["FillBrush"] = MakeFrozen(new SolidColorBrush(
            light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xE6, 0xE6, 0xE6)));
        Resources["RimBrush"] = light ? LightRim : DarkRim;
        Resources["BtnHoverBrush"] = MakeFrozen(new SolidColorBrush(light ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));

        // Tiles, hover and badges in the pages (new ones use these; the open list is rebuilt below)
        // Dark theme: tiles are iOS 26 style crystal glass (soft light-to-dark fill + bright rim). Light theme keeps solid tiles.
        TileBrush = light ? MakeFrozen(new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA))) : GlassTile;
        Resources["TileBg"] = TileBrush;
        Resources["TileStroke"] = light ? MakeFrozen(new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0))) : GlassTileStroke;
        HoverBrush = light ? MakeFrozen(new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00))) : GlassTileHover;

        foreach (UIElement dot in Dots.Children)
            if (dot is System.Windows.Shapes.Shape sh) sh.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");

    }

    /// <summary>Shows or hides the glass layers (fades them in / out).</summary>
    private void ApplyGlass(bool animate)
    {
        bool on = _s.Glass;
        if (on == _glassShown) return;
        _glassShown = on;
        int t = ++_glassToken;

        foreach (FrameworkElement el in new FrameworkElement[] { GlassGloss, GlassRim })
        {
            FrameworkElement e = el;
            if (on)
            {
                e.Visibility = Visibility.Visible;
                if (animate) e.BeginAnimation(OpacityProperty, Da(1, 420, CubicOut()));
                else { e.BeginAnimation(OpacityProperty, null); e.Opacity = 1; }
            }
            else if (animate && e.Visibility == Visibility.Visible)
            {
                var f = Da(0, 260, CubicOut());
                f.Completed += (_, _) =>
                {
                    if (t == _glassToken && !_s.Glass) e.Visibility = Visibility.Collapsed;
                };
                e.BeginAnimation(OpacityProperty, f);
            }
            else
            {
                e.BeginAnimation(OpacityProperty, null);
                e.Opacity = 0;
                e.Visibility = Visibility.Collapsed;
            }
        }

        if (!on)
        {
            _sheenToken++;
            GlassSheen.BeginAnimation(OpacityProperty, null);
            GlassSheen.Opacity = 0;
            GlassSheen.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The glass "wakes up" when the island opens: a shine sweeps across and the rim flashes.</summary>
    private void PlayGlass()
    {
        if (!_s.Glass) return;
        int t = ++_sheenToken;

        GlassSheen.Visibility = Visibility.Visible;
        GlassSheen.BeginAnimation(OpacityProperty, null);
        GlassSheen.Opacity = 1;

        TimeSpan d = Dur(860);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        SheenA.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(-0.45, 1.0, d) { EasingFunction = ease });
        SheenB.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(-0.2, 1.25, d) { EasingFunction = ease });
        var last = new DoubleAnimation(0.05, 1.5, d) { EasingFunction = ease };
        last.Completed += (_, _) =>
        {
            if (t == _sheenToken)
            {
                GlassSheen.Visibility = Visibility.Collapsed;
                GlassSheen.Opacity = 0;
            }
        };
        SheenC.BeginAnimation(GradientStop.OffsetProperty, last);

        // Rim: dims for a moment, then glows back
        var rim = new DoubleAnimationUsingKeyFrames();
        rim.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(Dur(140)), CubicOut()));
        rim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(Dur(760)), CubicOut()));
        GlassRim.BeginAnimation(OpacityProperty, rim);
    }
}
