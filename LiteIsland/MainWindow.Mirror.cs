using System;
using System.Threading;
using System.Windows;

namespace LiteIsland;

// Camera mirror: opened from the tray menu or from Settings. It runs in its own helper process.
public partial class MainWindow
{
    private bool _mirrorOpen;

    public async void OpenMirror()
    {
        if (_mirrorOpen) return;
        _mirrorOpen = true;
        try { await Proc.RunWorkerAsync("mirror", Array.Empty<string>(), null, CancellationToken.None); }
        catch { ShowHud("\uE7BA", "Could not open the mirror", null, null, 2400); }
        finally { _mirrorOpen = false; }
    }
}
