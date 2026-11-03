using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LiteIsland;

/// <summary>
/// Soft, glossy weather icons drawn from plain vector shapes (no bitmaps, no blur effects). They are animated
/// (rotating sun rays, drifting clouds, falling rain and snow, lightning flashes), but the animations only run while
/// the weather page is on screen: <see cref="Activate"/> starts and stops them, so they cost nothing the rest of the time.
/// The canvas is 100 x 100; put it in a Viewbox.
/// </summary>
public static class WeatherArt
{
    private sealed record Anim(IAnimatable Target, DependencyProperty Prop, AnimationTimeline Timeline);

    private static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static LinearGradientBrush Lin(string top, string bottom) => Fz(new LinearGradientBrush(
        (Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90));

    private static RadialGradientBrush Rad(string light, string mid, string edge)
    {
        var r = new RadialGradientBrush { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.5, 0.5), RadiusX = 0.65, RadiusY = 0.65 };
        r.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(light), 0));
        r.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(mid), 0.55));
        r.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(edge), 1));
        return Fz(r);
    }

    private static Brush Solid(string c) => Fz(new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)));

    private static readonly Brush CloudWhite = Lin("#FFFFFF", "#CFDDF3");
    private static readonly Brush CloudGrey = Lin("#EEF2F9", "#A9B7CE");
    private static readonly Brush CloudDark = Lin("#B4BFD2", "#6E7B93");
    private static readonly Brush CloudBack = Lin("#C9D6EA", "#8FA0BC");
    private static readonly Brush SunDisc = Rad("#FFF2A8", "#FFC93C", "#FFAA00");
    private static readonly Brush SunRay = Solid("#FFC93C");
    private static readonly Brush MoonFill = Lin("#FFF7CF", "#F3CE62");
    private static readonly Brush Star = Solid("#FFF6D0");
    private static readonly Brush DropBrush = Solid("#4DA3FF");
    private static readonly Brush FlakeBrush = Solid("#FFFFFF");
    private static readonly Brush BoltBrush = Lin("#FFE867", "#FFA400");
    private static readonly Brush FogBrush = Solid("#E8EEF8");
    private static readonly Brush ShadowBrush = Fz(new SolidColorBrush(Color.FromArgb(0x26, 0, 0x10, 0x40)));

    // ---------- card background (sky) ----------

    public static string KindOf(int code, bool day)
    {
        if (code <= 1) return day ? "sun" : "moon";
        if (code == 2) return day ? "partly" : "cloud";
        if (code == 3) return "cloud";
        if (code == 45 || code == 48) return "fog";
        if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) return "rain";
        if ((code >= 71 && code <= 77) || code == 85 || code == 86) return "snow";
        return code >= 95 ? "storm" : "cloud";
    }

    private static readonly Dictionary<string, Brush> Skies = new();

    /// <summary>The card background: a soft sky gradient that matches the weather and the time of day.</summary>
    public static Brush Sky(int code, bool day)
    {
        string k = KindOf(code, day);
        string key = k + (day ? "d" : "n");
        if (Skies.TryGetValue(key, out Brush? b)) return b;
        (string a, string z) = (k, day) switch
        {
            ("sun", true) or ("partly", true) => ("#58A6F5", "#1C6AE4"),
            ("sun", false) or ("moon", _) => ("#2E3D6E", "#0D1530"),
            ("cloud", true) => ("#8396B2", "#4C5C78"),
            ("cloud", false) => ("#3C4760", "#1B2232"),
            ("rain", true) => ("#62738D", "#2F3B52"),
            ("rain", false) => ("#3A455B", "#171D2B"),
            ("snow", true) => ("#93A8C4", "#5C7090"),
            ("snow", false) => ("#47546E", "#232C3E"),
            ("storm", _) => ("#4C5270", "#1E2134"),
            ("fog", true) => ("#93A0AF", "#5F6B7A"),
            _ => ("#3F4856", "#1E242E"),
        };
        var g = new LinearGradientBrush { StartPoint = new Point(0.15, 0), EndPoint = new Point(0.85, 1) };
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(a), 0));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(z), 1));
        g.Freeze();
        Skies[key] = g;
        return g;
    }

    // ---------- shapes ----------

    private static readonly Geometry CloudShape = MakeCloud();

    private static Geometry MakeCloud()
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(33, 53), 17, 17));
        g.Children.Add(new EllipseGeometry(new Point(54, 41), 23, 23));
        g.Children.Add(new EllipseGeometry(new Point(76, 55), 15, 15));
        g.Children.Add(new RectangleGeometry(new Rect(14, 52, 78, 22), 11, 11));
        g.Freeze();
        return g;
    }

    private static Canvas Group(Canvas parent, double x, double y, double s, out TranslateTransform move)
    {
        var c = new Canvas { IsHitTestVisible = false };
        move = new TranslateTransform();
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(s, s));
        tg.Children.Add(new TranslateTransform(x, y));
        tg.Children.Add(move);
        c.RenderTransform = tg;
        parent.Children.Add(c);
        return c;
    }

    private static TranslateTransform Cloud(Canvas cv, double x, double y, double s, Brush fill)
    {
        Canvas g = Group(cv, x, y, s, out TranslateTransform move);
        g.Children.Add(new Path { Data = CloudShape, Fill = ShadowBrush, RenderTransform = new TranslateTransform(0, 3.5) });
        g.Children.Add(new Path { Data = CloudShape, Fill = fill });
        return move;
    }

    private static void Disc(Canvas cv, double cx, double cy, double r, Brush b)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = b, IsHitTestVisible = false };
        Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
        cv.Children.Add(e);
    }

    private static Ellipse Dot(Canvas cv, double cx, double cy, double r, Brush b)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = b, IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
        Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
        cv.Children.Add(e);
        return e;
    }

    private static Line Bar(Canvas cv, double x1, double y1, double x2, double y2, Brush b, double th)
    {
        var l = new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = b, StrokeThickness = th,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false,
            RenderTransform = new TranslateTransform()
        };
        cv.Children.Add(l);
        return l;
    }

    // Sun: soft disc with eight rays that turn slowly
    private static void Sun(Canvas cv, List<Anim> an, double cx, double cy, double r)
    {
        var rays = new Canvas { IsHitTestVisible = false };
        var rot = new RotateTransform(0, cx, cy);
        rays.RenderTransform = rot;
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            Bar(rays, cx + Math.Cos(a) * (r + 7), cy + Math.Sin(a) * (r + 7), cx + Math.Cos(a) * (r + 14), cy + Math.Sin(a) * (r + 14), SunRay, 5).RenderTransform = null;
        }
        cv.Children.Add(rays);
        Disc(cv, cx, cy, r, SunDisc);
        an.Add(new Anim(rot, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(36)) { RepeatBehavior = RepeatBehavior.Forever }));
    }

    private static void Drift(List<Anim> an, TranslateTransform t, double amount, double seconds, double delay = 0)
    {
        an.Add(new Anim(t, TranslateTransform.XProperty, new DoubleAnimation(-amount, amount, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        }));
    }

    private static void Fall(List<Anim> an, UIElement e, double dist, double seconds, double delay, double sway = 0)
    {
        var t = (TranslateTransform)e.RenderTransform;
        an.Add(new Anim(t, TranslateTransform.YProperty, new DoubleAnimation(0, dist, TimeSpan.FromSeconds(seconds))
        { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay) }));
        an.Add(new Anim(e, UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(seconds), RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay),
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)),
                new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.18)),
                new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.65)),
                new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)),
            }
        }));
        if (sway > 0)
            an.Add(new Anim(t, TranslateTransform.XProperty, new DoubleAnimation(-sway, sway, TimeSpan.FromSeconds(seconds / 1.5))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }));
    }

    public static FrameworkElement Build(int code, bool day)
    {
        var cv = new Canvas { Width = 100, Height = 100, IsHitTestVisible = false, ClipToBounds = false };
        var an = new List<Anim>();
        switch (KindOf(code, day))
        {
            case "sun":
                Sun(cv, an, 50, 50, 21);
                break;
            case "moon":
            {
                var g = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(46, 52), 27, 27), new EllipseGeometry(new Point(60, 42), 23, 23));
                g.Freeze();
                cv.Children.Add(new Path { Data = g, Fill = MoonFill, IsHitTestVisible = false });
                for (int i = 0; i < 3; i++)
                {
                    Ellipse s = Dot(cv, 66 + i * 9 - (i == 2 ? 14 : 0), 28 + i * 14 + (i == 2 ? 6 : 0), i == 1 ? 2.4 : 1.8, Star);
                    an.Add(new Anim(s, UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(1.6 + i * 0.5))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(i * 0.4) }));
                }
                break;
            }
            case "partly":
                Sun(cv, an, 64, 36, 16);
                Drift(an, Cloud(cv, -2, 8, 0.96, CloudWhite), 3.5, 4.5);
                break;
            case "cloud":
                Drift(an, Cloud(cv, 20, -6, 0.7, CloudBack), 3, 5.5, 0.6);
                Drift(an, Cloud(cv, -4, 12, 0.96, CloudWhite), 4, 4.6);
                break;
            case "fog":
            {
                Drift(an, Cloud(cv, 0, -6, 0.94, CloudWhite), 3, 5);
                double[] ys = { 80, 88, 96 };
                for (int i = 0; i < 3; i++)
                {
                    Line l = Bar(cv, 18 + i * 6, ys[i], 78 - i * 4, ys[i], FogBrush, 4.5);
                    l.Opacity = 0.9 - i * 0.2;
                    Drift(an, (TranslateTransform)l.RenderTransform, 5, 3 + i * 0.9, i * 0.3);
                }
                break;
            }
            case "rain":
            {
                Drift(an, Cloud(cv, -2, -8, 0.96, CloudGrey), 3, 5);
                for (int i = 0; i < 4; i++)
                    Fall(an, Bar(cv, 30 + i * 14, 72, 27 + i * 14, 80, DropBrush, 4.5), 16, 1.1, i * 0.27);
                break;
            }
            case "snow":
            {
                Drift(an, Cloud(cv, -2, -8, 0.96, CloudGrey), 3, 5);
                for (int i = 0; i < 5; i++)
                    Fall(an, Dot(cv, 26 + i * 11, 74, 3.2, FlakeBrush), 18, 2.6, i * 0.5, 3);
                break;
            }
            default: // storm
            {
                Drift(an, Cloud(cv, -2, -8, 0.96, CloudDark), 3, 5);
                var bolt = new Polygon
                {
                    Points = new PointCollection { new Point(55, 62), new Point(40, 84), new Point(51, 84), new Point(45, 100), new Point(66, 76), new Point(55, 76), new Point(61, 62) },
                    Fill = BoltBrush, IsHitTestVisible = false, Opacity = 0.15
                };
                cv.Children.Add(bolt);
                an.Add(new Anim(bolt, UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromSeconds(2.6), RepeatBehavior = RepeatBehavior.Forever,
                    KeyFrames =
                    {
                        new DiscreteDoubleKeyFrame(0.15, KeyTime.FromPercent(0)),
                        new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0.55)),
                        new DiscreteDoubleKeyFrame(0.2, KeyTime.FromPercent(0.62)),
                        new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0.68)),
                        new DiscreteDoubleKeyFrame(0.15, KeyTime.FromPercent(0.8)),
                    }
                }));
                break;
            }
        }
        cv.Tag = an;
        return cv;
    }

    /// <summary>Starts or stops the animation (only while the weather page is visible).</summary>
    public static void Activate(FrameworkElement? art, bool on)
    {
        if (art?.Tag is not List<Anim> list) return;
        foreach (Anim a in list)
        {
            try { a.Target.BeginAnimation(a.Prop, on ? a.Timeline : null); } catch { }
        }
    }
}
