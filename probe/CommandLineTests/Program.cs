using Overlyric.App;
using Overlyric.Interop;

namespace Overlyric.CommandLineTests;

/// <summary>
/// Checks the command-line grammar a script or a second launch uses, and the named-pipe round trip
/// that forwards it to the running instance.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        GrammarChecks();
        PipeChecks();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void GrammarChecks()
    {
        Check("--toggle parses", Parse("--toggle") == HostCommand.Toggle, Parse("--toggle"));
        Check("--show parses", Parse("--show") == HostCommand.Show, Parse("--show"));
        Check("--hide parses", Parse("--hide") == HostCommand.Hide, Parse("--hide"));
        Check("--layout=single parses", Parse("--layout=single") == HostCommand.LayoutSingle, Parse("--layout=single"));
        Check("--layout=peek parses", Parse("--layout=peek") == HostCommand.LayoutPeek, Parse("--layout=peek"));
        Check("--layout=next parses", Parse("--layout=next") == HostCommand.LayoutNext, Parse("--layout=next"));
        Check("--layout=multi parses", Parse("--layout=multi") == HostCommand.LayoutMulti, Parse("--layout=multi"));
        Check("--offset=+250 parses later", Parse("--offset=+250") == HostCommand.OffsetLater, Parse("--offset=+250"));
        Check("--offset=-250 parses earlier", Parse("--offset=-250") == HostCommand.OffsetEarlier, Parse("--offset=-250"));
        Check("--offset=reset parses", Parse("--offset=reset") == HostCommand.OffsetReset, Parse("--offset=reset"));
        Check("--preview parses", Parse("--preview") == HostCommand.Preview, Parse("--preview"));
        Check("--settings parses", Parse("--settings") == HostCommand.Settings, Parse("--settings"));
        Check("--diagnostics parses", Parse("--diagnostics") == HostCommand.Diagnostics, Parse("--diagnostics"));
        Check("--refresh parses", Parse("--refresh") == HostCommand.RefreshLyrics, Parse("--refresh"));
        Check("--lock and --unlock parse", Parse("--lock") == HostCommand.Lock && Parse("--unlock") == HostCommand.Unlock, "");
        Check("--exit parses", Parse("--exit") == HostCommand.Exit, Parse("--exit"));

        Check("the switch is case-insensitive", Parse("--TOGGLE") == HostCommand.Toggle, Parse("--TOGGLE"));
        Check("no arguments is a no-op", Parse() == HostCommand.None, Parse());
        Check("the last command wins", Parse("--show", "--hide") == HostCommand.Hide, Parse("--show", "--hide"));
        Check("legacy startup flags are not errors", CommandLine.TryParse(["--demo", "--settings", "--preview"], out _, out var legacyError) && legacyError is null, legacyError);

        var unknown = CommandLine.TryParse(["--tell-me-a-joke"], out var unknownCommand, out var unknownError);
        Check("an unknown switch is an error, not a silent no-op",
            !unknown && unknownCommand == HostCommand.None && unknownError is not null, unknownError);

        var badLayout = CommandLine.TryParse(["--layout=diagonal"], out _, out var layoutError);
        Check("an unknown layout value is refused", !badLayout && layoutError is not null, layoutError);

        var badOffset = CommandLine.TryParse(["--offset=lots"], out _, out var offsetError);
        Check("an unknown offset value is refused", !badOffset && offsetError is not null, offsetError);

        Check("forwarding joins the arguments the parser understands",
            CommandLine.Forward(["--toggle", "--layout=peek"]) == "--toggle --layout=peek",
            CommandLine.Forward(["--toggle", "--layout=peek"]));
    }

    private static void PipeChecks()
    {
        using var cancellation = new CancellationTokenSource();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        CommandPipe.Start(
            line =>
            {
                received.TrySetResult(line);
                cancellation.Cancel();
            },
            cancellation.Token);

        // Give the server a moment to start listening before the first connect attempt.
        var sent = false;

        for (var attempt = 0; attempt < 20 && !sent; attempt++)
        {
            sent = CommandPipe.TrySend("--toggle", 200);
            if (!sent)
            {
                Thread.Sleep(50);
            }
        }

        Check("a client can reach the listening instance", sent, sent);
        Check("the forwarded command arrives intact",
            received.Task.Wait(TimeSpan.FromSeconds(2)) && received.Task.Result == "--toggle",
            received.Task.IsCompleted ? received.Task.Result : "timeout");

        cancellation.Cancel();
        Check("a send with nothing listening fails instead of hanging", !CommandPipe.TrySend("--toggle", 100), "");
    }

    private static HostCommand Parse(params string[] args)
    {
        CommandLine.TryParse(args, out var command, out _);
        return command;
    }

    private static void Check(string what, bool ok, object? detail)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine($"PASS | {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL | {what}   (got: {detail})");
    }
}
