using System;
using System.Threading;
using Velopack;

namespace Scribe.App;

/// <summary>
/// Real process entry point. Velopack must run before any WPF/UI code so its install, update, and
/// uninstall hooks (invoked by the bootstrapper with <c>--veloapp-*</c> arguments) can execute and
/// exit quickly without spinning up the tray app. After that returns we start WPF normally.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Processes Velopack lifecycle hooks and returns immediately during normal runs. Failures
        // here must never block the app from launching, so swallow and continue to the UI.
        try
        {
            VelopackApp.Build().Run();
        }
        catch
        {
            // Not packaged with Velopack (e.g. a plain dev build) — ignore and start the app.
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
