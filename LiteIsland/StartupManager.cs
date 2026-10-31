using System;
using System.IO;
using Microsoft.Win32;

namespace LiteIsland;

/// <summary>
/// Start-with-Windows switch (Registry: HKCU\...\Run). No admin rights needed.
/// - The entry always points to the exe that is running now (a stale entry from an older copy is repaired).
/// - If Windows (Task Manager > Startup apps) has the entry switched off, the box shows it as off.
/// - Windows starts it with "--autostart", so the island waits until the desktop is ready.
/// </summary>
public static class StartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "LiteIsland";
    public const string AutoArg = "--autostart";

    private static string? Command()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;

        // Started with 'dotnet run', the path is dotnet.exe, which is useless for startup
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return null;

        return "\"" + exe + "\" " + AutoArg;
    }

    // Windows can switch an entry off from Task Manager: that is stored here (first byte odd = off)
    private static bool DisabledByWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath);
            if (key?.GetValue(ValueName) is byte[] b && b.Length > 0) return (b[0] & 1) == 1;
        }
        catch { }
        return false;
    }

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) != null && !DisabledByWindows();
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                string? cmd = Command();
                if (cmd == null) return;
                key.SetValue(ValueName, cmd);

                // Remove an old "switched off in Task Manager" mark, otherwise Windows would still skip it
                try
                {
                    using var ap = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true);
                    ap?.DeleteValue(ValueName, false);
                }
                catch { }
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch
        {
            // Registry not accessible: ignore
        }
    }

    /// <summary>Called at every start: if the entry exists but points to another / an old exe, point it here.</summary>
    public static void Repair()
    {
        try
        {
            string? cmd = Command();
            if (cmd == null) return;

            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key?.GetValue(ValueName) is string current && !string.Equals(current, cmd, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, cmd);
        }
        catch { }
    }
}
