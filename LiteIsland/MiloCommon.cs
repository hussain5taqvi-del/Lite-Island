using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LiteIsland;

/// <summary>Files of the Milo assistant (all under %AppData%\LiteIsland).</summary>
internal static class MiloPaths
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteIsland");
    public static string Log => Path.Combine(Dir, "milo.log");
    public static string KeyFile => Path.Combine(Dir, "milo.key");
    public static string MemoryFile => Path.Combine(Dir, "milo-memory.json");
    public static string RemindersFile => Path.Combine(Dir, "milo-reminders.json");
}

/// <summary>Plain text log. It never contains the API key. Settings has a button that opens it.</summary>
internal static class MiloLog
{
    private static readonly object Lock = new();
    private static bool _checked;

    public static void W(string text)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(MiloPaths.Dir);
                if (!_checked)
                {
                    _checked = true;
                    var fi = new FileInfo(MiloPaths.Log);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = MiloPaths.Log + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(MiloPaths.Log, old);
                    }
                }
                File.AppendAllText(MiloPaths.Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + "\r\n");
            }
        }
        catch { }
    }
}

/// <summary>The Gemini API key, encrypted with the Windows login (DPAPI). Only this user on this PC can read it.</summary>
internal static class MiloKey
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiteIsland.Milo.v1");

    public static string Load()
    {
        try
        {
            if (!File.Exists(MiloPaths.KeyFile)) return "";
            byte[] raw = ProtectedData.Unprotect(File.ReadAllBytes(MiloPaths.KeyFile), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw).Trim();
        }
        catch (Exception ex)
        {
            MiloLog.W("key: could not read (" + ex.Message + ")");
            return "";
        }
    }

    public static bool Save(string key)
    {
        try
        {
            Directory.CreateDirectory(MiloPaths.Dir);
            key = (key ?? "").Trim();
            if (key.Length == 0)
            {
                if (File.Exists(MiloPaths.KeyFile)) File.Delete(MiloPaths.KeyFile);
                return true;
            }
            File.WriteAllBytes(MiloPaths.KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex)
        {
            MiloLog.W("key: could not save (" + ex.Message + ")");
            return false;
        }
    }

    public static bool Has => File.Exists(MiloPaths.KeyFile);
}
