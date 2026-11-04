using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;

namespace LiteIsland;

// Focus modes (Normal, Do Not Disturb, Work, ...). They are shown by the tab on the right edge of the screen.
public partial class MainWindow
{
    private sealed record Prof(string Name, string Glyph, Color Col, bool Toasts, int Vol, int Bright, string Sub);

    private static readonly Prof[] Profiles =
    {
        new("Normal", "\uE80F", Color.FromRgb(0x0A, 0x84, 0xFF), true, -1, -1, "Notifications on"),
        new("Do Not Disturb", "\uE708", Color.FromRgb(0x5E, 0x5C, 0xE6), false, -1, -1, "Notifications off"),
        new("Work", "\uE716", Color.FromRgb(0xBF, 0x5A, 0xF2), false, 30, -1, "Quiet, volume 30%"),
        new("Personal", "\uE77B", Color.FromRgb(0x30, 0xD1, 0x58), true, 60, -1, "Volume 60%"),
        new("Gaming", "\uE7FC", Color.FromRgb(0xFF, 0x45, 0x3A), false, 80, 100, "Quiet, bright, loud")
    };

    private int ProfileIndex() => Math.Max(0, Array.FindIndex(Profiles, p => p.Name == _s.Profile));

    private void ApplyProfile(string name)
    {
        Prof? p = Profiles.FirstOrDefault(x => x.Name == name);
        if (p == null) return;
        _s.Profile = p.Name;
        _s.Save();
        Task.Run(() =>
        {
            SysControls.SetToasts(p.Toasts);
            if (p.Vol >= 0) SysControls.SetVolume(p.Vol / 100f);
            if (p.Bright >= 0) SysControls.RequestBrightness(p.Bright);
        });
        LobeModes(Array.IndexOf(Profiles, p));
    }
}
