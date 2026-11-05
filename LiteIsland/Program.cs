using System;

namespace LiteIsland;

internal static class Program
{
    /// <summary>
    /// Helper mode ("--worker") skips the whole WPF application (windows, theme, tray...): it starts much faster.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // build check: unpack the tools that travel inside the exe and verify that they are there
        if (args.Length > 0 && args[0] == "--selftest")
            return Bundle.EnsureNow() ? 0 : 3;

        if (args.Length > 0 && args[0] == "--worker")
            return Worker.Run(args);

        // Milo runs without the WPF application (saves a lot of RAM: it must stay far below 100 MB)
        if (args.Length > 0 && args[0] == "--milo")
            return MiloWorker.Run(args);
        if (args.Length > 0 && args[0] == "--milo-sample")
            return MiloWorker.Sample(args);

        // Playlist player: Windows media player in its own process (keeps the island small)
        if (args.Length > 0 && args[0] == "--player")
            return PlayerWorker.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
