using System.Diagnostics;
using System.Text;

namespace Overlyric.UiTests;

/// <summary>
/// Entry point. A normal run is the parent: it measures the user's desktop and the production data
/// folder, creates a private desktop, relaunches itself as a worker on that desktop, waits, and
/// reports. The worker is the half that touches the application.
/// </summary>
/// <remarks>
/// The split exists because a process cannot move itself to another desktop: the desktop is chosen
/// in <c>STARTUPINFO.lpDesktop</c> when the process starts. Everything the suite does to the app -
/// launching it, posting to its window, capturing its surface - therefore happens in a process that
/// was born on the private desktop, and every window it creates or reads belongs to that desktop.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Real pixels, no virtualisation: the suite compares capture dimensions, so it must see the
        // window's actual client area. This has nothing to do with the app's test DPI seam, which is
        // an environment variable the app reads for itself.
        NativeMethods.MakePerMonitorAware();

        Options options;

        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return 1;
        }

        if (options.Help)
        {
            PrintUsage(Console.Out);
            return 0;
        }

        if (options.List)
        {
            ScenarioRegistry.PrintList(Console.Out);
            return 0;
        }

        return options.Worker ? RunWorker(options) : RunParent(options);
    }

    // --- Parent ----------------------------------------------------------------------------------

    private static int RunParent(Options options)
    {
        var started = DateTime.Now;
        var runId = $"{started:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        var artifactDirectory = Path.Combine(
            options.RepoRoot,
            Options.ArtifactsRelativePath.Replace('/', Path.DirectorySeparatorChar),
            runId);
        var resultPath = Path.Combine(artifactDirectory, "result.json");
        var workerLogPath = Path.Combine(artifactDirectory, "worker.log");

        Directory.CreateDirectory(artifactDirectory);

        Console.WriteLine($"Overlyric UI tests - run {runId}");
        Console.WriteLine($"  application  {options.ExePath}");
        Console.WriteLine($"  mode         {DescribeMode(options)}");

        var selected = ScenarioRegistry.Select(options.Filter);

        if (selected.Count == 0)
        {
            Console.Error.WriteLine($"error: no scenario matches '{options.Filter}'. Use --list to see the names.");
            return 1;
        }

        // Measure before anything is created, and again after the desktop is gone. These two
        // readings are the suite's proof that it did not steal focus, move the cursor, or write to
        // the user's instance; a run that changes either is a failed run, not a note in a log.
        var userDesktopBefore = ProductionGuard.CaptureUserDesktop();
        var productionBefore = ProductionGuard.CaptureProductionData();

        // --visible is a debugging aid, not a mode with the suite's guarantees: the windows appear on
        // the interactive desktop and the application calls SetForegroundWindow when it opens.
        if (options.Visible)
        {
            Console.Error.WriteLine(
                "warning: --visible runs the UI on your own desktop. Windows may take focus and appear over your work; " +
                "this mode is for watching a failing step, not for unattended runs.");
            Console.Error.WriteLine();
        }

        var desktopName = options.Visible ? string.Empty : $"OverlyricUiTests-{Environment.ProcessId}";
        var workerProblem = string.Empty;
        var workerPid = 0;
        RunResult? result;

        var workerArguments = new List<string>
        {
            "--worker", resultPath, artifactDirectory,
            "--desktop", desktopName,
            "--repo", options.RepoRoot,
            "--exe", options.ExePath
        };

        if (options.Update)
        {
            workerArguments.Add("--update");
        }

        if (options.Visible)
        {
            workerArguments.Add("--visible");
        }

        if (options.Filter.Length > 0)
        {
            workerArguments.Add("--filter");
            workerArguments.Add(options.Filter);
        }

        if (options.Repeat > 1)
        {
            workerArguments.Add("--repeat");
            workerArguments.Add(options.Repeat.ToString());
        }

        using var job = ProcessJob.TryCreate();

        if (job is null)
        {
            Console.Error.WriteLine(
                "note: the kill-on-close job object is unavailable here; a killed run may leave an invisible test instance behind.");
        }

        PrivateDesktop? desktop = options.Visible ? null : PrivateDesktop.Create(desktopName);

        try
        {
            var harnessExecutable = HarnessExecutable();

            try
            {
                using var worker = ChildProcess.Start(harnessExecutable, workerArguments, desktopName, null, options.RepoRoot);
                workerPid = worker.Pid;

                if (job is not null && !job.TryAdd(worker))
                {
                    Console.WriteLine("  note: the worker could not join the kill-on-close job; its cleanup relies on the normal path.");
                }

                Console.WriteLine(options.Visible
                    ? $"  desktop      the current desktop (--visible), worker pid {worker.Pid}"
                    : $"  desktop      '{desktopName}', worker pid {worker.Pid}");

                if (!worker.WaitForExit(options.WorkerTimeoutMs))
                {
                    workerProblem = $"the worker did not finish within {options.WorkerTimeoutMs / 1000}s and was terminated";
                    worker.KillTree();
                }
                else if (worker.ExitCode != 0)
                {
                    workerProblem = $"the worker exited with code {worker.ExitCode}";
                }
            }
            catch (Exception exception)
            {
                workerProblem = $"the worker could not be started: {exception.Message}";
            }
        }
        finally
        {
            desktop?.Dispose();
        }

        // A crashed worker can leave an app behind on the desktop it was on. The desktop handle is
        // gone now, so sweep only processes the worker itself started and only if they are the
        // application under test: the suite never kills anything by name otherwise.
        if (workerPid != 0)
        {
            ProcessLauncher.KillOrphanedChildren(workerPid, Path.GetFileName(options.ExePath));
        }

        result = Results.Read(resultPath);

        var desktopCheck = ProductionGuard.VerifyUserDesktop(userDesktopBefore);
        var safetyProblems = new List<string>(options.Visible ? [] : desktopCheck.Problems);
        var safetyNoteList = new List<string>(desktopCheck.Notes);

        if (options.Visible)
        {
            safetyNoteList.Add("visible mode: the windows were shown on your desktop by request, so the no-interruption check is disabled");
        }

        safetyProblems.AddRange(ProductionGuard.VerifyProductionData(productionBefore));

        PrintResult(
            options,
            selected.Count,
            result,
            workerProblem,
            workerLogPath,
            safetyProblems,
            safetyNoteList,
            productionBefore);
        Console.WriteLine();
        Console.WriteLine($"Artifacts: {artifactDirectory}");

        var expectationsMet = result is not null
            && result.Fatal is null
            && result.Scenarios.Count == selected.Count * options.Repeat
            && result.AllPassed
            && safetyProblems.Count == 0
            && workerProblem.Length == 0;

        return expectationsMet ? 0 : 1;
    }

    private static string DescribeMode(Options options)
    {
        var parts = new List<string> { options.Update ? "update baselines" : "compare against baselines" };

        if (options.Filter.Length > 0)
        {
            parts.Add($"filter '{options.Filter}'");
        }

        if (options.Repeat > 1)
        {
            parts.Add($"{options.Repeat} repeats");
        }

        return string.Join(", ", parts);
    }

    private static void PrintResult(
        Options options,
        int selectedScenarioCount,
        RunResult? result,
        string workerProblem,
        string workerLogPath,
        IReadOnlyList<string> safetyProblems,
        IReadOnlyList<string> safetyNotes,
        ProductionGuard.ProductionSnapshot productionBefore)
    {
        Console.WriteLine();

        if (result is null)
        {
            Console.WriteLine($"  FAIL  the worker produced no result file. {workerProblem}");
            PrintFileTail(workerLogPath, "worker log");
            PrintSafety(safetyProblems, safetyNotes, productionBefore);
            return;
        }

        foreach (var scenario in result.Scenarios)
        {
            var repeat = options.Repeat > 1 ? $" (repeat {scenario.Repeat})" : string.Empty;
            var captures = scenario.Steps.Count(step => step.CapturePath is not null);
            var skipped = scenario.Steps.Count(step => step.Status == Status.Skip);
            var skipNote = skipped > 0 ? $", {skipped} skipped" : string.Empty;

            Console.WriteLine(
                $"  {scenario.Status,-4} {scenario.Name,-26} {scenario.DurationMs / 1000.0,6:F2}s{repeat,-12} {captures,2} capture(s){skipNote}");

            foreach (var step in scenario.Steps.Where(step => step.Status == Status.Skip))
            {
                Console.WriteLine($"         SKIP {step.Name}: {step.Message}");
            }

            if (scenario.Status == Status.Pass)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(scenario.Message))
            {
                foreach (var line in scenario.Message.Split(Environment.NewLine))
                {
                    Console.WriteLine($"         {line}");
                }
            }

            foreach (var step in scenario.Steps.Where(step => step.Status == Status.Fail))
            {
                Console.WriteLine($"         step: {step.Name}");
                Console.WriteLine($"           {step.Message}");
            }
        }

        var expectedRuns = selectedScenarioCount * options.Repeat;

        if (result.Scenarios.Count != expectedRuns)
        {
            Console.WriteLine($"  FAIL  {result.Scenarios.Count} of {expectedRuns} scenario runs reported. {workerProblem}");
        }
        else if (result.Fatal is not null)
        {
            Console.WriteLine($"  FAIL  the worker reported a fatal error:");
            Console.WriteLine($"        {result.Fatal}");
        }
        else if (workerProblem.Length > 0)
        {
            Console.WriteLine($"  FAIL  {workerProblem}");
        }

        if (result.Fatal is not null)
        {
            PrintFileTail(workerLogPath, "worker log");
        }

        PrintSafety(safetyProblems, safetyNotes, productionBefore);

        if (options.Update && safetyProblems.Count == 0)
        {
            Console.WriteLine($"Baselines: {Path.Combine(options.BaselinesDirectory)}");
        }
    }

    private static void PrintSafety(
        IReadOnlyList<string> problems,
        IReadOnlyList<string> notes,
        ProductionGuard.ProductionSnapshot productionBefore)
    {
        Console.WriteLine();

        if (problems.Count == 0)
        {
            Console.WriteLine($"Safety: production data folder unchanged ({ProductionGuard.Describe(productionBefore)});");
            Console.WriteLine("        user foreground window and cursor unchanged.");

            foreach (var note in notes)
            {
                Console.WriteLine($"        note: {note}");
            }

            return;
        }

        Console.WriteLine("Safety: FAILED");

        foreach (var problem in problems)
        {
            Console.WriteLine($"        - {problem}");
        }
    }

    private static void PrintFileTail(string path, string label)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            Console.WriteLine($"        {label} ({path}):");
            var lines = File.ReadAllLines(path);
            var tail = lines.Length <= 25 ? lines : lines[^25..];

            foreach (var line in tail)
            {
                Console.WriteLine($"          {line}");
            }
        }
        catch (Exception)
        {
            // A missing log is not worth a second failure.
        }
    }

    /// <summary>
    /// The executable to relaunch as the worker. <see cref="Environment.ProcessPath"/> is the apphost
    /// when the suite is started normally; the fallback covers being hosted by dotnet.exe.
    /// </summary>
    private static string HarnessExecutable()
    {
        var processPath = Environment.ProcessPath;

        if (processPath is not null
            && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "Overlyric.UiTests.exe");

        if (!File.Exists(fallback))
        {
            throw new InvalidOperationException(
                "Could not resolve the harness executable to relaunch as the worker; start the built Overlyric.UiTests.exe.");
        }

        return fallback;
    }

    // --- Worker ----------------------------------------------------------------------------------

    private static int RunWorker(Options options)
    {
        Directory.CreateDirectory(options.ArtifactDirectory);

        // Everything the worker says goes to a log file: a process on a private desktop has no
        // visible console, and a failed run has to be diagnosable afterwards.
        using var log = new StreamWriter(
            Path.Combine(options.ArtifactDirectory, "worker.log"),
            append: false,
            Encoding.UTF8)
        {
            AutoFlush = true
        };

        Console.SetOut(log);
        Console.SetError(log);

        var result = new RunResult
        {
            RunId = Path.GetFileName(options.ArtifactDirectory),
            ArtifactDirectory = options.ArtifactDirectory,
            StartedAt = DateTimeOffset.Now.ToString("o"),
            Update = options.Update,
            Repeat = options.Repeat,
            Filter = options.Filter
        };

        var productionBefore = ProductionGuard.CaptureProductionData();
        var scenarios = ScenarioRegistry.Select(options.Filter);

        try
        {
            if (scenarios.Count == 0)
            {
                throw new InvalidOperationException($"No scenario matches '{options.Filter}'.");
            }

            for (var repeat = 1; repeat <= options.Repeat; repeat++)
            {
                foreach (var scenario in scenarios)
                {
                    result.Scenarios.Add(RunScenario(options, scenario, repeat, log));
                }
            }
        }
        catch (Exception exception)
        {
            result.Fatal = exception.ToString();
            log.WriteLine("fatal: " + result.Fatal);
        }
        finally
        {
            result.SafetyProblems.AddRange(ProductionGuard.VerifyProductionData(productionBefore));
            result.FinishedAt = DateTimeOffset.Now.ToString("o");
            Results.Write(options.ResultPath, result);
        }

        return result.AllPassed ? 0 : 1;
    }

    private static ScenarioResult RunScenario(Options options, IScenario scenario, int repeat, TextWriter log)
    {
        var result = new ScenarioResult { Name = scenario.Name, Repeat = repeat };
        var workDirectory = Path.Combine(options.ArtifactDirectory, scenario.Name, $"repeat-{repeat}");
        Directory.CreateDirectory(workDirectory);

        var context = new ScenarioContext(options, scenario.Name, repeat, result, workDirectory, log);
        var stopwatch = Stopwatch.StartNew();

        log.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {scenario.Name} repeat {repeat} start");

        try
        {
            scenario.Run(context);
            result.Status = Status.Pass;
        }
        catch (ScenarioFailureException exception)
        {
            result.Status = Status.Fail;
            result.Message = exception.Message;
            log.WriteLine($"{scenario.Name}: FAIL {exception.Message}");
        }
        catch (Exception exception)
        {
            result.Status = Status.Fail;
            result.Message = $"{exception.GetType().Name}: {exception.Message}";
            log.WriteLine($"{scenario.Name}: FAIL {exception}");
        }

        result.DurationMs = stopwatch.ElapsedMilliseconds;
        log.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {scenario.Name} repeat {repeat} {result.Status} in {result.DurationMs} ms");

        return result;
    }

    // --- Usage -----------------------------------------------------------------------------------

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Overlyric UI tests - drives the settings window and the demo overlay on a private desktop.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  Overlyric.UiTests.exe [run] [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --update            capture the app and rewrite tools/uitest/baselines instead of comparing");
        writer.WriteLine("  --filter <text>     run only the scenarios whose name contains <text> (case-insensitive)");
        writer.WriteLine("  --visible           run on your own desktop so a failing step can be watched; debugging only,");
        writer.WriteLine("                      the no-interruption guarantee does not apply");
        writer.WriteLine("  --repeat <n>        run the selected scenarios n times; repeats after the first compare, which");
        writer.WriteLine("                      is how --update proves its own baselines are deterministic");
        writer.WriteLine("  --exe <path>        application under test (default: src/Overlyric/bin/Debug/net10.0-");
        writer.WriteLine("                      windows10.0.26100.0/win-x64/Overlyric.exe)");
        writer.WriteLine("  --repo <path>       repository root (default: discovered by walking up for Overlyric.slnx)");
        writer.WriteLine("  --list              list the scenarios and exit");
        writer.WriteLine("  --help              this text");
        writer.WriteLine();
        writer.WriteLine("Exit code: 0 when every scenario passes and the user's desktop and data were left alone, 1 otherwise.");
        writer.WriteLine();
        writer.WriteLine("Every run gets a private desktop, a private instance namespace and a data root under");
        writer.WriteLine(".dsh-tmp/uitest/<runId>/; the artifact directory is printed at the end.");
    }
}
