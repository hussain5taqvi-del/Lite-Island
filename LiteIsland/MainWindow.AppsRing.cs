using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace LiteIsland;

// Hold the apps key (Caps Lock by default): a ring of your favorite apps opens under the cursor. Click an app, or move
// onto it and let go of the key, and it opens. Let go anywhere else and the ring just disappears.
// Right-click an app in the ring to remove it. The "+" slice adds apps. Dropping a file on the island adds it too.
public partial class MainWindow
{
    private RadialWindow? _appsWheel;

    private Color AccentValue()
    {
        try { return (Color)ColorConverter.ConvertFromString(_s.AccentColor); }
        catch { return Color.FromRgb(0x38, 0xBD, 0xF8); }
    }

    private void InitAppsRing()
    {
        if (_sel == null) return;
        _sel.AppsDown += ShowAppsRing;
        _sel.AppsUp += () => _appsWheel?.ReleaseClose();
        _sel.AppsCancel += () => _appsWheel?.CloseOnce();
    }

    private void ShowAppsRing()
    {
        if (_sel != null && !_sel.AppsHeld) return; // let go before the ring could open
        if (_appsWheel != null && !_appsWheel.IsClosing) return; // already open
        if (_wheel != null && !_wheel.IsBusy) _wheel.CloseOnce();
        MemoryGuard.Burst();

        var apps = new List<ActionDef>();
        foreach (string path in _s.FavoriteApps.ToArray())
        {
            string name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\'));
            if (name.Length == 0) name = path;
            apps.Add(new ActionDef("app:" + path, name.Length > 11 ? name.Substring(0, 10) + "…" : name, "file", null, IconHelper.Get(path)));
        }
        apps.Add(new ActionDef("addapp", "Add app", "plus"));

        var pages = new List<List<ActionDef>>();
        for (int i = 0; i < apps.Count; i += RingPlan.PerPage) pages.Add(apps.Skip(i).Take(RingPlan.PerPage).ToList());

        var w = new RadialWindow(pages, "Apps", null, _s.Theme == "Light", AccentValue(),
            RunAppAction, null, null, true, "Let go on an app", RemoveApp, _s.Glass, _s.GlassOpacity, () => _sel != null && _sel.AppsHeld);
        _appsWheel = w;
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_appsWheel, w)) _appsWheel = null;
            _trimTimer.Stop();
            _trimTimer.Start();
        };
        w.Show();
    }

    private RingResult? RunAppAction(ActionDef a)
    {
        if (a.Id == "addapp")
        {
            Dispatcher.BeginInvoke(new Action(AddAppsDialog));
            return null;
        }
        if (a.Id.StartsWith("app:", StringComparison.Ordinal))
        {
            string path = a.Id.Substring(4);
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" }); }
            catch { ShowHud("", "Could not open " + Short(a.Label, 20), null, null, 2400); }
        }
        return null;
    }

    private string? RemoveApp(ActionDef a)
    {
        string path = a.Id.Substring(4);
        _s.FavoriteApps.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        _s.Save();
        return "Removed " + a.Label;
    }

    private void AddAppsDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add favorite apps",
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.bat;*.url|All files|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        int before = _s.FavoriteApps.Count;
        foreach (string f in dlg.FileNames) AddUnique(_s.FavoriteApps, f, 40);
        _s.Save();
        int added = _s.FavoriteApps.Count - before;
        if (added > 0) ShowHud("", "Added " + added + (added == 1 ? " app" : " apps"), null, null, 2000);
    }
}
