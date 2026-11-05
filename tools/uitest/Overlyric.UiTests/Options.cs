namespace Overlyric.UiTests;

/// <summary>
/// Everything a run needs to know. The parent and the worker share this type because the worker is
/// the same executable relaunched with extra positional arguments; the parent forwards the flags so
/// there is exactly one parser and one interpretation of them.
/// </summary>
internal sealed class Options
{
    public const string FixtureRelativePath = "tools/uitest/fixtures/settings.json";

    public const string BaselinesRelativePath = "tools/uitest/baselines";

    public const string ArtifactsRelativePath = ".dsh-tmp/uitest";

    /// <summary>Replaces the capture baselines instead of comparing against them.</summary>
    public bool Update { get; private set; }

    public bool List { get; private set; }

    public bool Help { get; private set; }

    /// <summary>
    /// Run on the current desktop instead of a private one, so a failing step can be watched. Debug
    /// only: the windows appear over the user's work, the application calls SetForegroundWindow, and
    /// the no-interruption guarantee does not apply.
    /// </summary>
    public bool Visible { get; private set; }

    /// <summary>Case-insensitive substring filter over scenario names; empty runs everything.</summary>
    public string Filter { get; private set; } = string.Empty;

    /// <summary>How many times to run the selected scenarios. The second pass proves determinism.</summary>
    public int Repeat { get; private set; } = 1;

    /// <summary>Worker mode: this process runs the scenarios on the private desktop.</summary>
    public bool Worker { get; private set; }

    public string ResultPath { get; private set; } = string.Empty;

    public string ArtifactDirectory { get; private set; } = string.Empty;

    /// <summary>The private desktop the parent created; the worker passes it to every app it starts.</summary>
    public string DesktopName { get; private set; } = string.Empty;

    public string RepoRoot { get; private set; } = string.Empty;

    public string ExePath { get; private set; } = string.Empty;

    public string FixturePath => Path.Combine(RepoRoot, FixtureRelativePath);

    public string BaselinesDirectory => Path.Combine(RepoRoot, BaselinesRelativePath);

    /// <summary>How long the parent waits for the worker before killing it, in milliseconds.</summary>
    public int WorkerTimeoutMs => Math.Min(900_000, 180_000 + ((Repeat - 1) * 90_000));

    public static Options Parse(string[] args)
    {
        var options = new Options();
        string? repo = null;
        string? exe = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            if (argument.Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (name, inlineValue) = Split(argument);

            switch (name)
            {
                case "--update":
                    options.Update = true;
                    break;

                case "--visible":
                    options.Visible = true;
                    break;

                case "--list":
                    options.List = true;
                    break;

                case "--help":
                case "-h":
                case "-?":
                    options.Help = true;
                    break;

                case "--filter":
                    options.Filter = inlineValue ?? Take(args, ref i, name);
                    break;

                case "--repeat":
                    if (!int.TryParse(inlineValue ?? Take(args, ref i, name), out var repeat) || repeat < 1 || repeat > 10)
                    {
                        throw new ArgumentException("--repeat needs a whole number between 1 and 10.");
                    }

                    options.Repeat = repeat;
                    break;

                case "--exe":
                    exe = inlineValue ?? Take(args, ref i, name);
                    break;

                case "--repo":
                    repo = inlineValue ?? Take(args, ref i, name);
                    break;

                case "--desktop":
                    options.DesktopName = inlineValue ?? Take(args, ref i, name);
                    break;

                case "--worker":
                    options.Worker = true;
                    options.ResultPath = inlineValue ?? Take(args, ref i, name);
                    options.ArtifactDirectory = Take(args, ref i, "--worker artifact directory");
                    break;

                default:
                    throw new ArgumentException($"Unknown argument '{argument}'. Use --help for the accepted ones.");
            }
        }

        options.RepoRoot = repo is null ? RepoLocator.Find() : Path.GetFullPath(repo);

        if (!File.Exists(Path.Combine(options.RepoRoot, "Overlyric.slnx")))
        {
            throw new ArgumentException(
                $"'{options.RepoRoot}' does not look like the Overlyric repository (no Overlyric.slnx). Pass --repo <path>.");
        }

        options.ExePath = exe is null
            ? Path.Combine(options.RepoRoot, "src/Overlyric/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Overlyric.exe")
            : Path.GetFullPath(exe);

        if (!File.Exists(options.ExePath))
        {
            throw new ArgumentException(
                $"The application under test was not found at '{options.ExePath}'. Build it first: dotnet build src/Overlyric/Overlyric.csproj -c Debug (or pass --exe).");
        }

        if (!File.Exists(options.FixturePath))
        {
            throw new ArgumentException($"The settings fixture is missing at '{options.FixturePath}'.");
        }

        return options;
    }

    private static (string Name, string? Value) Split(string argument)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown argument '{argument}'.");
        }

        var equals = argument.IndexOf('=');

        return equals < 0
            ? (argument, null)
            : (argument[..equals], argument[(equals + 1)..]);
    }

    private static string Take(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{name} needs a value.");
        }

        return args[++index];
    }

    /// <summary>Walks up from the executable looking for the repository root.</summary>
    internal static class RepoLocator
    {
        public static string Find()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                var directory = new DirectoryInfo(start);

                for (var depth = 0; depth < 12 && directory is not null; depth++)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "Overlyric.slnx")))
                    {
                        return directory.FullName;
                    }

                    directory = directory.Parent;
                }
            }

            throw new ArgumentException(
                "Could not find the Overlyric repository root from the harness executable. Pass --repo <path>.");
        }
    }
}
