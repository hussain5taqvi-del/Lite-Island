using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LiteIsland;

// Activity bar: for a moment the collapsed island widens and shows the volume, the brightness
// or a "Bluetooth device connected" message, then it goes back by itself.
public partial class MainWindow
{
    private const double HudBarWidth = 96;
    private const string GlyphVolume = "\uE767";
    private const string GlyphMute = "\uE74F";
    private const string GlyphBrightness = "\uE706";
    private const string GlyphBluetooth = "\uE702";

    private readonly VolumeWatcher _volume = new();
    private readonly BrightnessWatcher _brightness = new();
    private readonly BluetoothWatcher _btWatcher = new();
    private readonly DispatcherTimer _hudTimer = new();
    private bool _hudActive;
    private readonly Spring _hudSpring = new(0);
    private FrameLoop? _hudLoop;

    private void InitHud()
    {
        _hudTimer.Tick += (_, _) =>
        {
            _hudTimer.Stop();
            HideHud();
        };

        // These events arrive on background threads: hand them to the UI thread
        _volume.Changed += (vol, muted) => Dispatcher.InvokeAsync(() => OnVolume(vol, muted), DispatcherPriority.Send);
        _brightness.Changed += value => Dispatcher.InvokeAsync(() => OnBrightness(value), DispatcherPriority.Send);
        _btWatcher.Connected += (name, battery) => Dispatcher.InvokeAsync(() => OnBluetooth(name, battery));

        ApplyHudSettings();
    }

    /// <summary>Starts or stops the watchers according to the settings (a switched off watcher costs nothing).</summary>
    public void ApplyHudSettings()
    {
        _volume.SetEnabled(_s.HudVolume);
        _brightness.SetEnabled(_s.HudBrightness);
        _btWatcher.SetEnabled(_s.HudBluetooth);
        StartEdgeWatch();
    }

    private void DisposeHud()
    {
        _hudTimer.Stop();
        _hudLoop?.Stop();
        _edgeTimer?.Stop();
        _volume.Dispose();
        _brightness.Dispose();
        _btWatcher.Dispose();
        try { _lobeL?.Close(); _lobeR?.Close(); } catch { }
    }

    private void OnVolume(float volume, bool muted)
    {
        if (!_s.HudVolume) return;
        double v = muted ? 0 : volume;
        LobeLevel(v <= 0.001 ? GlyphMute : v < 0.4 ? "\uE992" : GlyphVolume, v, true);
    }

    private void OnBrightness(int percent)
    {
        if (!_s.HudBrightness) return;
        LobeLevel(GlyphBrightness, Math.Clamp(percent, 0, 100) / 100.0, false);
    }

    // Volume / brightness: a tab slides out of the LEFT edge of the screen (not out of the island)
    private EdgeLobe? _lobeL, _lobeR;
    private bool _lobeIsVol = true;

    private void LobeLevel(string glyph, double v, bool vol)
    {
        if (_eyesOn) return;
        if (_lobeL == null)
        {
            _lobeL = new EdgeLobe(true, GlyphFont, 0);
            _lobeL.Set = x =>
            {
                if (_lobeIsVol) SysControls.SetVolume((float)x);
                else SysControls.RequestBrightness((int)Math.Round(x * 100));
            };
        }
        _lobeIsVol = vol;
        _lobeL.Rescale(_s.LobeWidth, _s.LobeHeight);
        _lobeL.ShowLevel(glyph, v, 1600);
    }

    private void LobeModes(int sel)
    {
        if (_lobeR == null)
        {
            _lobeR = new EdgeLobe(false, GlyphFont, Profiles.Length);
            _lobeR.Pick = i => ApplyProfile(Profiles[Math.Clamp(i, 0, Profiles.Length - 1)].Name);
        }
        var list = new System.Collections.Generic.List<(string, Color, string)>();
        foreach (var p in Profiles) list.Add((p.Glyph, p.Col, p.Name));
        _lobeR.Rescale(_s.LobeWidth, _s.LobeHeight);
        _lobeR.ShowModes(list, sel, 2200);
    }

    /// <summary>Settings changed: resize the tabs and, when a size slider moved, show both so the size can be judged.</summary>
    public void ApplyLobeSettings(bool preview)
    {
        _lobeL?.Rescale(_s.LobeWidth, _s.LobeHeight);
        _lobeR?.Rescale(_s.LobeWidth, _s.LobeHeight);
        StartEdgeWatch();
        if (!preview) return;
        System.Threading.Tasks.Task.Run(() => SysControls.GetVolume()).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            double v = t.Result < 0 ? 0.5 : t.Result;
            LobeLevel(v <= 0.001 ? GlyphMute : v < 0.4 ? "\uE992" : GlyphVolume, v, true);
            LobeModes(ProfileIndex());
        }));
    }

    // ---------- right edge: moving the cursor to the right edge of the screen opens the focus tab ----------
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct EdgePt { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool EdgeCursor(out EdgePt p);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")] private static extern short EdgeKey(int vk);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetSystemMetrics")] private static extern int EdgeMetric(int i);

    private DispatcherTimer? _edgeTimer;
    private int _edgeHits;
    private bool _edgeArmed = true;

    private void StartEdgeWatch()
    {
        if (!_s.LobeEdge)
        {
            _edgeTimer?.Stop();
            return;
        }
        if (_edgeTimer == null)
        {
            _edgeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(110) };
            _edgeTimer.Tick += (_, _) => EdgeTick();
        }
        if (!_edgeTimer.IsEnabled) _edgeTimer.Start();
    }

    private void EdgeTick()
    {
        if (!EdgeCursor(out EdgePt p)) return;
        int w = EdgeMetric(0);
        bool atEdge = p.X >= w - 1 && p.X <= w && p.Y >= 0 && p.Y < EdgeMetric(1);
        if (!atEdge) { _edgeHits = 0; _edgeArmed = true; return; }
        if (!_edgeArmed) return;
        if ((EdgeKey(0x01) & 0x8000) != 0) { _edgeHits = 0; return; } // dragging something to the edge: stay out of the way
        if (++_edgeHits < 2) return;                                    // the cursor has to rest there for a moment
        _edgeHits = 0;
        _edgeArmed = false;                                             // opens once per visit, move away and back to open again
        if (!_eyesOn) LobeModes(ProfileIndex());
    }

    private void OnBluetooth(string name, int? battery)
    {
        if (!_s.HudBluetooth) return;
        ShowHud(GlyphBluetooth, name, null, battery != null ? battery + "%" : "Connected", 3200);
    }

    // Spring used by the activity bar (Snappy style stays crisp and cheap)
    private IEasingFunction HudSpring(double damping) =>
        _s.AnimStyle == "Snappy" ? QuinticOut() : new SpringEase { Damping = damping };

    private void ShowHud(string glyph, string label, double? bar, string? rightText, int ms)
    {
        if (_expanded || _eyesOn) return; // do not disturb while the island is open or Milo is listening

        HudIcon.Text = glyph;
        HudLabel.Text = label;

        bool hasBar = bar != null;
        HudBarHost.Visibility = hasBar ? Visibility.Visible : Visibility.Collapsed;
        HudValue.Visibility = !hasBar && rightText != null ? Visibility.Visible : Visibility.Collapsed;
        if (!hasBar && rightText != null) HudValue.Text = rightText;

        // The bar fill follows the value with a critically damped spring: it keeps its speed when new values
        // arrive (the system sends them in steps), so the slider glides instead of stepping
        if (hasBar)
        {
            double target = HudBarWidth * Math.Clamp(bar!.Value, 0, 1);
            if (!_hudActive)
            {
                _hudLoop?.Stop();
                _hudSpring.Snap(target);
                HudFill.Width = target;
            }
            else
            {
                _hudSpring.Tune(260, 1.0);
                _hudSpring.Target = target;
                _hudLoop ??= new FrameLoop(dt =>
                {
                    bool more = _hudSpring.Advance(dt);
                    HudFill.Width = Math.Clamp(_hudSpring.Value, 0, HudBarWidth);
                    return more;
                });
                _hudLoop.Start();
            }
        }

        if (!_hudActive)
        {
            _hudActive = true;
            double w = Math.Max(_s.CollapsedWidth, HudMinWidth);
            Hud.Width = w;
            Hud.Visibility = Visibility.Visible;

            // Old content leaves quickly and shrinks a little...
            Compact.BeginAnimation(OpacityProperty, Da(0, 120, CubicOut()));
            CompactScale.BeginAnimation(ScaleTransform.ScaleXProperty, Da(0.94, 160, CubicOut()));
            CompactScale.BeginAnimation(ScaleTransform.ScaleYProperty, Da(0.94, 160, CubicOut()));

            // ...the new content grows in with a soft spring, a moment later
            Hud.BeginAnimation(OpacityProperty, Da(1, 240, CubicOut(), 80));
            var hs = new DoubleAnimation(0.9, 1, Dur(520)) { BeginTime = Dur(70), EasingFunction = HudSpring(0.78) };
            HudScale.BeginAnimation(ScaleTransform.ScaleXProperty, hs);
            HudScale.BeginAnimation(ScaleTransform.ScaleYProperty, hs);

            // The island itself widens, gets a little taller and rounder (one spring for all three)
            StopSizeSprings();
            Island.BeginAnimation(WidthProperty, Da(w, 560, HudSpring(0.72)));
            Island.BeginAnimation(HeightProperty, Da(_s.CollapsedHeight + 5, 520, HudSpring(0.8)));
            BeginAnimation(BottomRadiusProperty, Da(CollapsedR + 3, 520, HudSpring(0.8)));
        }

        _hudTimer.Stop();
        _hudTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _hudTimer.Start();
    }

    private void HideHud()
    {
        if (!_hudActive) return;
        _hudActive = false;
        if (_expanded) return;

        // Activity content fades out first...
        Hud.BeginAnimation(OpacityProperty, Da(0, 170, CubicOut()));
        var hs = Da(0.94, 240, CubicOut());
        HudScale.BeginAnimation(ScaleTransform.ScaleXProperty, hs);
        HudScale.BeginAnimation(ScaleTransform.ScaleYProperty, hs);

        // ...the island glides back and settles softly (long, calm spring, no wobble)...
        var back = Da(_s.CollapsedWidth, 640, HudSpring(0.88));
        back.Completed += (_, _) =>
        {
            if (!_hudActive) Hud.Visibility = Visibility.Collapsed;
        };
        StopSizeSprings();
        Island.BeginAnimation(WidthProperty, back);
        Island.BeginAnimation(HeightProperty, Da(_s.CollapsedHeight, 560, HudSpring(0.9)));
        BeginAnimation(BottomRadiusProperty, Da(CollapsedR, 560, HudSpring(0.9)));

        // ...and the normal content appears while it is still settling, growing from 92% to 100%
        Compact.BeginAnimation(OpacityProperty, Da(1, 320, CubicOut(), 190));
        var cs = new DoubleAnimation(0.92, 1, Dur(560)) { BeginTime = Dur(170), EasingFunction = HudSpring(0.82) };
        CompactScale.BeginAnimation(ScaleTransform.ScaleXProperty, cs);
        CompactScale.BeginAnimation(ScaleTransform.ScaleYProperty, cs);
    }

    // The island is being opened: drop the activity bar at once
    private void CancelHud()
    {
        _hudActive = false;
        _hudTimer.Stop();
        Hud.BeginAnimation(OpacityProperty, null);
        Hud.Opacity = 0;
        Hud.Visibility = Visibility.Collapsed;
        CompactScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactScale.ScaleX = CompactScale.ScaleY = 1;
    }
}
