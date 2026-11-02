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

    private static void Cloud(Canvas cv, double dx, double dy, double s, Brush b)
    {
        // three puffs plus a rounded base, all sharing one gradient so it reads as one soft 3D cloud
        Disc(cv, dx + 12 * s, dy + 34 * s, 36 * s, b);
        Disc(cv, dx + 30 * s, dy + 18 * s, 44 * s, b);
        Disc(cv, dx + 56 * s, dy + 32 * s, 34 * s, b);
        var bar = new Rectangle { Width = 78 * s, Height = 28 * s, RadiusX = 14 * s, RadiusY = 14 * s, Fill = b, IsHitTestVisible = false };
        Canvas.SetLeft(bar, dx + 10 * s); Canvas.SetTop(bar, dy + 46 * s);
        cv.Children.Add(bar);
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
                Disc(cv, 0, -6, 84, Glow);
                Disc(cv, 12, 6, 46, SunBrush);
                Cloud(cv, 8, 22, 0.95, CloudLight);
                break;
            case "cloud":
                Cloud(cv, 12, 2, 0.7, CloudDark);
                Cloud(cv, 0, 18, 1.0, CloudLight);
                break;
            case "fog":
                Cloud(cv, 4, 2, 0.95, CloudLight);
                Line(cv, 16, 78, 78, 78, FogLine, 5);
                Line(cv, 28, 90, 90, 90, FogLine, 5);
                break;
            case "rain":
                Cloud(cv, 4, 0, 1.0, CloudDark);
                for (int i = 0; i < 3; i++) Line(cv, 30 + i * 18, 78, 24 + i * 18, 94, Drop, 5);
                break;
            case "snow":
                Cloud(cv, 4, 0, 1.0, CloudDark);
                for (int i = 0; i < 3; i++) Disc(cv, 28 + i * 18, 80 + (i % 2) * 8, 9, Flake);
                break;
            default: // storm
                Cloud(cv, 4, 0, 1.0, CloudDark);
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
