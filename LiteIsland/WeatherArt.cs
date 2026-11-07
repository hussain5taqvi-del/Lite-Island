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

    // ---------- full-card scene (the whole card moves, not only the icon) ----------

    private const double SceneW = 360, SceneH = 150;
    private static Brush A(byte a) => Fz(new SolidColorBrush(Color.FromArgb(a, 255, 255, 255)));

    private static void Reg(List<Anim> an, IAnimatable t, DependencyProperty p, AnimationTimeline tl)
    {
        Timeline.SetDesiredFrameRate(tl, 30);
        an.Add(new Anim(t, p, tl));
    }

    private static DoubleAnimation Loop(double from, double to, double sec, double delay = 0, bool rev = false, bool ease = false)
    {
        var d = new DoubleAnimation(from, to, TimeSpan.FromSeconds(sec))
        { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = rev, BeginTime = TimeSpan.FromSeconds(delay) };
        if (ease) d.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
        return d;
    }

    private static Brush Vert(Color top, byte topA, byte botA)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(topA, top.R, top.G, top.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(botA, top.R, top.G, top.B), 1));
        g.Freeze();
        return g;
    }

    private static Brush SoftBall(Color c)
    {
        var g = new RadialGradientBrush();
        g.GradientStops.Add(new GradientStop(Color.FromArgb(255, c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(150, c.R, c.G, c.B), 0.45));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        g.Freeze();
        return g;
    }

    /// <summary>A soft cloud that drifts across the whole card; near layers are bigger and faster (depth).</summary>
    private static void CloudLayer(Canvas cv, List<Anim> an, Color tint, double y, double scale, byte topA, byte botA, double sec, double offset)
    {
        var tt = new TranslateTransform();
        var c = new Canvas { IsHitTestVisible = false, RenderTransform = tt };
        c.Children.Add(new Path { Data = CloudShape, Fill = Vert(tint, topA, botA), RenderTransform = new ScaleTransform(scale, scale) });
        Canvas.SetTop(c, y);
        cv.Children.Add(c);
        double w = 100 * scale;
        Reg(an, tt, TranslateTransform.XProperty, Loop(-w - 20, SceneW + 20, sec, -offset * sec));
    }

    private static void Twinkle(Canvas cv, List<Anim> an, Random rnd, int n)
    {
        for (int i = 0; i < n; i++)
        {
            bool big = rnd.NextDouble() < 0.18;
            double r = big ? 1.7 + rnd.NextDouble() * 0.8 : 0.6 + rnd.NextDouble() * 0.9;
            Ellipse e = Dot(cv, rnd.NextDouble() * SceneW, rnd.NextDouble() * SceneH, r, Star);
            e.Opacity = 0.2;
            Reg(an, e, UIElement.OpacityProperty, Loop(0.1, big ? 1 : 0.8, 0.9 + rnd.NextDouble() * 2.4, rnd.NextDouble() * 2, true, true));
        }
    }

    private static void ShootingStar(Canvas cv, List<Anim> an, double x, double y, double period, double at)
    {
        var l = Bar(cv, 0, 0, 30, -15, Star, 1.6);
        Canvas.SetLeft(l, x); Canvas.SetTop(l, y);
        l.Opacity = 0;
        var t = (TranslateTransform)l.RenderTransform;
        DoubleAnimationUsingKeyFrames K(double to, bool discrete)
        {
            var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(period), RepeatBehavior = RepeatBehavior.Forever };
            k.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            k.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(at)));
            k.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromPercent(Math.Min(1, at + 0.1))));
            k.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromPercent(1)));
            return k;
        }
        Reg(an, t, TranslateTransform.XProperty, K(-150, false));
        Reg(an, t, TranslateTransform.YProperty, K(75, false));
        var o = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(period), RepeatBehavior = RepeatBehavior.Forever };
        o.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        o.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.95, KeyTime.FromPercent(at)));
        o.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(Math.Min(1, at + 0.1))));
        Reg(an, l, UIElement.OpacityProperty, o);
    }

    private static void RainLayer(Canvas cv, List<Anim> an, Random rnd, int n, double sec0, double thick, int alphaMin, int alphaMax, double lenMin, double lenMax, double slant)
    {
        for (int i = 0; i < n; i++)
        {
            double x = rnd.NextDouble() * (SceneW + 90);
            double len = lenMin + rnd.NextDouble() * (lenMax - lenMin);
            Line l = Bar(cv, x, 0, x - len * slant, len, A((byte)rnd.Next(alphaMin, alphaMax)), thick);
            double sec = sec0 * (0.8 + rnd.NextDouble() * 0.4);
            double delay = -rnd.NextDouble() * sec;
            var t = (TranslateTransform)l.RenderTransform;
            double fall = SceneH + 30;
            Reg(an, t, TranslateTransform.YProperty, Loop(-len - 4, fall, sec, delay));
            Reg(an, t, TranslateTransform.XProperty, Loop(0, -(fall + len) * slant, sec, delay));
        }
    }

    private static void Splashes(Canvas cv, List<Anim> an, Random rnd, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var sc = new ScaleTransform(0.2, 0.2, 9, 2);
            var e = new Ellipse { Width = 18, Height = 4, Stroke = A(0xB0), StrokeThickness = 1, IsHitTestVisible = false, RenderTransform = sc, Opacity = 0 };
            Canvas.SetLeft(e, rnd.NextDouble() * (SceneW - 20)); Canvas.SetTop(e, SceneH - 4 - rnd.NextDouble() * 12);
            cv.Children.Add(e);
            double sec = 0.9 + rnd.NextDouble() * 0.6, delay = -rnd.NextDouble() * sec;
            Reg(an, sc, ScaleTransform.ScaleXProperty, Loop(0.2, 1.6, sec, delay));
            Reg(an, sc, ScaleTransform.ScaleYProperty, Loop(0.2, 1.6, sec, delay));
            Reg(an, e, UIElement.OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(sec)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay) });
        }
    }

    private static void SnowLayer(Canvas cv, List<Anim> an, Random rnd, int n, double rMin, double rMax, double sec0, double sway, double alpha)
    {
        Brush ball = SoftBall(Colors.White);
        for (int i = 0; i < n; i++)
        {
            double r = rMin + rnd.NextDouble() * (rMax - rMin);
            var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = ball, Opacity = alpha * (0.6 + rnd.NextDouble() * 0.4), IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
            Canvas.SetLeft(e, rnd.NextDouble() * SceneW); Canvas.SetTop(e, 0);
            cv.Children.Add(e);
            double sec = sec0 * (0.8 + rnd.NextDouble() * 0.5), delay = -rnd.NextDouble() * sec;
            var t = (TranslateTransform)e.RenderTransform;
            Reg(an, t, TranslateTransform.YProperty, Loop(-r * 2 - 4, SceneH + 6, sec, delay));
            Reg(an, t, TranslateTransform.XProperty, Loop(-sway, sway, 2.4 + rnd.NextDouble() * 2.2, delay, true, true));
        }
    }

    private static void Glow(Canvas cv, List<Anim> an, double cx, double cy, double r, Color c, byte alpha)
    {
        var g = new RadialGradientBrush();
        g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha / 3), c.R, c.G, c.B), 0.5));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        g.Freeze();
        var sc = new ScaleTransform(1, 1, r, r);
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = g, IsHitTestVisible = false, RenderTransform = sc };
        Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
        cv.Children.Add(e);
        Reg(an, sc, ScaleTransform.ScaleXProperty, Loop(0.9, 1.14, 4.2, 0, true, true));
        Reg(an, sc, ScaleTransform.ScaleYProperty, Loop(0.9, 1.14, 4.2, 0, true, true));
        Reg(an, e, UIElement.OpacityProperty, Loop(0.7, 1, 4.2, 0, true, true));
    }

    /// <summary>Thin light beams that fade out with the distance from the sun (no hard petals).</summary>
    private static void Rays(Canvas cv, List<Anim> an, double cx, double cy, int count, double width, byte alpha, double sec, bool reverse)
    {
        var holder = new Canvas { IsHitTestVisible = false };
        var rot = new RotateTransform(0, cx, cy);
        holder.RenderTransform = rot;
        var mask = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = new Point(cx, cy), GradientOrigin = new Point(cx, cy), RadiusX = 210, RadiusY = 210 };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(110, 0, 0, 0), 0.45));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        mask.Freeze();
        holder.OpacityMask = mask;
        for (int i = 0; i < count; i++)
        {
            double a = i * Math.PI * 2 / count, L = 260;
            holder.Children.Add(new Polygon
            {
                Points = new PointCollection
                {
                    new Point(cx, cy),
                    new Point(cx + Math.Cos(a - width) * L, cy + Math.Sin(a - width) * L),
                    new Point(cx + Math.Cos(a + width) * L, cy + Math.Sin(a + width) * L)
                },
                Fill = A(alpha), IsHitTestVisible = false
            });
        }
        cv.Children.Add(holder);
        Reg(an, rot, RotateTransform.AngleProperty, Loop(reverse ? 360 : 0, reverse ? 0 : 360, sec));
    }

    private static void Flare(Canvas cv, List<Anim> an, double x, double y, double r, byte alpha, double sec, double delay)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = SoftBall(Color.FromRgb(0xFF, 0xE9, 0xA8)), IsHitTestVisible = false, Opacity = alpha / 255.0, RenderTransform = new TranslateTransform() };
        Canvas.SetLeft(e, x - r); Canvas.SetTop(e, y - r);
        cv.Children.Add(e);
        var t = (TranslateTransform)e.RenderTransform;
        Reg(an, t, TranslateTransform.XProperty, Loop(-10, 10, sec, delay, true, true));
        Reg(an, t, TranslateTransform.YProperty, Loop(-4, 4, sec * 1.3, delay, true, true));
        Reg(an, e, UIElement.OpacityProperty, Loop(alpha / 255.0 * 0.4, alpha / 255.0, sec * 0.8, delay, true, true));
    }

    private static void Motes(Canvas cv, List<Anim> an, Random rnd, int n, byte alpha)
    {
        for (int i = 0; i < n; i++)
        {
            Ellipse e = Dot(cv, rnd.NextDouble() * SceneW, 0, 0.8 + rnd.NextDouble() * 1.4, A(alpha));
            double sec = 7 + rnd.NextDouble() * 6, delay = -rnd.NextDouble() * sec;
            var t = (TranslateTransform)e.RenderTransform;
            Reg(an, t, TranslateTransform.YProperty, Loop(SceneH + 6, -8, sec, delay));
            Reg(an, t, TranslateTransform.XProperty, Loop(-14, 14, 3 + rnd.NextDouble() * 3, delay, true, true));
            Reg(an, e, UIElement.OpacityProperty, Loop(0.1, 0.9, 1.6 + rnd.NextDouble() * 2, delay, true, true));
        }
    }

    private static void Aurora(Canvas cv, List<Anim> an, Color c, double y, double rot, double sec, double delay)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0x4A, c.R, c.G, c.B), 0.5));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        g.Freeze();
        var tt = new TransformGroup();
        var move = new TranslateTransform();
        tt.Children.Add(new RotateTransform(rot, 150, 20));
        tt.Children.Add(move);
        var r = new Rectangle { Width = 300, Height = 40, RadiusX = 20, RadiusY = 20, Fill = g, IsHitTestVisible = false, RenderTransform = tt };
        Canvas.SetLeft(r, 30); Canvas.SetTop(r, y);
        cv.Children.Add(r);
        Reg(an, move, TranslateTransform.XProperty, Loop(-60, 60, sec, delay, true, true));
        Reg(an, move, TranslateTransform.YProperty, Loop(-8, 8, sec * 0.7, delay, true, true));
        Reg(an, r, UIElement.OpacityProperty, Loop(0.35, 1, sec * 0.6, delay, true, true));
    }

    private static void Lightning(Canvas cv, List<Anim> an, Random rnd, double x0, double period, double at)
    {
        var pts = new PointCollection();
        double x = x0, y = 0;
        pts.Add(new Point(x, y));
        for (int i = 0; i < 7; i++) { y += 15 + rnd.NextDouble() * 6; x += (rnd.NextDouble() - 0.5) * 30; pts.Add(new Point(x, y)); }
        var glow = new Polyline { Points = pts, Stroke = A(0x55), StrokeThickness = 7, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false, Opacity = 0 };
        var core = new Polyline { Points = pts, Stroke = Solid("#FFF8D0"), StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false, Opacity = 0 };
        var flash = new Rectangle { Width = SceneW, Height = SceneH, Fill = Brushes.White, Opacity = 0, IsHitTestVisible = false };
        cv.Children.Add(flash); cv.Children.Add(glow); cv.Children.Add(core);
        DoubleAnimationUsingKeyFrames K(double peak)
        {
            var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(period), RepeatBehavior = RepeatBehavior.Forever };
            k.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            k.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, KeyTime.FromPercent(at)));
            k.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak * 0.1, KeyTime.FromPercent(at + 0.03)));
            k.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak * 0.8, KeyTime.FromPercent(at + 0.06)));
            k.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(Math.Min(1, at + 0.16))));
            return k;
        }
        Reg(an, flash, UIElement.OpacityProperty, K(0.32));
        Reg(an, glow, UIElement.OpacityProperty, K(1));
        Reg(an, core, UIElement.OpacityProperty, K(1));
    }

    /// <summary>The animated sky behind the time and the temperature: rain with splashes, snow in layers, stars and aurora, sun light, parallax clouds, lightning, fog.</summary>
    public static FrameworkElement Scene(int code, bool day)
    {
        var cv = new Canvas { Width = SceneW, Height = SceneH, IsHitTestVisible = false };
        var an = new List<Anim>();
        var rnd = new Random(code * 7 + (day ? 1 : 2));
        string k = KindOf(code, day);
        Color white = Colors.White;
        Color grey = day ? Color.FromRgb(0xE6, 0xEC, 0xF6) : Color.FromRgb(0x9A, 0xA6, 0xC0);
        Color dark = day ? Color.FromRgb(0x4A, 0x55, 0x6C) : Color.FromRgb(0x2A, 0x32, 0x48);
        switch (k)
        {
            case "sun":
                Glow(cv, an, 304, 52, 150, Color.FromRgb(0xFF, 0xE0, 0x80), 0xA0);
                Rays(cv, an, 304, 52, 14, 0.035, 0x1C, 150, false);
                Flare(cv, an, 236, 78, 12, 0x60, 5, 0);
                Flare(cv, an, 170, 104, 7, 0x55, 4, 1);
                Flare(cv, an, 112, 126, 16, 0x40, 6, 2);
                Motes(cv, an, rnd, 16, 0xA0);
                CloudLayer(cv, an, white, 96, 1.5, 0x30, 0x10, 70, 0.3);
                break;
            case "moon":
                Aurora(cv, an, Color.FromRgb(0x6B, 0xFF, 0xD2), 18, -14, 9, 0);
                Aurora(cv, an, Color.FromRgb(0xA4, 0x7B, 0xFF), 52, 10, 12, 2);
                Glow(cv, an, 304, 52, 130, Color.FromRgb(0xA8, 0xB8, 0xFF), 0x60);
                Twinkle(cv, an, rnd, 40);
                ShootingStar(cv, an, 250, 14, 8, 0.82);
                ShootingStar(cv, an, 120, 6, 13, 0.55);
                break;
            case "partly":
                Glow(cv, an, 304, 52, 125, Color.FromRgb(0xFF, 0xE0, 0x80), 0x80);
                Rays(cv, an, 304, 52, 12, 0.03, 0x18, 160, false);
                Flare(cv, an, 220, 84, 10, 0x4A, 5, 0);
                CloudLayer(cv, an, white, 4, 1.1, 0x80, 0x28, 52, 0.0);
                CloudLayer(cv, an, white, 46, 1.9, 0xA0, 0x38, 38, 0.5);
                CloudLayer(cv, an, white, 92, 2.5, 0x90, 0x30, 26, 0.2);
                Motes(cv, an, rnd, 8, 0x80);
                break;
            case "cloud":
                CloudLayer(cv, an, grey, 0, 1.2, 0x70, 0x20, 60, 0.0);
                CloudLayer(cv, an, grey, 38, 1.9, 0x98, 0x30, 42, 0.45);
                CloudLayer(cv, an, grey, 86, 2.5, 0x88, 0x28, 28, 0.15);
                CloudLayer(cv, an, white, 20, 1.4, 0x50, 0x14, 34, 0.75);
                if (!day) Twinkle(cv, an, rnd, 12);
                break;
            case "fog":
                CloudLayer(cv, an, grey, 0, 1.6, 0x60, 0x20, 70, 0.1);
                for (int i = 0; i < 5; i++)
                {
                    var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                    g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
                    g.GradientStops.Add(new GradientStop(Color.FromArgb(0x78, 255, 255, 255), 0.5));
                    g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
                    g.Freeze();
                    var tt = new TranslateTransform();
                    var r = new Rectangle { Width = 280, Height = 30, RadiusX = 15, RadiusY = 15, Fill = g, IsHitTestVisible = false, RenderTransform = tt };
                    Canvas.SetTop(r, 8 + i * 30); Canvas.SetLeft(r, 20 + (i % 2) * 50);
                    cv.Children.Add(r);
                    Reg(an, tt, TranslateTransform.XProperty, Loop(-90, 90, 10 + i * 2.2, -i * 2.5, true, true));
                    Reg(an, r, UIElement.OpacityProperty, Loop(0.45, 1, 5 + i, -i, true, true));
                }
                break;
            case "rain":
                CloudLayer(cv, an, dark, -14, 2.0, 0xB0, 0x30, 64, 0.0);
                CloudLayer(cv, an, dark, -26, 2.8, 0x98, 0x20, 44, 0.5);
                RainLayer(cv, an, rnd, 30, 1.25, 1.0, 50, 100, 8, 13, 0.30);
                RainLayer(cv, an, rnd, 22, 0.7, 1.7, 110, 190, 13, 20, 0.30);
                Splashes(cv, an, rnd, 11);
                break;
            case "snow":
                CloudLayer(cv, an, grey, -16, 2.2, 0x88, 0x20, 70, 0.0);
                var gl = new Rectangle { Width = SceneW, Height = 34, Fill = Vert(white, 0x00, 0x34), IsHitTestVisible = false };
                Canvas.SetTop(gl, SceneH - 34);
                cv.Children.Add(gl);
                SnowLayer(cv, an, rnd, 22, 1.0, 1.8, 10.5, 6, 0.55);
                SnowLayer(cv, an, rnd, 16, 1.8, 3.0, 7.5, 10, 0.8);
                SnowLayer(cv, an, rnd, 8, 3.4, 5.2, 5.5, 14, 0.9);
                break;
            default: // storm
                CloudLayer(cv, an, dark, -14, 2.0, 0xC8, 0x40, 46, 0.0);
                CloudLayer(cv, an, dark, -28, 2.8, 0xB0, 0x30, 32, 0.5);
                RainLayer(cv, an, rnd, 34, 0.85, 1.0, 60, 110, 9, 14, 0.38);
                RainLayer(cv, an, rnd, 26, 0.5, 1.8, 120, 200, 14, 22, 0.38);
                Splashes(cv, an, rnd, 9);
                Lightning(cv, an, rnd, 168, 5.5, 0.62);
                Lightning(cv, an, rnd, 96, 8.5, 0.28);
                break;
        }
        var vb = new Viewbox { Stretch = Stretch.UniformToFill, IsHitTestVisible = false, Child = cv, Tag = an };
        return vb;
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
