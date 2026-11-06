using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Overlyric.UiTests;

/// <summary>
/// The per-scenario services: launching the app with the seams, driving it with posted messages,
/// capturing and comparing against baselines, and recording what happened.
/// </summary>
internal sealed class ScenarioContext
{
    private readonly TextWriter _log;

    public ScenarioContext(
        Options options,
        string scenarioName,
        int repeatIndex,
        ScenarioResult result,
        string workDirectory,
        TextWriter log)
    {
        Options = options;
        ScenarioName = scenarioName;
        RepeatIndex = repeatIndex;
        Result = result;
        WorkDirectory = workDirectory;
        _log = log;
    }

    public Options Options { get; }

    public string ScenarioName { get; }

    public int RepeatIndex { get; }

    public ScenarioResult Result { get; }

    public string WorkDirectory { get; }

    /// <summary>
    /// True when this pass writes the baseline instead of reading it. Only the first repeat of an
    /// --update run writes: a later repeat compares, which is what makes the update self-checking.
    /// </summary>
    public bool WritingBaselines => Options.Update && RepeatIndex == 1;

    public void Log(string message) => _log.WriteLine($"    {message}");

    // --- Launching -------------------------------------------------------------------------------

    public AppSession LaunchSettings()
    {
        var instance = NewInstanceName();
        var dataRoot = Path.Combine(WorkDirectory, "data");
        var session = AppSession.Launch(Options, instance, dataRoot, demo: false, "OverlyricSettingsWindow");

        Result.InstanceName = instance;
        Result.DataRoot = dataRoot;

        CheckWindowGeometry(session);
        return session;
    }

    public AppSession LaunchDemo()
    {
        var instance = NewInstanceName();
        var dataRoot = Path.Combine(WorkDirectory, "data");
        var session = AppSession.Launch(Options, instance, dataRoot, demo: true, "OverlyricOverlayWindow");

        Result.InstanceName = instance;
        Result.DataRoot = dataRoot;
        Result.DpiNote = session.DescribeDpi();

        Check("overlay window is visible", session.IsVisible(), "The demo overlay window was found but is not visible.");
        return session;
    }

    private static string NewInstanceName()
    {
        // InstanceScope allows letters, digits, hyphens and underscores, capped at 32 characters; a
        // random suffix keeps parallel or repeated runs from sharing a mutex, a pipe and a folder.
        return "uitest-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    }

    /// <summary>
    /// The observable proof that the DPI seam is active: the layout is 780x584 *pixels* regardless
    /// of the monitor's scaling. The OS still reports the real monitor DPI, so that number is
    /// recorded in the failure message (and the result JSON) but is never the assertion.
    /// </summary>
    private void CheckWindowGeometry(AppSession session)
    {
        Result.DpiNote = session.DescribeDpi();

        var (clientWidth, clientHeight) = session.ClientSize();
        var (windowWidth, windowHeight) = session.WindowSize();

        Check(
            "settings client is 780x584 px",
            clientWidth == SettingsGeometry.ClientWidthDip && clientHeight == SettingsGeometry.ClientHeightDip,
            $"The settings window client is {clientWidth}x{clientHeight} px, expected 780x584. The DPI seam is not active: " +
            $"OVERLYRIC_TEST_INSTANCE and OVERLYRIC_TEST_DPI=96 must both be set before the app starts (the OS reports {session.OsWindowDpi} DPI here).");

        Check(
            "settings window is framed only by its own pixels",
            windowWidth == SettingsGeometry.ClientWidthDip && windowHeight == SettingsGeometry.ClientHeightDip,
            $"The settings window rectangle is {windowWidth}x{windowHeight} px, expected 780x584; the capture would not line up with the baseline.");

        // Capture once here so a compositor that has not committed its first frame yet is caught as
        // a launch problem, not as a mysterious baseline diff on the first section.
        var frame = Frame.Capture(session.Window);

        Check(
            "PrintWindow captures the client area",
            frame.Width == SettingsGeometry.ClientWidthDip && frame.Height == SettingsGeometry.ClientHeightDip,
            $"PrintWindow produced a {frame.Width}x{frame.Height} px image, expected 780x584.");

        Check(
            "the window is actually drawn",
            !frame.IsBlank,
            "The capture is a single colour: PrintWindow returned nothing, so the DirectComposition surface never became readable.");
    }

    // --- Assertions ------------------------------------------------------------------------------

    /// <summary>Records one assertion and fails the scenario immediately when it is not met.</summary>
    public void Check(string name, bool condition, string failure)
    {
        Result.Steps.Add(new StepResult
        {
            Name = name,
            Status = condition ? Status.Pass : Status.Fail,
            Message = condition ? null : failure
        });

        Log(condition ? $"PASS {name}" : $"FAIL {name}: {failure}");

        if (!condition)
        {
            throw new ScenarioFailureException($"{name}: {failure}");
        }
    }

    /// <summary>
    /// Records a step that could not be exercised in this environment, and why. A skip is not a
    /// pass: the parent prints it, so an unverified seam is visible instead of silently green.
    /// </summary>
    public void Skip(string name, string reason)
    {
        Result.Steps.Add(new StepResult { Name = name, Status = Status.Skip, Message = reason });
        Log($"SKIP {name}: {reason}");
    }

    /// <summary>
    /// Probes whether this environment lets a process connect to a named pipe at all. The suite is
    /// sometimes run from tool sandboxes that deny the pipe namespace; that is a property of the
    /// runner, not of the application, and a test has to tell the two apart before blaming the app.
    /// </summary>
    public bool NamedPipeTransportUnavailable(out string reason)
    {
        var name = "overlyric-uitest-probe-" + Guid.NewGuid().ToString("N");

        try
        {
            using var server = new NamedPipeServerStream(
                name,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            var waiting = server.WaitForConnectionAsync();

            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
            client.Connect(1_000);

            if (!waiting.Wait(2_000))
            {
                reason = "the probe connection was never accepted";
                return true;
            }

            reason = string.Empty;
            return false;
        }
        catch (Exception exception)
        {
            reason =
                $"a pipe created and connected inside one test process was refused ({exception.GetType().Name}: {exception.Message})";
            return true;
        }
    }

    // --- Captures --------------------------------------------------------------------------------

    /// <summary>
    /// Captures one step and either writes its baseline or compares against the committed one.
    /// Every capture is kept in the artifact folder, hit or miss, so a failed run can be reviewed
    /// without rerunning it.
    /// </summary>
    public void CaptureStep(string step, AppSession session, IReadOnlyList<MaskRect> masks, float dipWidth = SettingsGeometry.ClientWidthDip)
    {
        var frame = Frame.Capture(session.Window);
        var capturesDirectory = Path.Combine(WorkDirectory, "captures");
        var actualPath = Path.Combine(capturesDirectory, step + ".png");
        frame.Save(actualPath);

        var baselinePath = Path.Combine(Options.BaselinesDirectory, ScenarioName, step + ".png");
        var stepResult = new StepResult { Name = step, CapturePath = actualPath };

        if (frame.IsBlank)
        {
            stepResult.Status = Status.Fail;
            stepResult.Message = "The capture is a single colour; nothing was drawn, so it cannot be compared.";
            Result.Steps.Add(stepResult);
            throw new ScenarioFailureException($"{step}: {stepResult.Message}");
        }

        if (WritingBaselines)
        {
            BaselineStore.Save(frame, baselinePath);
            stepResult.Status = Status.Pass;
            stepResult.Message = $"baseline written to '{baselinePath}'";
        }
        else
        {
            var comparison = BaselineStore.Compare(
                baselinePath,
                frame,
                masks,
                dipWidth,
                Path.Combine(capturesDirectory, step + ".diff.png"));

            stepResult.Status = comparison.Ok ? Status.Pass : Status.Fail;
            stepResult.Message = comparison.Message;
        }

        Result.Steps.Add(stepResult);
        Log($"{stepResult.Status} {step} ({stepResult.Message})");

        if (stepResult.Status == Status.Fail)
        {
            throw new ScenarioFailureException($"{step}: {stepResult.Message}");
        }
    }

    /// <summary>
    /// Captures a frame for review only, with no comparison. Used where the pixels are not a stable
    /// contract (the layered overlay window is not guaranteed to render through PrintWindow) but a
    /// human still wants to see what the run saw.
    /// </summary>
    public void CaptureArtifact(string name, AppSession session, int settleMs = 0) =>
        CaptureWindow(name, session.Window, settleMs);

    /// <summary>The same review-only capture, for a window the session did not open itself.</summary>
    public void CaptureWindow(string name, nint window, int settleMs = 0)
    {
        // The overlay's first capture can land before the compositor has committed its first frame,
        // which is a black image. A settle window makes the review artifact actually show the scene
        // without turning a review artifact into an assertion.
        if (settleMs > 0)
        {
            WaitUntil(() => !Frame.Capture(window).IsBlank, settleMs);
        }

        var frame = Frame.Capture(window);
        var path = Path.Combine(WorkDirectory, "captures", name + ".png");
        frame.Save(path);

        Result.Steps.Add(new StepResult
        {
            Name = name,
            Status = Status.Pass,
            Message = frame.IsBlank ? "captured for review (the layered window rendered blank, as expected)" : "captured for review",
            CapturePath = path
        });

        Log($"PASS {name} (artifact '{path}')");
    }

    // --- Input -----------------------------------------------------------------------------------

    public void Click(AppSession session, (int X, int Y) point) => session.Click(point.X, point.Y);

    // --- Waiting ---------------------------------------------------------------------------------

    /// <summary>Polls a predicate; returns whether it became true inside the budget.</summary>
    public bool WaitUntil(Func<bool> predicate, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return predicate();
    }

    /// <summary>
    /// Waits for a property in the instance's own config.json to satisfy a predicate. This is how the
    /// suite proves a control did something: the app persists through the same path the user's copy
    /// uses, so a changed file is evidence the switch ran, the value reached the config and the
    /// window did not merely repaint itself.
    /// </summary>
    public (bool Ok, string Observed) WaitForConfig(
        AppSession session,
        string propertyName,
        Func<JsonElement, bool> predicate,
        int timeoutMs)
    {
        var observed = "<missing>";
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (TryReadConfigProperty(session, propertyName, predicate, ref observed))
            {
                return (true, observed);
            }

            Thread.Sleep(50);
        }

        return (TryReadConfigProperty(session, propertyName, predicate, ref observed), observed);
    }

    private static bool TryReadConfigProperty(
        AppSession session,
        string propertyName,
        Func<JsonElement, bool> predicate,
        ref string observed)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(session.ConfigPath));

            if (document.RootElement.TryGetProperty(propertyName, out var value))
            {
                observed = value.GetRawText();
                return predicate(value);
            }

            observed = "<property missing>";
        }
        catch (Exception exception)
        {
            // The app writes config.json through a temp file and a move; a read can land in the
            // middle of that, which is a reason to try again, not a reason to fail.
            observed = $"<unreadable: {exception.GetType().Name}>";
        }

        return false;
    }

    // --- Fixture expectations --------------------------------------------------------------------

    public bool FixtureBool(string propertyName) => FixtureFile.ReadBool(Options, propertyName);

    public double FixtureNumber(string propertyName) => FixtureFile.ReadNumber(Options, propertyName);

    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
}
