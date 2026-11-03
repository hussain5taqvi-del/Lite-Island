using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteIsland;

// Music page: a small slider that changes the volume of the app that plays the song (Spotify, Chrome...), not the whole PC.
// The audio sessions are read only while the music page is shown, and released again at once.
public partial class MainWindow
{
    private List<MixApp> _avApps = new();
    private Border? _avFill;
    private Grid? _avTrack;
    private TextBlock? _avIcon;
    private bool _avDrag;
    private FrameworkElement? _avBox;

    private void BuildAppVol()
    {
        if (AppVolHost == null || _avBox != null) return;
        var box = new Grid { Width = 86, Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Volume of this app" };
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _avIcon = new TextBlock { Text = "", FontFamily = GlyphFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
        _avIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        box.Children.Add(_avIcon);

        _avTrack = new Grid { Height = 18, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Tag = "slider" };
        var bar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var tr = new Border { CornerRadius = new CornerRadius(2) };
        tr.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        _avFill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _avFill.SetResourceReference(Border.BackgroundProperty, "FillBrush");
        bar.Children.Add(tr);
        bar.Children.Add(_avFill);
        _avTrack.Children.Add(bar);
        Grid.SetColumn(_avTrack, 1);
        box.Children.Add(_avTrack);

        _avTrack.MouseLeftButtonDown += (_, e) => { if (_avApps.Count == 0) return; _avDrag = true; _avTrack!.CaptureMouse(); AvSet(e); e.Handled = true; };
        _avTrack.MouseMove += (_, e) => { if (_avDrag) AvSet(e); };
        _avTrack.MouseLeftButtonUp += (_, e) => { if (!_avDrag) return; _avDrag = false; _avTrack!.ReleaseMouseCapture(); AvSet(e); e.Handled = true; };
        _avTrack.MouseWheel += (_, e) =>
        {
            if (_avApps.Count == 0) return;
            double v = Math.Clamp((_avApps[0].Volume) + (e.Delta > 0 ? 0.05 : -0.05), 0, 1);
            foreach (var a in _avApps) a.SetVolume((float)v);
            AvShow(v);
            e.Handled = true;
        };
        AppVolHost.Children.Add(box);
        _avBox = box;
    }

    private void AvSet(MouseEventArgs e)
    {
        if (_avTrack == null || _avTrack.ActualWidth < 4) return;
        double v = Math.Clamp(e.GetPosition(_avTrack).X / _avTrack.ActualWidth, 0, 1);
        foreach (var a in _avApps) a.SetVolume((float)v);
        AvShow(v);
    }

    private void AvShow(double v)
    {
        if (_avFill != null && _avTrack != null) _avFill.Width = Math.Max(0, _avTrack.ActualWidth * v);
        if (_avIcon != null) _avIcon.Text = v < 0.01 ? "" : v < 0.4 ? "" : "";
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
        if (dot > 0 && dot < n.Length - 1 && n.Contains('.')) n = n.Substring(dot + 1);
        return n.ToLowerInvariant();
    }

    /// <summary>Finds the audio sessions of the app that plays the current song and shows the slider.</summary>
    private void RefreshAppVol()
    {
        try
        {
            BuildAppVol();
            ReleaseAppVol();
            if (_avBox == null) return;
            string stem = AvStem(_media.SourceId);
            if (stem.Length == 0) { _avBox.Visibility = Visibility.Collapsed; return; }

            var all = AudioMixer.Query();
            var match = new List<MixApp>();
            foreach (MixApp a in all)
            {
                string exe = a.Path != null ? Path.GetFileNameWithoutExtension(a.Path).ToLowerInvariant() : "";
                string nm = (a.Name ?? "").ToLowerInvariant();
                bool hit = (exe.Length > 1 && (exe == stem || stem.Contains(exe) || exe.Contains(stem))) ||
                           (nm.Length > 2 && (nm.Contains(stem) || stem.Contains(nm)));
                if (hit) match.Add(a); else a.Release();
            }
            _avApps = match;
            _avBox.Visibility = match.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (match.Count > 0)
            {
                _avBox.UpdateLayout();
                AvShow(match.Max(m => m.Volume));
            }
        }
        catch (Exception ex) { Debug.WriteLine("appvol: " + ex.Message); }
    }

    private void ReleaseAppVol()
    {
        foreach (var a in _avApps) { try { a.Release(); } catch { } }
        _avApps = new List<MixApp>();
    }
}
