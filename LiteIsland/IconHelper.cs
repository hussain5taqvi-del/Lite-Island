using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiteIsland;

/// <summary>
/// Gets the Explorer icon of a file as a small frozen image. Cached, so each icon is read once.
/// For shortcuts (.lnk) the icon of the target is used, so there is no small blue arrow on it.
/// </summary>
public static class IconHelper
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;

        ImageSource? img = null;
        try
        {
            if (File.Exists(path))
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    img = FromShortcut(path);

                img ??= FromAssociated(path);
            }
        }
        catch
        {
            // No icon available: the UI shows a glyph instead
        }

        Cache[path] = img;
        return img;
    }

    // Icon of the shortcut's own icon location, or of its target program
    private static ImageSource? FromShortcut(string lnk)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;

            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string iconLoc = (string?)sc.IconLocation ?? "";
            string target = (string?)sc.TargetPath ?? "";

            // IconLocation looks like "C:\path\app.exe,0" (it can also be empty)
            string iconFile = "";
            int index = 0;
            if (!string.IsNullOrWhiteSpace(iconLoc))
            {
                int comma = iconLoc.LastIndexOf(',');
                if (comma > 0 && int.TryParse(iconLoc.Substring(comma + 1).Trim(), out int idx))
                {
                    iconFile = iconLoc.Substring(0, comma);
                    index = idx;
                }
                else
                {
                    iconFile = iconLoc;
                }
                iconFile = Environment.ExpandEnvironmentVariables(iconFile.Trim().Trim('"'));
            }

            if (iconFile.Length > 0 && File.Exists(iconFile))
            {
                var img = FromIconFile(iconFile, index);
                if (img != null) return img;
            }

            if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                return FromIconFile(target, 0);

            // Shortcuts to special items (This PC, Recycle Bin, folders...): icon of the item the shortcut points to
            var viaPidl = FromShortcutPidl(lnk);
            if (viaPidl != null) return viaPidl;
        }
        catch
        {
            // Shortcut could not be read: fall back to the normal icon
        }
        return null;
    }

    private static ImageSource? FromShortcutPidl(string lnk)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            var link = (IShellLinkW)new ShellLinkCom();
            ((IPersistFile)link).Load(lnk, 0);
            link.GetIDList(out pidl);
            if (pidl == IntPtr.Zero) return null;

            var info = new SHFILEINFO();
            // SHGFI_PIDL | SHGFI_ICON | SHGFI_LARGEICON
            if (SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x8 | 0x100) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            try
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                bs.Freeze();
                return bs;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        catch { return null; }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCom { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, int mode);
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    private static ImageSource? FromIconFile(string file, int index)
    {
        IntPtr[] large = new IntPtr[1];
        try
        {
            if (ExtractIconEx(file, index, large, null, 1) > 0 && large[0] != IntPtr.Zero)
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(
                    large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
        }
        catch
        {
            // try the next method
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
        }

        return FromAssociated(file);
    }

    private static ImageSource? FromAssociated(string path)
    {
        var info = new SHFILEINFO();
        // SHGFI_ICON | SHGFI_LARGEICON
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var bs = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bs.Freeze();
            return bs;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
