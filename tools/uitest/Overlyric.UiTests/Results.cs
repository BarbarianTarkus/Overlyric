using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlyric.UiTests;

internal static class Status
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Skip = "SKIP";
}

/// <summary>One checked step inside a scenario: a capture compared to a baseline, or an assertion.</summary>
internal sealed class StepResult
{
    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = Overlyric.UiTests.Status.Fail;

    public string? Message { get; set; }

    /// <summary>Where the capture of this step was written, when it captured anything.</summary>
    public string? CapturePath { get; set; }
}

/// <summary>One scenario's outcome. Scenarios are independent processes, so this is the unit of isolation.</summary>
internal sealed class ScenarioResult
{
    public string Name { get; set; } = string.Empty;

    public int Repeat { get; set; } = 1;

    public string Status { get; set; } = Overlyric.UiTests.Status.Fail;

    public string? Message { get; set; }

    public long DurationMs { get; set; }

    public string? InstanceName { get; set; }

    public string? DataRoot { get; set; }

    /// <summary>The OS DPI the window was on, recorded so a size failure can be diagnosed.</summary>
    public string? DpiNote { get; set; }

    public List<StepResult> Steps { get; set; } = [];
}

/// <summary>The worker's whole report, and the only thing the parent reads back.</summary>
internal sealed class RunResult
{
    public string RunId { get; set; } = string.Empty;

    public string ArtifactDirectory { get; set; } = string.Empty;

    public string StartedAt { get; set; } = string.Empty;

    public string FinishedAt { get; set; } = string.Empty;

    public bool Update { get; set; }

    public int Repeat { get; set; } = 1;

    public string Filter { get; set; } = string.Empty;

    public string? Fatal { get; set; }

    public List<string> SafetyProblems { get; set; } = [];

    public List<ScenarioResult> Scenarios { get; set; } = [];

    public bool AllPassed =>
        Fatal is null
        && SafetyProblems.Count == 0
        && Scenarios.Count > 0
        && Scenarios.TrueForAll(scenario => scenario.Status == Status.Pass);
}

internal static class Results
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(string path, RunResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, SerializerOptions));
    }

    public static RunResult? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunResult>(File.ReadAllText(path), SerializerOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Thrown when a step's expectation is not met; the runner turns it into a FAIL line.</summary>
internal sealed class ScenarioFailureException(string message) : Exception(message);
