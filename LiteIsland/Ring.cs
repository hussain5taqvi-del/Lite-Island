using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LiteIsland;

/// <summary>Activity-ring progress for an Ellipse: Ring.Value (0..1) sets the visible arc. Animatable.</summary>
public static class Ring
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(Ring), new PropertyMetadata(0.0, OnValue));

    public static double GetValue(DependencyObject o) => (double)o.GetValue(ValueProperty);
    public static void SetValue(DependencyObject o, double v) => o.SetValue(ValueProperty, v);

    private static void OnValue(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Ellipse el) return;
        double v = Math.Clamp((double)e.NewValue, 0, 1);
        double t = el.StrokeThickness <= 0 ? 1 : el.StrokeThickness;
        double circ = Math.PI * Math.Max(1, el.Width - t) / t;   // circumference in stroke units
        double len = v * circ - 1;                                // round caps add one unit
        if (len < 0.01)
        {
            el.StrokeDashArray = new DoubleCollection { 0.001, 1000 };
            el.Opacity = v <= 0.004 ? 0 : 1;
        }
        else
        {
            el.StrokeDashArray = new DoubleCollection { len, 1000 };
            el.Opacity = 1;
        }
    }
}
