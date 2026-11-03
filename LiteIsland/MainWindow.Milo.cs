using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LiteIsland;

// Milo page: a friendly voice assistant (Gemini, the user's own API key). The heavy work runs in a helper process (MiloHost).
// Tap the orb, press Ctrl + Alt + Space, or say the name (wake word, optional).
public partial class MainWindow
{
    private const int MiloHotId = 0x4C30 + 200;
    private readonly MiloHost _milo = new();
    private bool _miloHot, _miloWired;
    private DispatcherTimer? _remTimer;
    private Border? _orb;
    private ScaleTransform? _orbScale;
    private TextBlock? _mStatus, _mHeard, _mSaid, _mTool;
    private bool _orbPulsing;

    // ---------- start / settings ----------
    private void ApplyMilo()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            if (_s.MiloEnabled && !_miloHot)
                _miloHot = RegisterHotKey(hwnd, MiloHotId, 0x1 | 0x2 | 0x4000, 0x20); // Ctrl + Alt + Space
            else if (!_s.MiloEnabled && _miloHot)
            {
                UnregisterHotKey(hwnd, MiloHotId);
                _miloHot = false;
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

        if (!_s.MiloEnabled) { if (_milo.Running) _milo.Quit(); }
        else if (_s.MiloWake && MiloKey.Has && !_milo.Running) _milo.Start(_s);

        if (_expanded && _view == ViewMilo) BuildMilo();
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

    private void MiloActivate()
    {
        if (!_s.MiloEnabled) { MiloNote("Turn on Milo in Settings"); return; }
        if (!MiloKey.Has) { MiloNote("Add your Gemini API key in Settings"); return; }
        if (_milo.State is "listening" or "speaking" or "connecting") { _milo.Send("STOP"); return; }
        if (!_milo.Running) _milo.Start(_s);
        _milo.Send("ACT");
    }

    private void MiloNote(string text)
    {
        if (_expanded && _view == ViewMilo && _mStatus != null) _mStatus.Text = text;
        else FlashOrHud(text);
    }

    private void FlashOrHud(string text)
    {
        if (_expanded) FlashDrop(text);
        else ShowHud("", text, null, null, 3000);
    }

    // ---------- events from the helper ----------
    private void OnMiloEvent(string kind, string val)
    {
        switch (kind)
        {
            case "S":
                UpdateMilo();
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
                if (val == "listening" && !_expanded) ShowHud("", string.IsNullOrWhiteSpace(_s.MiloName) ? "Milo" : _s.MiloName, null, "Listening", 2500);
                break;
            case "T":
                if (!_expanded) ShowHud("", val, null, null, 2200);
                UpdateMilo();
                break;
            case "E":
                FlashOrHud(val.Length > 60 ? val.Substring(0, 60) + "…" : val);
                UpdateMilo();
                break;
            default:
                UpdateMilo();
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
                else ShowHud("", "Reminder", null, r.Text, 9000);
                if (!string.IsNullOrEmpty(r.Url))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Url) { UseShellExecute = true })?.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex) { MiloLog.W("reminders: " + ex.Message); }
    }

    // ---------- page ----------
    private void BuildMilo()
    {
        if (MiloPageHost == null) return;
        MiloPageHost.Children.Clear();
        string name = string.IsNullOrWhiteSpace(_s.MiloName) ? "Milo" : _s.MiloName.Trim();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid { Height = 26, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("PageTitle"), VerticalAlignment = VerticalAlignment.Center });
        var pills = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        pills.Children.Add(Pill("Log", OpenMiloLog));
        head.Children.Add(pills);
        root.Children.Add(head);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _orbScale = new ScaleTransform(1, 1);
        _orb = new Border
        {
            Width = 76, Height = 76, CornerRadius = new CornerRadius(38), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _orbScale
        };
        _orb.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var mic = new TextBlock { Text = "", FontFamily = GlyphFont, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mic.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
        _orb.Child = mic;
        _orb.MouseLeftButtonUp += (_, e) => { e.Handled = true; MiloActivate(); };
        AddPress(_orb, 0.92);
        body.Children.Add(_orb);

        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        _mStatus = Txt("", 14, true);
        _mTool = Txt("", 11, false, true);
        _mHeard = Txt("", 11.5, false, true);
        _mSaid = Txt("", 12, false);
        _mHeard.TextWrapping = TextWrapping.Wrap; _mHeard.MaxHeight = 32; _mHeard.TextTrimming = TextTrimming.CharacterEllipsis;
        _mSaid.TextWrapping = TextWrapping.Wrap; _mSaid.MaxHeight = 48; _mSaid.TextTrimming = TextTrimming.CharacterEllipsis;
        _mHeard.Margin = new Thickness(0, 6, 0, 0);
        _mSaid.Margin = new Thickness(0, 4, 0, 0);
        col.Children.Add(_mStatus);
        col.Children.Add(_mTool);
        col.Children.Add(_mHeard);
        col.Children.Add(_mSaid);
        Grid.SetColumn(col, 1);
        body.Children.Add(col);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        MiloPageHost.Children.Add(root);
        UpdateMilo();
    }

    private void UpdateMilo()
    {
        if (_mStatus == null || _orb == null) return;
        string name = string.IsNullOrWhiteSpace(_s.MiloName) ? "Milo" : _s.MiloName.Trim();
        string st = _milo.State;
        string status;
        if (!_s.MiloEnabled) status = "Turn on " + name + " in Settings";
        else if (!MiloKey.Has) status = "Add your Gemini API key in Settings";
        else status = st switch
        {
            "connecting" => "Connecting…",
            "listening" => "Listening…",
            "speaking" => "Speaking…",
            "nokey" => "Add your Gemini API key in Settings",
            _ => _s.MiloWake ? "Say “" + name + "” or tap" : "Tap to talk"
        };
        _mStatus.Text = status;
        _mTool.Text = st is "listening" or "speaking" ? _milo.LastTool : "";
        _mHeard.Text = _milo.LastHeard.Length > 0 ? "You: " + _milo.LastHeard : "";
        _mSaid.Text = _milo.LastSaid.Length > 0 ? name + ": " + _milo.LastSaid : "";
        if (_milo.LastError.Length > 0 && st is "idle" or "off" or "nokey") _mTool.Text = _milo.LastError;
        _orb.Opacity = _s.MiloEnabled ? 1 : 0.4;
        SetOrbPulse(_expanded && _view == ViewMilo && IsVisible && st is "listening" or "speaking");
    }

    private void SetOrbPulse(bool on)
    {
        if (_orbScale == null || on == _orbPulsing) return;
        _orbPulsing = on;
        if (on)
        {
            var a = new DoubleAnimation(1.0, 1.1, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            _orbScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            _orbScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        else
        {
            _orbScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _orbScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _orbScale.ScaleX = 1; _orbScale.ScaleY = 1;
        }
    }

    private void OpenMiloLog()
    {
        try
        {
            Directory.CreateDirectory(MiloPaths.Dir);
            if (!File.Exists(MiloPaths.Log)) File.WriteAllText(MiloPaths.Log, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MiloPaths.Log) { UseShellExecute = true })?.Dispose();
        }
        catch { }
    }
}
