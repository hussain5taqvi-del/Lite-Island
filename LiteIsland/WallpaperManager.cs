using System;

namespace LiteIsland;

/// <summary>Sets a still picture as the normal Windows wallpaper (uses no RAM in this app).</summary>
public static class WallpaperManager
{
    public static void Apply(string path, Action<string>? onError)
    {
        if (WallpaperService.IsImage(path)) WallpaperService.SetStatic(path);
        else onError?.Invoke("This file type cannot be used as a wallpaper.");
    }
}
