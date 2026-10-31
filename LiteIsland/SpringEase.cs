using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace LiteIsland;

/// <summary>
/// Spring easing, similar to Apple's spring animations: fast start, a very small
/// overshoot, then a soft settle (damping above 0.86 is treated as 0.86). Damping near 1 = almost no bounce, lower = more bounce.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingProperty =
        DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase),
            new PropertyMetadata(0.75));

    public double Damping
    {
        get => (double)GetValue(DampingProperty);
        set => SetValue(DampingProperty, value);
    }

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        // Damped spring that starts WITH speed (like Apple's springs): it leaves at once, no slow start,
        // and it is finished long before the time is up (no slow crawl at the end that looks like a glitch).
        double z = Math.Clamp(Damping, 0.3, 0.86);
        const double k = 8.0;                      // how fast the motion dies out
        const double v0 = 4.5;                     // starting speed
        double wd = k * Math.Sqrt(1 - z * z) / z;  // oscillation speed
        return 1 - Math.Exp(-k * t) * (Math.Cos(wd * t) + ((k - v0) / wd) * Math.Sin(wd * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
