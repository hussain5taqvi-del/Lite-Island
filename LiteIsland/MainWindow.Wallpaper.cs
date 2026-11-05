namespace LiteIsland;

public partial class MainWindow
{
    private readonly WallpaperHost _wall = new();

    internal string WallpaperStatus => _wall.Status;
    internal event System.Action? WallpaperStatusChanged
    {
        add { _wall.StatusChanged += value; }
        remove { _wall.StatusChanged -= value; }
    }

    /// <summary>Starts, restarts or stops the live wallpaper helper to match the settings.</summary>
    internal void ApplyWallpaper() => _wall.Apply(_s.WallpaperOn, _s.WallpaperPath, _s.WallpaperPause);
}
