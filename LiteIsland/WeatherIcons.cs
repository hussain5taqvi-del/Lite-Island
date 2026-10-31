using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LiteIsland;

/// <summary>
/// Small flat weather icons drawn from shapes (no image files, nothing to load).
/// Every icon fits a 24 x 24 box and is drawn in one color.
/// </summary>
public static class WeatherIcons
{
    private static readonly Dictionary<string, Geometry> Cache = new();

    public static Geometry Get(int code, bool isDay)
    {
        string kind = KindOf(code, isDay);
        if (!Cache.TryGetValue(kind, out Geometry? g))
        {
            g = Build(kind);
            if (g.CanFreeze) g.Freeze();
            Cache[kind] = g;
        }
        return g;
    }

    private static string KindOf(int code, bool day)
    {
        if (code == 0 || code == 1) return day ? "sun" : "moon";
        if (code == 2) return day ? "partly" : "cloud";
        if (code == 3) return "cloud";
        if (code == 45 || code == 48) return "fog";
        if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) return "rain";
        if ((code >= 71 && code <= 77) || code == 85 || code == 86) return "snow";
        if (code >= 95) return "storm";
        return "cloud";
    }

    private static Geometry Build(string kind)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        switch (kind)
        {
            case "sun":
                AddSun(g, 12, 12, 4.8, 7.6, 10.8, 8);
                break;

            case "moon":
                return new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(12, 12), 8, 8),
                    new EllipseGeometry(new Point(16.5, 9), 6.8, 6.8));

            case "partly":
                AddSun(g, 8.4, 8.4, 3.4, 5.4, 7.6, 8);
                AddCloud(g, 1.6, 3.2, 0.92);
                break;

            case "rain":
                AddCloud(g, 0, -3.6, 0.9);
                foreach (double x in new[] { 8.0, 11.9, 15.8 })
                {
                    g.Children.Add(new RectangleGeometry(new Rect(x, 16.6, 1.8, 4.4), 0.9, 0.9)
                    {
                        Transform = new RotateTransform(16, x + 0.9, 18.8)
                    });
                }
                break;

            case "snow":
                AddCloud(g, 0, -3.6, 0.9);
                g.Children.Add(new EllipseGeometry(new Point(8.6, 19.2), 1.3, 1.3));
                g.Children.Add(new EllipseGeometry(new Point(12, 21.4), 1.3, 1.3));
                g.Children.Add(new EllipseGeometry(new Point(15.4, 19.2), 1.3, 1.3));
                break;

            case "storm":
                AddCloud(g, 0, -4.6, 0.9);
                g.Children.Add(Geometry.Parse("M13,14.2 L8.8,20.2 L11.6,20.2 L10.6,23.8 L15.4,17.8 L12.6,17.8 L13.8,14.2 Z"));
                break;

            case "fog":
                g.Children.Add(new RectangleGeometry(new Rect(4, 7.5, 16, 2.2), 1.1, 1.1));
                g.Children.Add(new RectangleGeometry(new Rect(6.5, 11.9, 14, 2.2), 1.1, 1.1));
                g.Children.Add(new RectangleGeometry(new Rect(4, 16.3, 16, 2.2), 1.1, 1.1));
                break;

            default: // cloud
                AddCloud(g, 0, 0, 1.0);
                break;
        }
        return g;
    }

    private static void AddSun(GeometryGroup g, double cx, double cy, double r, double r1, double r2, int rays)
    {
        g.Children.Add(new EllipseGeometry(new Point(cx, cy), r, r));
        for (int i = 0; i < rays; i++)
        {
            g.Children.Add(new RectangleGeometry(new Rect(cx - 0.9, cy - r2, 1.8, r2 - r1), 0.9, 0.9)
            {
                Transform = new RotateTransform(i * 360.0 / rays, cx, cy)
            });
        }
    }

    // A cloud made of three circles and a flat bottom, scaled around the middle of the box
    private static void AddCloud(GeometryGroup g, double dx, double dy, double s)
    {
        static double P(double v, double d, double sc) => 12 + (v - 12) * sc + d;

        void Circle(double x, double y, double r) =>
            g.Children.Add(new EllipseGeometry(new Point(P(x, dx, s), P(y, dy, s)), r * s, r * s));

        Circle(7.8, 15.2, 3.6);
        Circle(13.2, 12.2, 5.2);
        Circle(18.4, 15.4, 3.3);
        g.Children.Add(new RectangleGeometry(
            new Rect(P(7.8, dx, s), P(15, dy, s), 10.6 * s, 3.8 * s)));
    }

    // Wind arrow: the arrow points where the wind is going (the API gives where it comes from)
    public static string Arrow(double fromDegrees)
    {
        string[] a = { "↑", "↗", "→", "↘", "↓", "↙", "←", "↖" };
        double to = (fromDegrees + 180) % 360;
        int i = (int)Math.Round(to / 45.0) % 8;
        return a[i];
    }
}
