using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LiteIsland;

// Island shape (MacBook notch / iPhone island), shadow, font, small-island layout and the play / pause icon.
public partial class MainWindow
{
    private const double HudMinWidth = 250; // the volume / brightness bar needs at least this much room

    private bool _hasWx;
    private bool _hideAmPm; // AM/PM steps aside first when the island is narrow
    private bool _playIconIsPause;

    private bool IsIsland => _s.IslandShape == "Island";
    private double CollapsedR => IsIsland ? Math.Max(8, _s.CollapsedHeight / 2.0) : _s.CollapsedRadius;
    private double TopGapPx => IsIsland ? _s.TopGap : 0;

    // ---------- Shape ----------

    private void ApplyShape()
    {
        // The island shape floats, so its glass rim goes all the way around
        GlassRim.BorderThickness = IsIsland ? new Thickness(1.4) : new Thickness(1.4, 0, 1.4, 1.4);
        ApplyEars();
        ApplyCorners();
    }

    // ---------- Small island ----------
    // The collapsed island can be very small. When there is not enough room, the least important parts
    // (weather, battery %, song name) step aside, so nothing is ever cut off or overlapping.

    private void ApplyCompactLayout()
    {
        if (Compact == null) return;
        double w = _s.CollapsedWidth;

        // Text and cover follow the island HEIGHT and the "Text size" slider. The WIDTH never changes the text size:
        // when the island is narrow, parts step aside (AM/PM first, then the weather) instead of shrinking.
        double hr = _s.CollapsedHeight / 36.0;
        double fh = Math.Clamp(hr, 0.7, 1.3);                          // cover art, dots
        double ft = Math.Clamp(hr, 0.9, 1.3) * _s.TextScale / 100.0;   // time, weather, song, battery
        var art = new ScaleTransform(fh, fh);
        var txt = new ScaleTransform(ft, ft);
        art.Freeze();
        txt.Freeze();
        ArtSmall.LayoutTransform = art;
        PrivBox.LayoutTransform = art;
        IdlePanel.LayoutTransform = txt;
        SongText.LayoutTransform = txt;
        RightCluster.LayoutTransform = txt;
        double f = ft;

        bool narrow = w < 260, tiny = w < 200;
        double edge = tiny ? 10 : narrow ? 13 : 16;

        ArtSmall.Margin = new Thickness(edge, 0, narrow ? 6 : 8, 0);
        bool priv = _camOn || _micOn;
        RightCluster.Margin = new Thickness(8, 0, priv ? edge + 14 * f : edge, 0);
        PrivBox.Margin = new Thickness(0, 0, Math.Max(8, edge - 3), 0);

        // Fit in steps: time + AM/PM + weather -> time + weather (no AM/PM) -> time only.
        // The text keeps its size the whole time.
        bool showWx = false, hideAp = false;
        if (_hasWx)
        {
            if (175 * f + 2 * edge <= w) showWx = true;
            else if (150 * f + 2 * edge <= w) { showWx = true; hideAp = true; }
        }
        if (!showWx && 72 * f + 2 * edge > w) hideAp = true;
        _hideAmPm = hideAp;

        IdleDivider.Visibility = showWx ? Visibility.Visible : Visibility.Collapsed;
        WxBox.Visibility = showWx ? Visibility.Visible : Visibility.Collapsed;
        bool wide = w >= 300;
        GridLength half = showWx && wide ? new GridLength(78) : GridLength.Auto;
        IdleCol0.Width = half;
        IdleCol2.Width = half;
        IdleDivider.Margin = new Thickness(showWx && !wide ? 10 : 0, 0, showWx && !wide ? 10 : 0, 0);

        SongText.FontSize = 13;
        Bars.Margin = new Thickness(0);

        UpdateCenter();
    }

    // ---------- Play / pause icon ----------

    private void SetPlayIcon(bool playing)
    {
        bool pause = playing;
        if (pause == _playIconIsPause && PlayIcon.Data != null && _playIconInit) return;
        _playIconInit = true;
        _playIconIsPause = pause;
        PlayIcon.Data = (Geometry)FindResource(pause ? "PauseGeo" : "PlayGeo");
        // The new icon pops in with a small spring
        var a = new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new SpringEase { Damping = 0.7 } };
        PlayIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        PlayIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private bool _playIconInit;
}

