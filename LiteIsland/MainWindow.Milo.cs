using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LiteIsland;

// Milo: a friendly voice assistant (Gemini, the user's own API key). The heavy work runs in a helper process (MiloHost).
// It starts only when you say its name (optional wake word) or press the shortcut. While it listens or talks the island
// shows two soft eyes. There is no page for it.
public partial class MainWindow
{
    private const int MiloHotId = 0x4C30 + 200;
    private readonly MiloHost _milo = new();
    private bool _miloHot, _miloWired;
    private string _miloHotKey = "";
    private DispatcherTimer? _remTimer;
    private bool _eyesOn;
    private string _eyesMode = "";

    // "Ctrl+Alt+Space", "F4", "Win+Shift+M" -> RegisterHotKey values
    private static bool ParseHotkey(string text, out uint mod, out uint vk)
    {
        mod = 0x4000; vk = 0; // no repeat
        foreach (string raw in (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string k = raw.ToLowerInvariant();
            if (k is "ctrl" or "control") mod |= 0x2;
            else if (k == "alt") mod |= 0x1;
            else if (k == "shift") mod |= 0x4;
            else if (k is "win" or "windows") mod |= 0x8;
            else if (k == "space") vk = 0x20;
            else if (k.Length == 1 && k[0] >= 'a' && k[0] <= 'z') vk = (uint)(k[0] - 'a' + 0x41);
            else if (k.Length == 1 && k[0] >= '0' && k[0] <= '9') vk = k[0];
            else if (k.Length >= 2 && k[0] == 'f' && int.TryParse(k.AsSpan(1), out int f) && f >= 1 && f <= 12) vk = (uint)(0x6F + f);
            else return false;
        }
        return vk != 0;
    }

    // ---------- start / settings ----------
    private void ApplyMilo()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            string want = _s.MiloEnabled ? (_s.MiloHotkey ?? "") : "";
            if (_miloHot && want != _miloHotKey)
            {
                UnregisterHotKey(hwnd, MiloHotId);
                _miloHot = false;
            }
            if (!_miloHot && want.Length > 0 && ParseHotkey(want, out uint mod, out uint vk))
            {
                _miloHot = RegisterHotKey(hwnd, MiloHotId, mod, vk);
                _miloHotKey = want;
                if (!_miloHot) MiloLog.W("shortcut '" + want + "' could not be registered (another app uses it)");
            }
        }

        if (!_miloWired)
        {
            _miloWired = true;
            _milo.Event += (k, v) => Dispatcher.BeginInvoke(new Action(() => OnMiloEvent(k, v)));
        }

        if (_remTimer == null)
        {
            _remTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _remTimer.Tick += (_, _) => CheckReminders();
            _remTimer.Start();
        }

        if (!_s.MiloEnabled) { if (_milo.Running) _milo.Quit(); HideEyes(); }
        else if (_s.MiloWake && MiloKey.Has && !_milo.Running) _milo.Start(_s);
    }

    /// <summary>Settings changed (name, voice, wake word...): restart the helper so it uses them.</summary>
    internal void RestartMilo()
    {
        if (_milo.Running) _milo.Quit();
        ApplyMilo();
    }

    private void DisposeMilo()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (_miloHot) UnregisterHotKey(hwnd, MiloHotId);
            _miloHot = false;
            _remTimer?.Stop();
            _milo.Quit();
        }
        catch { }
    }

    private long _miloLastAct;

    private void MiloActivate(string source)
    {
        long now = Environment.TickCount64;
        if (now - _miloLastAct < 900) return; // a double press must not start and stop it again at once
        _miloLastAct = now;
        MiloLog.W("activate from " + source + " (state " + _milo.State + ")");
        if (!_s.MiloEnabled) { FlashOrHud("Turn on Milo in Settings"); return; }
        if (!MiloKey.Has) { FlashOrHud("Add your Gemini API key in Settings"); return; }
        if (_milo.State is "listening" or "speaking" or "connecting") { _milo.Send("STOP"); return; }
        if (!_milo.Running) _milo.Start(_s);
        _milo.Send("ACT");
    }

    private void FlashOrHud(string text)
    {
        if (_expanded) FlashDrop(text);
        else if (!_eyesOn) ShowHud("", text, null, null, 3000);
    }

    // ---------- events from the helper ----------
    private void OnMiloEvent(string kind, string val)
    {
        switch (kind)
        {
            case "S":
                UpdateEyes();
                if (val == "idle" && !_s.MiloWake)
                {
                    // nothing to listen for: let the helper go so it uses no RAM
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    t.Tick += (_, _) =>
                    {
                        t.Stop();
                        if (_milo.State == "idle" && !_s.MiloWake) _milo.Quit();
                    };
                    t.Start();
                }
                break;
            case "N":
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                FlashOrHud(val.Length > 60 ? val.Substring(0, 60) + "…" : val);
                break;
            case "E":
                FlashOrHud(val.Length > 60 ? val.Substring(0, 60) + "…" : val);
                break;
        }
    }

    // ---------- reminders ----------
    private void CheckReminders()
    {
        try
        {
            if (!File.Exists(MiloPaths.RemindersFile)) return;
            foreach (MiloReminder r in MiloReminders.TakeDue(DateTime.Now))
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                if (_expanded) FlashDrop("Reminder: " + r.Text);
                else { HideEyes(); ShowHud("", "Reminder", null, r.Text, 9000); }
                if (!string.IsNullOrEmpty(r.Url))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Url) { UseShellExecute = true })?.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex) { MiloLog.W("reminders: " + ex.Message); }
    }

    // ---------- the eyes ----------
    // Two soft eyes replace the collapsed content while Milo is connecting, listening or speaking.
    // The animations run only while the eyes are visible, so they cost nothing the rest of the time.
    private void UpdateEyes()
    {
        string st = _milo.State;
        bool active = _s.MiloEnabled && st is "connecting" or "listening" or "speaking";
        if (!active || _expanded || _hudActive) { HideEyes(); return; }

        string mode = st == "speaking" ? "speak" : st == "connecting" ? "wait" : "listen";
        if (_eyesOn && _eyesMode == mode) return;
        _eyesMode = mode;

        if (!_eyesOn)
        {
            _eyesOn = true;
            Eyes.Visibility = Visibility.Visible;
            // the time / weather must not show through: hide them for real (an animation elsewhere cannot bring them back)
            Compact.BeginAnimation(OpacityProperty, null);
            Compact.Opacity = 0;
            Compact.Visibility = Visibility.Hidden;
            Eyes.BeginAnimation(OpacityProperty, Da(1, 220, CubicOut()));
        }
        PlayEyes(mode);
        StartEyeFollow();
    }

    // The eyes look at the mouse cursor wherever it is on screen (only runs while the eyes are visible).
    private DispatcherTimer? _eyeFollow;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenNative.POINT p);

    private void StartEyeFollow()
    {
        if (_eyeFollow != null) return;
        _eyeFollow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _eyeFollow.Tick += (_, _) =>
        {
            try
            {
                if (!_eyesOn || !EyesRow.IsVisible) return;
                if (!GetCursorPos(out var c)) return;
                var src = PresentationSource.FromVisual(EyesRow);
                if (src?.CompositionTarget == null) return;
                var m = src.CompositionTarget.TransformToDevice;
                var ctr = EyesRow.PointToScreen(new Point(EyesRow.ActualWidth / 2, EyesRow.ActualHeight / 2));
                double dx = (c.X - ctr.X) / m.M11, dy = (c.Y - ctr.Y) / m.M22;
                double len = Math.Sqrt(dx * dx + dy * dy);
                double k = len < 1 ? 0 : Math.Min(1, len / 260);   // far away = full look, close = gentle
                double tx = len < 1 ? 0 : dx / len * 4.5 * k, ty = len < 1 ? 0 : dy / len * 3.2 * k;
                // ease toward the target so the movement is smooth, not jumpy
                EyesShift.X += (tx - EyesShift.X) * 0.28;
                EyesShift.Y += (ty - EyesShift.Y) * 0.28;
            }
            catch { }
        };
        _eyeFollow.Start();
    }

    private void StopEyeFollow()
    {
        _eyeFollow?.Stop();
        _eyeFollow = null;
        EyesShift.X = 0; EyesShift.Y = 0;
    }

    private void PlayEyes(string mode)
    {
        StopEyeAnims();
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };

        if (mode == "wait")
        {
            // dim, slowly breathing
            var a = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine };
            EyesRow.BeginAnimation(OpacityProperty, a);
            return;
        }

        EyesRow.Opacity = 1;
        if (mode == "listen")
        {
            // a calm blink every few seconds + a slow look to the side
            var blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(3600) };
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3200))));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3320))));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3460))));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3600))));
            EyeLS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, blink);
            EyeRS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, blink);

            // the pupils follow the cursor (see EyeFollow)
        }
        else
        {
            // talking: the eyes squash and smile to the rhythm of speech
            var talk = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(900) };
            talk.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
            talk.KeyFrames.Add(new EasingDoubleKeyFrame(0.72, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), sine));
            talk.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), sine));
            talk.KeyFrames.Add(new EasingDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(680)), sine));
            talk.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), sine));
            EyeLS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, talk);
            EyeRS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, talk);
            var wide = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(900) };
            wide.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
            wide.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), sine));
            wide.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), sine));
            wide.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(680)), sine));
            wide.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), sine));
            EyeLS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, wide);
            EyeRS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, wide);
        }
    }

    private void StopEyeAnims()
    {
        EyesRow.BeginAnimation(OpacityProperty, null);
        EyeLS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        EyeRS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        EyeLS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        EyeRS.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        EyeLS.ScaleX = EyeLS.ScaleY = EyeRS.ScaleX = EyeRS.ScaleY = 1;
        EyesRow.Opacity = 1;
    }

    private void HideEyes()
    {
        if (!_eyesOn) return;
        _eyesOn = false;
        _eyesMode = "";
        StopEyeFollow();
        StopEyeAnims();
        Eyes.BeginAnimation(OpacityProperty, Da(0, 160, CubicOut()));
        Compact.Visibility = Visibility.Visible;
        if (!_hudActive && !_expanded) Compact.BeginAnimation(OpacityProperty, Da(1, 220, CubicOut(), 60));
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        t.Tick += (_, _) => { t.Stop(); if (!_eyesOn) Eyes.Visibility = Visibility.Collapsed; };
        t.Start();
    }
}
