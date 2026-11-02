using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LiteIsland;

/// <summary>
/// 3D-looking weather art drawn with plain gradients (no bitmap effects, so it stays cheap on low-end PCs).
/// Canvas is 100 x 100; put it in a Viewbox.
/// </summary>
public static class WeatherArt
{
    private static Brush F(Brush b) { b.Freeze(); return b; }

    private static RadialGradientBrush Ball(Color a, Color b, Color c, Color d)
    {
        var r = new RadialGradientBrush { GradientOrigin = new Point(0.32, 0.28), Center = new Point(0.45, 0.45), RadiusX = 0.7, RadiusY = 0.7 };
        r.GradientStops.Add(new GradientStop(a, 0));
        r.GradientStops.Add(new GradientStop(b, 0.28));
        r.GradientStops.Add(new GradientStop(c, 0.62));
        r.GradientStops.Add(new GradientStop(d, 1));
        return r;
    }

    private static readonly Brush CloudLight = F(Ball(Colors.White, Color.FromRgb(0xE8, 0xEA, 0xEE), Color.FromRgb(0xB4, 0xB8, 0xC0), Color.FromRgb(0x7A, 0x7F, 0x89)));
    private static readonly Brush CloudDark = F(Ball(Color.FromRgb(0xC9, 0xCD, 0xD6), Color.FromRgb(0x9A, 0xA0, 0xAC), Color.FromRgb(0x6A, 0x70, 0x7C), Color.FromRgb(0x42, 0x46, 0x50)));
    private static readonly Brush SunBrush = F(Ball(Color.FromRgb(0xFF, 0xF7, 0xC2), Color.FromRgb(0xFF, 0xD1, 0x5A), Color.FromRgb(0xF7, 0xA9, 0x2A), Color.FromRgb(0xEE, 0x8A, 0x10)));
    private static readonly Brush MoonBrush = F(Ball(Colors.White, Color.FromRgb(0xE4, 0xE7, 0xF5), Color.FromRgb(0xB2, 0xB8, 0xD8), Color.FromRgb(0x82, 0x89, 0xB0)));
    private static readonly Brush Glow = F(MakeGlow(Color.FromArgb(0x90, 0xFF, 0xC8, 0x40)));
    private static readonly Brush MoonGlow = F(MakeGlow(Color.FromArgb(0x50, 0xB4, 0xC0, 0xFF)));
    private static readonly Brush Drop = F(new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0xFA)));
    private static readonly Brush Flake = F(new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFF)));
    private static readonly Brush Bolt = F(new LinearGradientBrush(Color.FromRgb(0xFF, 0xE0, 0x6A), Color.FromRgb(0xFF, 0x9F, 0x0A), 90));
    private static readonly Brush FogLine = F(new SolidColorBrush(Color.FromArgb(0xB8, 0xD0, 0xD4, 0xDC)));

    private static RadialGradientBrush MakeGlow(Color c)
    {
        var r = new RadialGradientBrush();
        r.GradientStops.Add(new GradientStop(c, 0));
        r.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        return r;
    }

    private static void Disc(Canvas cv, double x, double y, double d, Brush b)
    {
        var e = new Ellipse { Width = d, Height = d, Fill = b, IsHitTestVisible = false };
        Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
        cv.Children.Add(e);
    }

    // Volumetric cloud: many soft puffs, back to front. Every puff is lit from the top-left and dark at the bottom-right,
    // so where a front puff overlaps a back puff you get real depth. (x, y, radius, shade 0 = bright .. 1 = in shadow)
    private static readonly double[][] Puffs =
    {
        new[] { 22, 66, 17, 0.70 }, new[] { 46, 52, 24, 0.65 }, new[] { 72, 55, 22, 0.70 }, new[] { 90, 70, 16, 0.75 },
        new[] { 14, 78, 14, 0.55 }, new[] { 34, 70, 22, 0.45 }, new[] { 60, 68, 25, 0.40 }, new[] { 84, 80, 18, 0.50 },
        new[] { 30, 48, 18, 0.20 }, new[] { 54, 40, 23, 0.10 }, new[] { 76, 46, 19, 0.18 }, new[] { 48, 62, 21, 0.25 },
        new[] { 22, 88, 13, 0.62 }, new[] { 44, 86, 17, 0.58 }, new[] { 68, 88, 17, 0.60 }, new[] { 88, 90, 12, 0.66 },
    };

    private static readonly System.Collections.Generic.Dictionary<int, Brush> PuffBrushes = new();

    private static Color Mix(Color c, double dark) =>
        Color.FromRgb((byte)(c.R * (1 - dark)), (byte)(c.G * (1 - dark)), (byte)(c.B * (1 - dark)));

    private static Brush PuffBrush(bool dark, double shade)
    {
        int q = (int)Math.Round(shade * 5);
        int key = q + (dark ? 100 : 0);
        if (PuffBrushes.TryGetValue(key, out Brush? b)) return b;
        double d = q / 5.0 * 0.38;
        Color l = dark ? Color.FromRgb(0xC9, 0xCE, 0xD8) : Color.FromRgb(0xFB, 0xFC, 0xFE);
        Color m = dark ? Color.FromRgb(0x8E, 0x95, 0xA4) : Color.FromRgb(0xD2, 0xD7, 0xDF);
        Color sh = dark ? Color.FromRgb(0x58, 0x5E, 0x6B) : Color.FromRgb(0x8C, 0x93, 0xA1);
        Color ed = dark ? Color.FromRgb(0x36, 0x3A, 0x45) : Color.FromRgb(0x5B, 0x61, 0x6F);
        var r = new RadialGradientBrush { GradientOrigin = new Point(0.34, 0.26), Center = new Point(0.48, 0.48), RadiusX = 0.66, RadiusY = 0.66 };
        r.GradientStops.Add(new GradientStop(Mix(l, d), 0));
        r.GradientStops.Add(new GradientStop(Mix(m, d), 0.42));
        r.GradientStops.Add(new GradientStop(Mix(sh, d), 0.82));
        r.GradientStops.Add(new GradientStop(Mix(ed, d), 1));
        r.Freeze();
        PuffBrushes[key] = r;
        return r;
    }

    private static void Cloud(Canvas cv, double dx, double dy, double s, Brush unused, bool dark = false)
    {
        _ = unused;
        foreach (double[] p in Puffs)
        {
            double r = p[2] * s;
            Disc(cv, dx + p[0] * s - r, dy + p[1] * s - r, r * 2, PuffBrush(dark, p[3]));
        }
    }

    private static void Line(Canvas cv, double x1, double y1, double x2, double y2, Brush b, double th)
    {
        cv.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = b, StrokeThickness = th,
                                   StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false });
    }

    public static FrameworkElement Build(int code, bool day)
    {
        var cv = new Canvas { Width = 100, Height = 100, IsHitTestVisible = false };
        string kind = code <= 1 ? (day ? "sun" : "moon")
                    : code == 2 ? (day ? "partly" : "cloud")
                    : code == 3 ? "cloud"
                    : (code == 45 || code == 48) ? "fog"
                    : ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) ? "rain"
                    : ((code >= 71 && code <= 77) || code == 85 || code == 86) ? "snow"
                    : code >= 95 ? "storm" : "cloud";
        switch (kind)
        {
            case "sun":
                Disc(cv, 0, 0, 100, Glow);
                Disc(cv, 22, 22, 56, SunBrush);
                break;
            case "moon":
                Disc(cv, 0, 0, 100, MoonGlow);
                var g = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(46, 52), 28, 28), new EllipseGeometry(new Point(62, 42), 24, 24));
                cv.Children.Add(new System.Windows.Shapes.Path { Data = g, Fill = MoonBrush, IsHitTestVisible = false });
                break;
            case "partly":
                Disc(cv, -6, -14, 84, Glow);
                Disc(cv, 6, -2, 48, SunBrush);
                Cloud(cv, 2, 4, 0.92, CloudLight);
                break;
            case "cloud":
                Cloud(cv, -4, -14, 0.8, CloudDark, true);
                Cloud(cv, 4, 6, 0.95, CloudLight);
                break;
            case "fog":
                Cloud(cv, 0, -20, 0.92, CloudLight);
                Line(cv, 14, 82, 80, 82, FogLine, 5);
                Line(cv, 28, 94, 92, 94, FogLine, 5);
                break;
            case "rain":
                Cloud(cv, 0, -16, 0.95, CloudLight, true);
                for (int i = 0; i < 4; i++) Line(cv, 26 + i * 15, 76, 21 + i * 15, 92, Drop, 4.5);
                break;
            case "snow":
                Cloud(cv, 0, -16, 0.95, CloudLight, true);
                for (int i = 0; i < 4; i++) Disc(cv, 24 + i * 15, 78 + (i % 2) * 9, 8, Flake);
                break;
            default: // storm
                Cloud(cv, 0, -16, 0.95, CloudLight, true);
                var bolt = new Polygon
                {
                    Points = new PointCollection { new Point(54, 66), new Point(40, 88), new Point(51, 88), new Point(45, 102), new Point(66, 78), new Point(54, 78), new Point(60, 66) },
                    Fill = Bolt, IsHitTestVisible = false
                };
                cv.Children.Add(bolt);
                break;
        }
        return cv;
    }
}
