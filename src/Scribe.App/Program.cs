using System;
using System.Threading;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
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
        // The startup stage timeline's anchor, before anything else runs. Its marks cost one timestamp read each and write
        // nothing; only StartupStageTiming logs them, once the host can say whether the flag is on.
        var stages = StageTimeline.Start();

        // SkipVelopackWhenPackaged: a process with Windows package identity (the Store package, or a sideloaded MSIX) is
        // never a Velopack install, so it leaves out Velopack's hook, which otherwise looks for an install and writes its own
        // log on every start. The flags are read here too, before the host exists; the host reads the same variable, which
        // nothing in the process changes.
        var skipVelopack = PerfFlags.FromEnvironment().IsOn(PerfFlags.SkipVelopackWhenPackaged) &&
            WindowsPackageIdentity.IsPackaged();

        // Processes Velopack lifecycle hooks and returns immediately during normal runs. Failures
        // here must never block the app from launching, so swallow and continue to the UI.
        try
        {
            if (!skipVelopack)
            {
                VelopackApp.Build()
                    .OnAfterInstallFastCallback(_ => Infrastructure.ShellIconCache.Refresh())
                    .OnAfterUpdateFastCallback(_ => Infrastructure.ShellIconCache.Refresh())
                    .OnRestarted(_ => Infrastructure.ShellIconCache.Refresh())
                    .Run();
            }
        }
        catch
        {
            // Not packaged with Velopack (e.g. a plain dev build); ignore and start the app.
        }

        stages.Mark("velopack");
        var app = new App();
        app.InitializeComponent();
        stages.Mark("app");
        app.StartupStages = stages;
        app.Run();
    }
}
