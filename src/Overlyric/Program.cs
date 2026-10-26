using Overlyric.App;
using Overlyric.Interop;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric;

internal static class Program
{
    private const string MutexName = "Overlyric.SingleInstance.v1";
    private const int DpiAwarenessPerMonitorV2 = -4;

    private static int Main(string[] args)
    {
        // Also declared in app.manifest; repeated here so an unpackaged run behaves identically.
        SetProcessDpiAwarenessContext(new nint(DpiAwarenessPerMonitorV2));

        if (!CommandLine.TryParse(args, out var startupCommand, out var argumentError))
        {
            // A script that asked for something unknown deserves a failure, not a silent success.
            new DiagnosticLog(Path.Combine(AppConfig.DataDirectory, "diagnostics.log"))
                .Add("args", argumentError ?? "Unknown arguments.");
            return 2;
        }

        using var instance = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // The running instance owns the overlay and the tray: hand it the command instead of
            // starting a second copy. If nothing is listening, exit as the old guard did.
            _ = CommandPipe.TrySend(CommandLine.Forward(args));
            return 0;
        }

        // --demo drives the overlay from a canned, synced track so the look can be
        // previewed (and the renderer verified) without anything playing.
        var demo = Array.Exists(args, a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

        // --settings opens the configuration window straight away, which is how a
        // shortcut or a second launch is expected to behave.
        var settings = Array.Exists(args, a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));

        // --preview starts the same canned track as the tray item, so a screenshot or a scripted
        // check can exercise the overlay without waiting for a real player.
        var preview = Array.Exists(args, a => string.Equals(a, "--preview", StringComparison.OrdinalIgnoreCase));

        var config = AppConfig.Load();
        var host = new AppHost(config, demo, settings, preview, startupCommand);

        return host.Run();
    }
}
