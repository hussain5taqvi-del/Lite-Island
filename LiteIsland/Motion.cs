using System;
using System.Diagnostics;
using System.Windows.Media;

namespace LiteIsland;

/// <summary>
/// A real spring (mass-spring-damper) for one number. Unlike a timed animation it keeps its speed when the target
/// changes in the middle of a move, so opening and closing the island quickly never jerks or restarts.
/// </summary>
public sealed class Spring
{
    private const double Step = 1.0 / 240;     // fixed small step: stable at any frame rate
    private const double RestDistance = 0.12;  // pixels
    private const double RestSpeed = 4.0;      // pixels per second

    public double Value, Velocity, Target;
    public double Stiffness, Damping;

    public Spring(double value, double stiffness = 300, double damping = 26)
    {
        Value = Target = value;
        Stiffness = stiffness;
        Damping = damping;
    }

    /// <summary>damping ratio 1 = no bounce, below 1 = bounce (0.6 is lively).</summary>
    public void Tune(double stiffness, double ratio)
    {
        Stiffness = stiffness;
        Damping = 2 * ratio * Math.Sqrt(stiffness);
    }

    public void Snap(double v) { Value = Target = v; Velocity = 0; }

    /// <summary>Moves the spring forward. Returns true while it is still moving.</summary>
    public bool Advance(double dt)
    {
        while (dt > 0)
        {
            double h = Math.Min(Step, dt);
            Velocity += (-Stiffness * (Value - Target) - Damping * Velocity) * h;
            Value += Velocity * h;
            dt -= h;
        }
        if (Math.Abs(Value - Target) > RestDistance || Math.Abs(Velocity) > RestSpeed) return true;
        Snap(Target);
        return false;
    }
}

/// <summary>Runs a callback on every screen frame until it says it is done (no cost while nothing moves).</summary>
public sealed class FrameLoop
{
    private readonly Func<double, bool> _advance;
    private long _last;
    public bool Running { get; private set; }

    public FrameLoop(Func<double, bool> advance) { _advance = advance; }

    public void Start()
    {
        if (Running) return;
        Running = true;
        _last = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _last) / (double)Stopwatch.Frequency;
        if (dt <= 0) return;
        _last = now;
        bool more;
        try { more = _advance(Math.Min(dt, 0.05)); }
        catch { more = false; }
        if (!more) Stop();
    }
}
