using System;

namespace LiteIsland;

/// <summary>Sets a still picture as the normal Windows wallpaper (uses no RAM in this app).</summary>
public static class WallpaperManager
{
    /// <summary>Raised after a still picture was set (the live wallpaper has to step aside).</summary>
    public static Action? StaticApplied;

    public static void Apply(string path, Action<string>? onError)
    {
        if (WallpaperService.IsImage(path)) { WallpaperService.SetStatic(path); StaticApplied?.Invoke(); }
        else onError?.Invoke("This file type cannot be used as a wallpaper.");
    }
}
