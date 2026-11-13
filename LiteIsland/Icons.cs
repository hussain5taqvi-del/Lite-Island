using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LiteIsland;

/// <summary>Small line icons (24 x 24 grid), drawn as vector paths, so they are sharp at any size and cost no font.</summary>
internal static class Icons
{
    private static readonly Dictionary<string, string> Data = new()
    {
        ["plus"] = "M12,5 V19 M5,12 H19",
        ["phone"] = "M8,3 H16 V21 H8 Z M11,18 H13",
        ["shelf"] = "M4,14 V19 H20 V14 M12,4 V14 M8,10.5 L12,14.5 L16,10.5",
        ["jpg"] = "M4,5 H20 V19 H4 Z M4,16 L9,11 L13,15 L16,12.5 L20,16.5 M8.5,8.5 A1.2,1.2 0 1 0 8.5,8.51",
        ["png"] = "M4,5 H20 V19 H4 Z M4,16 L9,11 L13,15 L16,12.5 L20,16.5 M8.5,8.5 A1.2,1.2 0 1 0 8.5,8.51",
        ["image"] = "M4,5 H20 V19 H4 Z M4,16 L9,11 L13,15 L16,12.5 L20,16.5 M8.5,8.5 A1.2,1.2 0 1 0 8.5,8.51",
        ["compress"] = "M10,4 V10 H4 M14,20 V14 H20 M4,4 L10,10 M20,20 L14,14",
        ["cutout"] = "M12,5 A3,3 0 1 0 12,11 A3,3 0 1 0 12,5 Z M6,20 C6,15.5 18,15.5 18,20",
        ["text"] = "M4,9 V5 H8 M16,5 H20 V9 M20,15 V19 H16 M8,19 H4 V15 M8,10 H16 M8,13 H16 M8,16 H12",
        ["pdf"] = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19 M9,13 H16 M9,16.5 H16",
        ["file"] = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19",
        ["zip"] = "M5,4 H19 V20 H5 Z M11,4 V8 H13 V4 M11,8 V11 H13 V8 M11,11 V14 H13 V11 M10.5,14 H13.5 V18 H10.5 Z",
        ["video"] = "M3,7 H15 V17 H3 Z M15,11 L21,7.5 V16.5 L15,13",
        ["gif"] = "M4,5 H20 V19 H4 Z M10,9 L15.5,12 L10,15 Z",
        ["audio"] = "M4,10 V14 M8,7 V17 M12,4 V20 M16,8 V16 M20,11 V13",
        ["note"] = "M9,17 V5 L19,3 V15 M9,17 A3,3 0 1 1 3,17 A3,3 0 0 1 9,17 M19,15 A3,3 0 1 1 13,15 A3,3 0 0 1 19,15",
        ["merge"] = "M4,4 H12 V12 H4 Z M12,12 H20 V20 H12 Z M12,8 H16 V12 M8,12 V16 H12",
        ["mute"] = "M4,9 H8 L13,5 V19 L8,15 H4 Z M17,9 L21,15 M21,9 L17,15",
        ["copy"] = "M8,8 H19 V20 H8 Z M5,16 V4 H15",
        ["cut"] = "M6,6 A2.5,2.5 0 1 0 6,11 A2.5,2.5 0 1 0 6,6 Z M6,13 A2.5,2.5 0 1 0 6,18 A2.5,2.5 0 1 0 6,13 Z M8,9.5 L20,17 M8,14.5 L20,7",
        ["timer"] = "M12,6 A8,8 0 1 0 12,22 A8,8 0 1 0 12,6 Z M12,10 V14 L15,16 M10,3 H14",
        ["folder"] = "M3,6 H9 L11,8 H21 V19 H3 Z",
        ["download"] = "M12,4 V15 M7,10.5 L12,15.5 L17,10.5 M5,20 H19",
        ["close"] = "M6,6 L18,18 M18,6 L6,18",
        ["chevron"] = "M6,9 L12,15 L18,9",
        ["back"] = "M15,5 L8,12 L15,19",
        ["link"] = "M10,14 L14,10 M9,11 L6.5,13.5 A3,3 0 0 0 10.5,17.5 L13,15 M15,13 L17.5,10.5 A3,3 0 0 0 13.5,6.5 L11,9",
        ["pause"] = "M8,5 V19 M16,5 V19",
        ["play"] = "M8,5 L19,12 L8,19 Z",
        ["stop"] = "M7,7 H17 V17 H7 Z",
        ["check"] = "M5,12.5 L10,17.5 L19,7",
        ["clipboard"] = "M9,4 H15 V7 H9 Z M7,5.5 H9 M15,5.5 H17 V20 H7 V5.5 M10,11 H14 M10,14 H14",
        ["spinner"] = "M12,3 A9,9 0 0 1 21,12",
        ["warn"] = "M12,4 L21,20 H3 Z M12,10 V14 M12,17 V17.2",
        ["pencil"] = "M4,20 L5,15 L15.5,4.5 L19.5,8.5 L9,19 Z M13.5,6.5 L17.5,10.5",
        ["marker"] = "M5,19 H11 M14,4 L20,10 L12,18 L8,18 L6,16 L6,14 Z M11,7 L17,13",
        ["eraser"] = "M4,16 L12,6 L19,12 L13,19 H8 Z M10,9.5 L16.5,15",
        ["undo"] = "M9,8 L5,12 L9,16 M5,12 H15 A4,4 0 0 1 15,20 H11",
        ["trash"] = "M5,7 H19 M10,7 V4 H14 V7 M7,7 L8,20 H16 L17,7 M10,11 V16 M14,11 V16",
        ["eyedropper"] = "M15,4 L20,9 M17.5,6.5 L9,15 V18 H12 L20.5,9.5 M9,18 L6,21",
        ["lens"] = "M4,8 V5 A1,1 0 0 1 5,4 H8 M16,4 H19 A1,1 0 0 1 20,5 V8 M20,16 V19 A1,1 0 0 1 19,20 H16 M8,20 H5 A1,1 0 0 1 4,19 V16 M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5",
        ["font"] = "M5,19 L11,5 H13 L19,19 M7.5,14 H16.5",
        ["calc"] = "M6,3 H18 V21 H6 Z M9,7 H15 V10 H9 Z M9,14 H9.1 M12,14 H12.1 M15,14 H15.1 M9,17.5 H9.1 M12,17.5 H12.1 M15,17.5 H15.1",
        ["mic"] = "M12,3 A3,3 0 0 1 15,6 V12 A3,3 0 0 1 9,12 V6 A3,3 0 0 1 12,3 Z M6,11 A6,6 0 0 0 18,11 M12,17 V21 M9,21 H15",
        ["qr"] = "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H16 M18,14 H20 M14,17 H17 M19,17 V20 M14,20 H16",
        ["coffee"] = "M5,9 H17 V15 A4,4 0 0 1 13,19 H9 A4,4 0 0 1 5,15 Z M17,10 H19 A2,2 0 0 1 19,14 H17 M9,3 V6 M13,3 V6",
        ["stopwatch"] = "M12,8 A7,7 0 1 0 12,22 A7,7 0 1 0 12,8 Z M12,11.5 V15 L14.5,16.5 M10,3 H14 M12,3 V8 M18.5,6 L17,7.5",
        ["flag"] = "M6,21 V4 M6,5 H18 L15.5,9 L18,13 H6",
        ["backspace"] = "M9,5 H20 V19 H9 L3,12 Z M12.5,9.5 L16.5,14.5 M16.5,9.5 L12.5,14.5",
        ["tray"] = "M3,13 L6,5 H18 L21,13 V19 H3 Z M3,13 H8 L9.5,15.5 H14.5 L16,13 H21",
    };

    private static readonly Dictionary<string, Geometry> Cache = new();

    private static Geometry Get(string key)
    {
        if (Cache.TryGetValue(key, out var g)) return g;
        string d = Data.TryGetValue(key, out var s) ? s : Data["file"];
        g = Geometry.Parse(d);
        g.Freeze();
        Cache[key] = g;
        return g;
    }

    /// <summary>An icon of the given size. stroke = null: it follows the island text color (live theme changes included).</summary>
    public static FrameworkElement Make(string key, double size, Brush? stroke = null, double thickness = 1.7)
    {
        var p = new Path
        {
            Data = Get(key),
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = null
        };
        if (stroke != null) p.Stroke = stroke;
        else p.SetResourceReference(Shape.StrokeProperty, "TextBrush");
        return new Viewbox { Width = size, Height = size, Child = p, IsHitTestVisible = false };
    }
}
