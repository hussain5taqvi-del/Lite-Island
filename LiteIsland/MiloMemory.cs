using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace LiteIsland;

/// <summary>Short facts the user asked Milo to remember (name of the boss, favorite music...). Kept on this PC only.</summary>
internal static class MiloMemory
{
    private const int Max = 60;
    private static readonly object Lock = new();

    public static List<string> Load()
    {
        lock (Lock)
        {
            try
            {
                if (!File.Exists(MiloPaths.MemoryFile)) return new List<string>();
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(MiloPaths.MemoryFile)) ?? new List<string>();
            }
            catch { return new List<string>(); }
        }
    }

    private static void Save(List<string> l)
    {
        try
        {
            Directory.CreateDirectory(MiloPaths.Dir);
            File.WriteAllText(MiloPaths.MemoryFile, JsonSerializer.Serialize(l));
        }
        catch (Exception ex) { MiloLog.W("memory save: " + ex.Message); }
    }

    public static string Add(string fact)
    {
        fact = (fact ?? "").Trim();
        if (fact.Length == 0) return "Nothing to remember.";
        if (fact.Length > 220) fact = fact.Substring(0, 220);
        lock (Lock)
        {
            List<string> l = Load();
            if (l.Any(x => x.Equals(fact, StringComparison.OrdinalIgnoreCase))) return "I already know that.";
            l.Add(fact);
            while (l.Count > Max) l.RemoveAt(0);
            Save(l);
        }
        return "Remembered.";
    }

    public static string Forget(string match)
    {
        match = (match ?? "").Trim();
        if (match.Length == 0) return "Tell me what to forget.";
        lock (Lock)
        {
            List<string> l = Load();
            int n = l.RemoveAll(x => x.Contains(match, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save(l);
            return n > 0 ? "Forgot " + n + (n == 1 ? " thing." : " things.") : "I did not have anything like that.";
        }
    }
}

public sealed class MiloReminder
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Due { get; set; }
    public string? Url { get; set; }
}

/// <summary>
/// Reminders ("remind me at 5 to email my boss"). They are saved in a small file; the island checks them
/// (one cheap timer, only while at least one reminder exists), so they also work after a restart.
/// </summary>
internal static class MiloReminders
{
    private static readonly Mutex FileLock = new(false, @"Local\LiteIsland_MiloReminders");

    private static T WithLock<T>(Func<T> f)
    {
        bool got = false;
        try { got = FileLock.WaitOne(3000); } catch (AbandonedMutexException) { got = true; }
        try { return f(); }
        finally { if (got) { try { FileLock.ReleaseMutex(); } catch { } } }
    }

    private static List<MiloReminder> ReadNoLock()
    {
        try
        {
            if (!File.Exists(MiloPaths.RemindersFile)) return new List<MiloReminder>();
            return JsonSerializer.Deserialize<List<MiloReminder>>(File.ReadAllText(MiloPaths.RemindersFile)) ?? new List<MiloReminder>();
        }
        catch { return new List<MiloReminder>(); }
    }

    private static void WriteNoLock(List<MiloReminder> l)
    {
        try
        {
            Directory.CreateDirectory(MiloPaths.Dir);
            File.WriteAllText(MiloPaths.RemindersFile, JsonSerializer.Serialize(l));
        }
        catch (Exception ex) { MiloLog.W("reminders save: " + ex.Message); }
    }

    public static List<MiloReminder> Load() => WithLock(() => ReadNoLock().OrderBy(r => r.Due).ToList());

    public static MiloReminder Add(string text, DateTime due, string? url)
    {
        return WithLock(() =>
        {
            List<MiloReminder> l = ReadNoLock();
            var r = new MiloReminder { Id = Guid.NewGuid().ToString("N").Substring(0, 6), Text = text.Trim(), Due = due, Url = url };
            l.Add(r);
            WriteNoLock(l);
            return r;
        });
    }

    public static bool Remove(string idOrText)
    {
        return WithLock(() =>
        {
            List<MiloReminder> l = ReadNoLock();
            int n = l.RemoveAll(r => r.Id.Equals(idOrText, StringComparison.OrdinalIgnoreCase) ||
                                     (idOrText.Length >= 3 && r.Text.Contains(idOrText, StringComparison.OrdinalIgnoreCase)));
            if (n > 0) WriteNoLock(l);
            return n > 0;
        });
    }

    /// <summary>Takes the reminders that are due now out of the file and returns them.</summary>
    public static List<MiloReminder> TakeDue(DateTime now)
    {
        return WithLock(() =>
        {
            List<MiloReminder> l = ReadNoLock();
            var due = l.Where(r => r.Due <= now).ToList();
            if (due.Count > 0)
            {
                l.RemoveAll(r => r.Due <= now);
                WriteNoLock(l);
            }
            return due;
        });
    }
}
