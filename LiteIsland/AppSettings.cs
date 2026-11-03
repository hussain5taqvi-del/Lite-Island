using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteIsland;

/// <summary>A downloaded file that is deleted by itself at the chosen time (only files LiteIsland downloaded).</summary>
public sealed class AutoDeleteItem
{
    public string Path { get; set; } = "";
    public long DueUtcTicks { get; set; }
}

/// <summary>
/// User settings and saved lists. Stored in %AppData%\LiteIsland\settings.json.
/// </summary>
public sealed class AppSettings
{
    // Size and corners
    public double CollapsedWidth { get; set; } = 340;
    public double CollapsedHeight { get; set; } = 36;
    public double CollapsedRadius { get; set; } = 20;
    public double ExpandedWidth { get; set; } = 440;
    public double ExpandedHeight { get; set; } = 200;
    public double ExpandedRadius { get; set; } = 36;
    public double TopRadius { get; set; } = 12; // size of the notch ears
    public double TextScale { get; set; } = 100; // percent: size of the text in the collapsed island

    // Shape: "Notch" (MacBook, attached to the top edge) or "Island" (iPhone, floating pill)
    public string IslandShape { get; set; } = "Notch";
    public double TopGap { get; set; } = 10;     // distance from the screen top (Island shape only)

    // Appearance
    public string IslandColor { get; set; } = "#000000"; // default: black
    public double BgOpacity { get; set; } = 100;          // percent, 20..100
    public double AnimSpeed { get; set; } = 1.0;          // 1 = normal
    public string AnimStyle { get; set; } = "Scale";        // Smooth, Bounce, Genie, Scale, Snappy
    public string TextColor { get; set; } = "#FFFFFF";    // main text color of the island
    public string Theme { get; set; } = "Dark";           // Dark or Light
    public string AccentColor { get; set; } = "#FF9F0A";  // highlight color (calendar today, weather, system bars)
    public bool Glass { get; set; } = false;              // Liquid Glass look
    public bool ReserveTop { get; set; } = false;         // keep maximized windows below the island (like a menu bar)
    public bool WindowSnap { get; set; } = true;          // Ctrl + Alt + arrow keys snap the active window
    public double GlassOpacity { get; set; } = 28;        // percent, 20..100 (lower = more see-through)

    public static readonly string[] AnimStyles = { "Smooth", "Bounce", "Genie", "Scale", "Snappy", "Notch", "Minimal", "Fluid" };

    // Activity bars
    public bool HudVolume { get => true; set { } } // always on (no switch in Settings)
    public bool HudBrightness { get => true; set { } } // always on (no switch in Settings)
    public bool HudBluetooth { get => true; set { } } // always on (no switch in Settings)

    // Weather and privacy
    public string WeatherCity { get; set; } = "";         // empty = detect from the internet connection
    public bool UseFahrenheit { get; set; } = false;
    public bool HudPrivacy { get => true; set { } } // always on (no switch in Settings)          // orange / green dot while the microphone / camera is used

    // Clock
    public bool Use24Hour { get; set; } = false;

    // Settings file version (used to upgrade old saved sizes)
    public int Version { get; set; } = 0;

    // Files, links and clipboard
    public bool FileWheel { get; set; } = true;        // hold the ring key on a selected file: ring of actions
    public bool LinkDetect { get; set; } = true;       // a copied video link opens the download card
    public bool LinkAnySite { get; set; } = true;      // also react to links of sites that are not in the known list
    public bool ClipHistory { get; set; } = true;      // clipboard page (kept in RAM only, never saved)
    public string DownloadDir { get; set; } = "";      // empty = Downloads\LiteIsland
    public int DefaultQuality { get; set; } = 1080;
    public int DlLimitMBps { get; set; } = 4;           // 0 = unlimited; keeps the rest of the PC online
    public int RamLimitMb { get; set; } = 30;          // hard limit for the island process
    public List<AutoDeleteItem> AutoDelete { get; set; } = new();
    public int AppsKey { get; set; } = 0x14;           // hold to open the ring of favorite apps (virtual-key code, default Caps Lock)
    public bool AppsRing { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;       // checks GitHub Releases twice a day and installs small updates by itself
    public int RingKey { get; set; } = 0x58;           // hold this key on a selected file: the ring is open until it is let go (virtual-key code, default X)
    public Dictionary<string, List<string>> RingPins { get; set; } = new();   // formats pinned to the front of the ring, per file type

    public string Profile { get; set; } = "Normal";      // Control page: Normal, Do Not Disturb, Work, Personal, Gaming

    // Milo assistant (the Gemini API key is NOT here: it is stored encrypted in milo.key)
    public bool MiloEnabled { get; set; } = false;
    public string MiloName { get; set; } = "Milo";
    public bool MiloWake { get; set; } = false;          // answer when its name is said (uses the microphone all the time)
    public string MiloVoice { get; set; } = "Puck";
    public string MiloModel { get; set; } = "gemini-2.5-flash-native-audio-preview-09-2025";
    public bool MiloHeadphones { get; set; } = false;    // with headphones Milo can be interrupted while talking
    public bool MiloGreet { get; set; } = false;         // not used any more (Milo never talks first)
    public string MiloHotkey { get; set; } = "Ctrl+Alt+Space";

    // Saved lists (paths only, nothing is copied)
    public List<string> FavoriteApps { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LiteIsland", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s != null)
                {
                    // Upgrade from older versions: the collapsed island now holds time + battery
                    if (s.Version < 6)
                    {
                        s.CollapsedHeight = 36;
                        s.CollapsedRadius = 18;
                    }
                    if (s.Version < 7)
                    {
                        s.CollapsedWidth = 280; // room for the song name in the middle
                    }
                    if (s.Version < 8)
                    {
                        s.TopRadius = 10; // notch ears
                        s.ExpandedHeight = Math.Max(s.ExpandedHeight, 180);
                    }
                    if (s.Version < 11)
                    {
                        s.CollapsedWidth = Math.Max(s.CollapsedWidth, 340); // room for time + weather
                        s.ExpandedWidth = Math.Max(s.ExpandedWidth, 440);   // calendar / system pages
                        s.ExpandedHeight = Math.Max(s.ExpandedHeight, 200);
                        s.GlassOpacity = 45; // clearer glass
                    }
                    if (s.Version < 12)
                    {
                        s.CollapsedWidth = Math.Max(s.CollapsedWidth, 340); // time + weather + dots need room
                        s.CollapsedRadius = 20;  // rounder, more premium corners
                        s.ExpandedRadius = 36;
                        s.TopRadius = 12;
                        s.GlassOpacity = 28;     // clearer glass
                    }
                    if (s.Version < 16) s.TextScale = Math.Max(s.TextScale, 100); // v1.2.5: text was too small
                    if (s.Version < 17) s.AnimStyle = "Scale"; // v1.2.5: Scale is the default animation (change it in Settings)
                    s.Version = 17; // v1.2: shape, shadow, fonts, favorite songs (defaults apply automatically)
                    s.Clamp();
                    return s;
                }
            }
        }
        catch
        {
            // Corrupt file: fall back to defaults
        }
        return new AppSettings { Version = 17 };
    }

    public void Save()
    {
        try
        {
            Version = 17;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Not being able to save is not fatal
        }
    }

    public void Clamp()
    {
        CollapsedWidth = Math.Clamp(CollapsedWidth, 150, 460);
        CollapsedHeight = Math.Clamp(CollapsedHeight, 24, 60);
        CollapsedRadius = Math.Clamp(CollapsedRadius, 0, 40);
        ExpandedWidth = Math.Clamp(ExpandedWidth, 380, 640);
        ExpandedHeight = Math.Clamp(ExpandedHeight, 165, 280);
        ExpandedRadius = Math.Clamp(ExpandedRadius, 0, 50);
        TopRadius = Math.Clamp(TopRadius, 0, 24);
        TopGap = Math.Clamp(TopGap, 0, 30);
        TextScale = Math.Clamp(TextScale, 60, 220);
        if (IslandShape != "Notch" && IslandShape != "Island") IslandShape = "Notch";
        BgOpacity = Math.Clamp(BgOpacity, 20, 100);
        GlassOpacity = Math.Clamp(GlassOpacity, 10, 100);
        WeatherCity = ""; // always detected automatically
        if (!Regex.IsMatch(AccentColor ?? "", "^#[0-9A-Fa-f]{6}$")) AccentColor = "#FF9F0A";
        if (Theme != "Dark" && Theme != "Light") Theme = "Dark";
        AnimSpeed = Math.Clamp(AnimSpeed, 0.4, 3.0);
        if (!Regex.IsMatch(IslandColor ?? "", "^#[0-9A-Fa-f]{6}$")) IslandColor = "#000000";
        if (!Regex.IsMatch(TextColor ?? "", "^#[0-9A-Fa-f]{6}$")) TextColor = "#FFFFFF";
        if (Array.IndexOf(AnimStyles, AnimStyle) < 0) AnimStyle = "Scale";

        FavoriteApps ??= new List<string>();
        AutoDelete ??= new List<AutoDeleteItem>();
        RingPins ??= new Dictionary<string, List<string>>();
        RamLimitMb = Math.Clamp(RamLimitMb, 20, 400);
        AppsRing = true; // always on (no switch in Settings)
        if (AppsKey < 8 || AppsKey > 254 || AppsKey == 0x1B || AppsKey == 0x0D || AppsKey == 0x09) AppsKey = 0x14;
        if (RingKey < 8 || RingKey > 254 || RingKey == 0x1B || RingKey == 0x0D || RingKey == 0x09) RingKey = 0x58;
        // These have no switch any more: always on / unlimited
        FileWheel = true;
        LinkDetect = true;
        LinkAnySite = true;
        ClipHistory = true;
        DlLimitMBps = 0;
        DefaultQuality = Math.Clamp(DefaultQuality, 720, 4320);
        DownloadDir ??= "";
    }

    /// <summary>Copies size, corner and appearance values (used by "Reset").</summary>
    public void CopyFrom(AppSettings o)
    {
        CollapsedWidth = o.CollapsedWidth;
        CollapsedHeight = o.CollapsedHeight;
        CollapsedRadius = o.CollapsedRadius;
        ExpandedWidth = o.ExpandedWidth;
        ExpandedHeight = o.ExpandedHeight;
        ExpandedRadius = o.ExpandedRadius;
        TopRadius = o.TopRadius;
        IslandShape = o.IslandShape;
        TextScale = o.TextScale;
        TopGap = o.TopGap;
        WeatherCity = o.WeatherCity;
        IslandColor = o.IslandColor;
        BgOpacity = o.BgOpacity;
        AnimSpeed = o.AnimSpeed;
        AnimStyle = o.AnimStyle;
        TextColor = o.TextColor;
        Theme = o.Theme;
        AccentColor = o.AccentColor;
        UseFahrenheit = o.UseFahrenheit;
        HudPrivacy = o.HudPrivacy;
        Glass = o.Glass;
        WindowSnap = o.WindowSnap;
        ReserveTop = o.ReserveTop;
        GlassOpacity = o.GlassOpacity;
        HudVolume = o.HudVolume;
        HudBrightness = o.HudBrightness;
        HudBluetooth = o.HudBluetooth;
    }
}

