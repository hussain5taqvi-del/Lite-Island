using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteIsland;

// Control page: volume and brightness on the left, profiles (Do Not Disturb, Work, ...) on the right.
// Scroll over the right side to change the profile, over a slider to change its value.
public partial class MainWindow
{
    private sealed record Prof(string Name, string Glyph, Color Col, bool Toasts, int Vol, int Bright, string Sub);

    private static readonly Prof[] Profiles =
    {
        new("Normal", "", Color.FromRgb(0x0A, 0x84, 0xFF), true, -1, -1, "Notifications on"),
        new("Do Not Disturb", "", Color.FromRgb(0x5E, 0x5C, 0xE6), false, -1, -1, "Notifications off"),
        new("Work", "", Color.FromRgb(0xBF, 0x5A, 0xF2), false, 30, -1, "Quiet, volume 30%"),
        new("Personal", "", Color.FromRgb(0x30, 0xD1, 0x58), true, 60, -1, "Volume 60%"),
        new("Gaming", "", Color.FromRgb(0xFF, 0x45, 0x3A), false, 80, 100, "Quiet, bright, loud")
    };

    private Border? _ctlVolFill, _ctlBrFill;
    private TextBlock? _ctlVolText, _ctlBrText;
    private StackPanel? _ctlList;
    private TextBlock? _ctlSub;
    private long _ctlWheel;
    private const double SlH = 122, SlW = 56;

    private int ProfileIndex() => Math.Max(0, Array.FindIndex(Profiles, p => p.Name == _s.Profile));

    private void BuildCtl()
    {
        if (CtlHost == null) return;
        CtlHost.Children.Clear();
        var root = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // left: two tall sliders
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        left.Children.Add(VSlider("", out _ctlVolFill, out _ctlVolText, v => { SysControls.SetVolume((float)v); }));
        var gap = new Border { Width = 10 };
        left.Children.Add(gap);
        left.Children.Add(VSlider("", out _ctlBrFill, out _ctlBrText, v => { int b = (int)Math.Round(v * 100); Task.Run(() => SysControls.SetBrightness(b)); }));
        root.Children.Add(left);

        // right: profiles
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0), Background = Brushes.Transparent, Tag = "slider" };
        _ctlSub = Txt("", 11, false, true);
        _ctlSub.Margin = new Thickness(6, 4, 0, 0);
        _ctlList = new StackPanel();
        right.Children.Add(_ctlList);
        right.Children.Add(_ctlSub);
        right.MouseWheel += (_, e) =>
        {
            long now = Environment.TickCount64;
            if (now - _ctlWheel < 140) { e.Handled = true; return; }
            _ctlWheel = now;
            int i = Math.Clamp(ProfileIndex() + (e.Delta > 0 ? -1 : 1), 0, Profiles.Length - 1);
            ApplyProfile(Profiles[i].Name);
            e.Handled = true;
        };
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        CtlHost.Children.Add(root);
        RenderProfiles();

        // current values (read off the UI thread)
        Task.Run(() => (SysControls.GetVolume(), SysControls.GetBrightness())).ContinueWith(t =>
            Dispatcher.InvokeAsync(() =>
            {
                var (v, b) = t.Result;
                SetFill(_ctlVolFill, _ctlVolText, v < 0 ? 0 : v);
                SetFill(_ctlBrFill, _ctlBrText, b < 0 ? 0 : b / 100.0);
                if (b < 0 && _ctlBrFill?.Parent is FrameworkElement f) f.Opacity = 0.35;
            }));
    }

    private static void SetFill(Border? fill, TextBlock? txt, double v)
    {
        if (fill == null) return;
        v = Math.Clamp(v, 0, 1);
        fill.Height = SlH * v;
        if (txt != null) txt.Text = (int)Math.Round(v * 100) + "%";
    }

    private UIElement VSlider(string glyph, out Border fill, out TextBlock pct, Action<double> set)
    {
        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var box = new Grid { Width = SlW, Height = SlH, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = "slider", ClipToBounds = false };
        box.Clip = new RectangleGeometry(new Rect(0, 0, SlW, SlH), 22, 22);
        var track = new Border { CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1) };
        track.SetResourceReference(Border.BackgroundProperty, "TileBg");
        track.SetResourceReference(Border.BorderBrushProperty, "TileStroke");
        box.Children.Add(track);
        var f = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 0, IsHitTestVisible = false, Opacity = 0.92 };
        f.SetResourceReference(Border.BackgroundProperty, "FillBrush");
        box.Children.Add(f);
        fill = f;

        var p = Txt("", 11, true, true);
        p.HorizontalAlignment = HorizontalAlignment.Center;
        p.Margin = new Thickness(0, 5, 0, 0);
        pct = p;
        var ic = new TextBlock { Text = glyph, FontFamily = GlyphFont, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        ic.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        bool drag = false;
        void Apply(double y, bool commit)
        {
            double v = Math.Clamp(1 - y / SlH, 0, 1);
            SetFill(f, p, v);
            set(v);
        }
        box.MouseLeftButtonDown += (_, e) => { drag = true; box.CaptureMouse(); Apply(e.GetPosition(box).Y, false); e.Handled = true; };
        box.MouseMove += (_, e) => { if (drag) Apply(e.GetPosition(box).Y, false); };
        box.MouseLeftButtonUp += (_, e) => { if (!drag) return; drag = false; box.ReleaseMouseCapture(); e.Handled = true; };
        box.MouseWheel += (_, e) =>
        {
            double cur = SlH > 0 ? f.Height / SlH : 0;
            double v = Math.Clamp(cur + (e.Delta > 0 ? 0.05 : -0.05), 0, 1);
            SetFill(f, p, v);
            set(v);
            e.Handled = true;
        };
        col.Children.Add(box);
        col.Children.Add(p);
        col.Children.Add(ic);
        return col;
    }

    private void RenderProfiles()
    {
        if (_ctlList == null) return;
        _ctlList.Children.Clear();
        int sel = ProfileIndex();
        for (int i = 0; i < Profiles.Length; i++)
        {
            Prof pr = Profiles[i];
            bool on = i == sel;
            var row = new Border { Height = 27, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent };
            if (on)
            {
                row.SetResourceReference(Border.BackgroundProperty, "TileBg");
                row.SetResourceReference(Border.BorderBrushProperty, "TileStroke");
            }
            var g = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Border { Width = 21, Height = 21, CornerRadius = new CornerRadius(11), Margin = new Thickness(3, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(pr.Col), Opacity = on ? 1 : 0.55 };
            dot.Child = new TextBlock { Text = pr.Glyph, FontFamily = GlyphFont, FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(dot);
            var name = Txt(pr.Name, 12.5, on, !on);
            name.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(name);
            row.Child = g;
            string n = pr.Name;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; ApplyProfile(n); };
            _ctlList.Children.Add(row);
        }
        if (_ctlSub != null) _ctlSub.Text = Profiles[sel].Sub;
    }

    private void ApplyProfile(string name)
    {
        Prof? p = Profiles.FirstOrDefault(x => x.Name == name);
        if (p == null) return;
        _s.Profile = p.Name;
        _s.Save();
        RenderProfiles();
        Task.Run(() =>
        {
            SysControls.SetToasts(p.Toasts);
            if (p.Vol >= 0) SysControls.SetVolume(p.Vol / 100f);
            if (p.Bright >= 0) SysControls.SetBrightness(p.Bright);
        }).ContinueWith(_ => Dispatcher.InvokeAsync(() =>
        {
            if (_expanded && _view == ViewCtl)
            {
                if (p.Vol >= 0) SetFill(_ctlVolFill, _ctlVolText, p.Vol / 100.0);
                if (p.Bright >= 0) SetFill(_ctlBrFill, _ctlBrText, p.Bright / 100.0);
            }
        }));
        if (!_expanded) ShowHud(p.Glyph, p.Name, null, "On", 1800);
    }
}
