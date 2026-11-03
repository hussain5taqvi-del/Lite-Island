using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LiteIsland;

// Music page: a black tab slides out of the left side of the island with a tall volume slider for the app that plays
// the song (Spotify, Chrome...), not for the whole PC. The audio sessions are read only while the music page is shown.
public partial class MainWindow
{
    private List<MixApp> _avApps = new();
    private Border? _avFill;
    private Grid? _avBox;
    private TextBlock? _avIcon;
    private bool _avDrag, _avShown;
    private const double AvW = 40, AvH = 92;

    private void BuildAppVol()
    {
        if (VolLobe == null || _avBox != null) return;
        var box = new Grid { Width = AvW, Height = AvH, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = "slider",
                             HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        box.Clip = new RectangleGeometry(new Rect(0, 0, AvW, AvH), 14, 14);
        box.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)) });
        _avFill = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 0, Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)), IsHitTestVisible = false };
        box.Children.Add(_avFill);
        _avIcon = new TextBlock { Text = "\uE767", FontFamily = GlyphFont, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)),
                                  HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 9), IsHitTestVisible = false };
        box.Children.Add(_avIcon);

        box.MouseLeftButtonDown += (_, e) => { if (_avApps.Count == 0) return; _avDrag = true; box.CaptureMouse(); AvSet(e); e.Handled = true; };
        box.MouseMove += (_, e) => { if (_avDrag) AvSet(e); };
        box.MouseLeftButtonUp += (_, e) => { if (!_avDrag) return; _avDrag = false; box.ReleaseMouseCapture(); AvSet(e); e.Handled = true; };
        box.MouseWheel += (_, e) =>
        {
            if (_avApps.Count == 0) return;
            double v = Math.Clamp(_avApps[0].Volume + (e.Delta > 0 ? 0.05 : -0.05), 0, 1);
            foreach (var a in _avApps) a.SetVolume((float)v);
            AvShow(v);
            e.Handled = true;
        };
        VolLobe.Child = box;
        _avBox = box;
    }

    private void AvSet(MouseEventArgs e)
    {
        if (_avBox == null) return;
        double v = Math.Clamp(1 - e.GetPosition(_avBox).Y / AvH, 0, 1);
        foreach (var a in _avApps) a.SetVolume((float)v);
        AvShow(v);
    }

    private void AvShow(double v)
    {
        if (_avFill != null) _avFill.Height = AvH * v;
        if (_avIcon != null) _avIcon.Text = v < 0.01 ? "\uE74F" : v < 0.4 ? "\uE992" : "\uE767";
    }

    private static string AvStem(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        string n = id!;
        int bang = n.IndexOf('!');
        if (bang > 0) n = n.Substring(0, bang);
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
        int us = n.IndexOf('_');
        if (us > 0) n = n.Substring(0, us); // Store apps: Name_publisherid
        int dot = n.LastIndexOf('.');
        if (dot > 0 && dot < n.Length - 1) n = n.Substring(dot + 1);
        return n.ToLowerInvariant();
    }

    private void ShowLobe(bool on)
    {
        if (VolLobe == null || on == _avShown) return;
        _avShown = on;
        if (on)
        {
            VolLobe.Visibility = Visibility.Visible;
            VolLobe.BeginAnimation(OpacityProperty, Da(1, 220, CubicOut()));
            VolLobeShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, Dur(520)) { EasingFunction = HudSpring(0.8) });
        }
        else
        {
            VolLobe.BeginAnimation(OpacityProperty, Da(0, 150, CubicOut()));
            VolLobeShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, Dur(220)) { EasingFunction = CubicOut() });
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            t.Tick += (_, _) => { t.Stop(); if (!_avShown) VolLobe.Visibility = Visibility.Collapsed; };
            t.Start();
        }
    }

    /// <summary>Finds the audio sessions of the app that plays the current song and slides the tab out.</summary>
    private void RefreshAppVol()
    {
        try
        {
            BuildAppVol();
            foreach (var a in _avApps) { try { a.Release(); } catch { } }
            _avApps = new List<MixApp>();
            if (_avBox == null || !_expanded || _view != ViewMedia) { ShowLobe(false); return; }
            string stem = AvStem(_media.SourceId);
            if (stem.Length == 0) { ShowLobe(false); return; }

            var match = new List<MixApp>();
            foreach (MixApp a in AudioMixer.Query())
            {
                string exe = a.Path != null ? Path.GetFileNameWithoutExtension(a.Path).ToLowerInvariant() : "";
                string nm = (a.Name ?? "").ToLowerInvariant();
                bool hit = (exe.Length > 1 && (exe == stem || stem.Contains(exe) || exe.Contains(stem))) ||
                           (nm.Length > 2 && (nm.Contains(stem) || stem.Contains(nm)));
                if (hit) match.Add(a); else a.Release();
            }
            _avApps = match;
            if (match.Count > 0) AvShow(match.Max(m => m.Volume));
            ShowLobe(match.Count > 0);
        }
        catch (Exception ex) { Debug.WriteLine("appvol: " + ex.Message); }
    }

    private void ReleaseAppVol()
    {
        foreach (var a in _avApps) { try { a.Release(); } catch { } }
        _avApps = new List<MixApp>();
        ShowLobe(false);
    }
}
