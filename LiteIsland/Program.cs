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
        if (args.Length > 0 && args[0] == "--worker")
            return Worker.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
