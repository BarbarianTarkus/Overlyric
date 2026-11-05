using System.Text.Json;

namespace Overlyric.UiTests;

/// <summary>One independent piece of behaviour the suite checks on its own private desktop.</summary>
internal interface IScenario
{
    string Name { get; }

    string Description { get; }

    void Run(ScenarioContext context);
}

internal static class ScenarioRegistry
{
    public static readonly IScenario[] All =
    [
        new SettingsSectionsScenario(),
        new SettingsToggleScenario(),
        new SettingsStepperScenario(),
        new SettingsEscapeScenario(),
        new OverlayDemoScenario()
    ];

    public static IReadOnlyList<IScenario> Select(string filter) =>
        filter.Length == 0
            ? All
            : [.. All.Where(scenario => scenario.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The scenarios in the order they run; used by --list and by the parent's expectations.</summary>
    public static void PrintList(TextWriter writer)
    {
        writer.WriteLine("Scenarios:");

        foreach (var scenario in All)
        {
            writer.WriteLine($"  {scenario.Name,-26} {scenario.Description}");
        }
    }
}

/// <summary>
/// Every navigation entry, clicked and captured. This is the broad visual contract: the eight
/// sections, their labels, their controls and their states come from the fixture, so any layout
/// regression in the window shows up as a diff in exactly the section that changed.
/// </summary>
internal sealed class SettingsSectionsScenario : IScenario
{
    public string Name => "settings-sections";

    public string Description => "clicks the eight navigation entries and compares each section to its baseline";

    public void Run(ScenarioContext context)
    {
        using var session = context.LaunchSettings();

        foreach (var (section, clientY) in SettingsGeometry.NavEntries)
        {
            session.Click(SettingsGeometry.NavX, clientY);

            // The window repaints synchronously on the click, but the DirectComposition commit that
            // PrintWindow reads lands a frame later; a quarter second is comfortably one frame.
            context.Sleep(250);
            context.CaptureStep(section, session, SettingsGeometry.MasksForSection(section));
        }
    }
}

/// <summary>
/// The Karaoke sweep switch, both ways, checked through the config file rather than through OCR.
/// A click that lands on the wrong control cannot flip the persisted value, and a repaint alone
/// cannot change the file, so this is the test of hit-testing, the custom switch and persistence at
/// once.
/// </summary>
internal sealed class SettingsToggleScenario : IScenario
{
    public string Name => "settings-toggle-persists";

    public string Description => "clicks the Karaoke sweep switch and proves the flipped value reaches config.json";

    public void Run(ScenarioContext context)
    {
        var expected = context.FixtureBool("Karaoke");
        using var session = context.LaunchSettings();
        var point = SettingsGeometry.Toggle(SettingsGeometry.KaraokeToggleRow);

        context.Click(session, point);
        var flipped = context.WaitForConfig(
            session,
            "Karaoke",
            value => value.ValueKind == (expected ? JsonValueKind.False : JsonValueKind.True),
            3_000);

        context.Check(
            $"Karaoke persisted as {!expected}",
            flipped.Ok,
            $"config.json still reports Karaoke={flipped.Observed} three seconds after clicking the switch at ({point.X},{point.Y}).");

        context.CaptureArtifact("lyrics-karaoke-toggled", session);

        context.Click(session, point);
        var restored = context.WaitForConfig(
            session,
            "Karaoke",
            value => value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False),
            3_000);

        context.Check(
            $"Karaoke restored to {expected}",
            restored.Ok,
            $"config.json still reports Karaoke={restored.Observed} three seconds after the second click.");

        context.CaptureArtifact("lyrics-karaoke-restored", session);
    }
}

/// <summary>
/// The active lyric size stepper: "+" raises the persisted size by the window's own step and "-"
/// puts it back. The step is a constant of the window, so the test reads the fixture's value and
/// asserts the change instead of pinning a second copy of the number.
/// </summary>
internal sealed class SettingsStepperScenario : IScenario
{
    public string Name => "settings-stepper";

    public string Description => "presses + and - on the lyric size stepper and proves the stored font size moves by the step";

    public void Run(ScenarioContext context)
    {
        var initial = context.FixtureNumber("ActiveFontSize");
        var raised = initial + SettingsGeometry.LyricSizeStep;
        using var session = context.LaunchSettings();

        context.Click(session, SettingsGeometry.StepperPlus(SettingsGeometry.LyricSizeRow));
        var up = context.WaitForConfig(
            session,
            "ActiveFontSize",
            value => NearlyEqual(value, raised),
            3_000);

        context.Check(
            $"ActiveFontSize rose to {raised}",
            up.Ok,
            $"config.json still reports ActiveFontSize={up.Observed} three seconds after pressing +; expected {raised} (fixture {initial} plus the window's step {SettingsGeometry.LyricSizeStep}).");

        context.Click(session, SettingsGeometry.StepperMinus(SettingsGeometry.LyricSizeRow));
        var back = context.WaitForConfig(
            session,
            "ActiveFontSize",
            value => NearlyEqual(value, initial),
            3_000);

        context.Check(
            $"ActiveFontSize restored to {initial}",
            back.Ok,
            $"config.json still reports ActiveFontSize={back.Observed} after pressing -; expected {initial}.");
    }

    private static bool NearlyEqual(JsonElement value, double expected) =>
        value.ValueKind == JsonValueKind.Number && Math.Abs(value.GetDouble() - expected) < 0.001;
}

/// <summary>
/// Escape closes the window. The key is posted to the window's own queue, so this also proves the
/// window procedure is receiving and acting on messages from this desktop's queue.
/// </summary>
internal sealed class SettingsEscapeScenario : IScenario
{
    public string Name => "settings-escape-closes";

    public string Description => "posts Escape and proves the settings window hides";

    public void Run(ScenarioContext context)
    {
        using var session = context.LaunchSettings();

        context.Check(
            "settings window is visible before the key",
            session.IsVisible(),
            "The settings window was not visible when the scenario started.");

        session.Key(NativeMethods.VK_ESCAPE);

        var hidden = context.WaitUntil(() => !session.IsVisible(), 2_000);

        context.Check(
            "Escape hides the settings window",
            hidden,
            "The settings window was still visible two seconds after WM_KEYDOWN(VK_ESCAPE).");
    }
}

/// <summary>
/// The demo overlay: the click-through contract, the single-line geometry, and the live command
/// pipe. No pixel baseline: the overlay is a layered window, and PrintWindow is not a promise for
/// that surface, so the pixels are kept as review artifacts and the assertions are window facts.
/// </summary>
internal sealed class OverlayDemoScenario : IScenario
{
    private const int SingleLineWidthPx = 1200;
    private const int SingleLineHeightPx = 200;
    private const int MultiLineWidthPx = 1040;
    private const int MultiLineHeightPx = 430;

    /// <summary>The app's <c>Commands.LayoutMulti</c>, posted as WM_HOTKEY when the pipe cannot be used.</summary>
    private const int LayoutMultiCommand = 177;

    public string Name => "overlay-demo-smoke";

    public string Description => "launches --demo, checks the overlay's click-through styles and switches layout over the isolated command pipe";

    public void Run(ScenarioContext context)
    {
        using var session = context.LaunchDemo();

        var extendedStyle = session.ExtendedStyle();

        context.Check(
            "the overlay is click-through (WS_EX_TRANSPARENT)",
            (extendedStyle & NativeMethods.WS_EX_TRANSPARENT) != 0,
            $"Extended style is 0x{extendedStyle:X8}; WS_EX_TRANSPARENT is not set, so a locked overlay would eat every click behind it.");

        context.Check(
            "the overlay never activates (WS_EX_NOACTIVATE)",
            (extendedStyle & NativeMethods.WS_EX_NOACTIVATE) != 0,
            $"Extended style is 0x{extendedStyle:X8}; WS_EX_NOACTIVATE is not set, so the overlay could take the foreground.");

        var (width, height) = session.ClientSize();

        context.Check(
            $"the single-line overlay is {SingleLineWidthPx}x{SingleLineHeightPx} px",
            width == SingleLineWidthPx && height == SingleLineHeightPx,
            $"The overlay measures {width}x{height} px at 96 DPI; expected {SingleLineWidthPx}x{SingleLineHeightPx}.");

        context.CaptureArtifact("overlay-single", session, settleMs: 2_000);

        var pipeError = session.TrySendCommand("--layout=multi");
        var switched = false;

        if (pipeError is null)
        {
            switched = context.WaitUntil(SwitchedToMultiLine, 3_000);
            var (finalWidth, finalHeight) = session.ClientSize();

            context.Check(
                $"the command pipe switched the layout to {MultiLineWidthPx}x{MultiLineHeightPx} px",
                switched,
                $"The overlay still measures {finalWidth}x{finalHeight} px three seconds after '--layout=multi' was written to the isolated command pipe '{session.PipeName}'.");
        }
        else if (context.NamedPipeTransportUnavailable(out var pipeTransportReason))
        {
            // The runner blocks the pipe namespace, so the transport cannot be exercised here. The
            // layout switch is still tested: the app's own command dispatch is reached by posting
            // the command its tray menu would send.
            context.Skip(
                "the isolated command pipe",
                $"'{session.PipeName}' could not be used ({pipeError}) and {pipeTransportReason}. The layout switch is checked through the posted command instead.");

            session.PostCommand(LayoutMultiCommand);
            switched = context.WaitUntil(SwitchedToMultiLine, 3_000);
            var (finalWidth, finalHeight) = session.ClientSize();

            context.Check(
                $"the posted layout command switched the overlay to {MultiLineWidthPx}x{MultiLineHeightPx} px",
                switched,
                $"The overlay still measures {finalWidth}x{finalHeight} px three seconds after the layout command was posted to the application's message window.");
        }
        else
        {
            context.Check(
                "the isolated command pipe accepted the layout command",
                false,
                $"Could not send '--layout=multi' to '{session.PipeName}': {pipeError}.");
        }

        context.CaptureArtifact("overlay-multi", session);

        bool SwitchedToMultiLine()
        {
            var (width, height) = session.ClientSize();
            return width == MultiLineWidthPx && height == MultiLineHeightPx;
        }
    }
}
