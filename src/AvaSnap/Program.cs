using System;
using System.Windows;
using Velopack;

namespace AvaSnap;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Must run before anything else touches WPF or app state:
        // VelopackApp.Build().Run() handles Velopack's own special
        // first-run/update/uninstall invocations (creating shortcuts, etc.)
        // and exits the process immediately for those, before any window
        // would otherwise open. A safe no-op when this exe is launched
        // directly rather than through a real Velopack install -- e.g.
        // running it straight from bin/Debug during development.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
