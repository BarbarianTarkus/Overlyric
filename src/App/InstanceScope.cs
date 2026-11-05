namespace Overlyric.App;

/// <summary>The three names one Overlyric instance owns: its mutex, its command pipe and its data folder.</summary>
internal readonly record struct InstanceNames(string MutexName, string PipeName, string DataDirectory);

/// <summary>
/// The namespace one Overlyric instance owns. A normal launch gets the names and folder the user
/// already has. A test launch sets <c>OVERLYRIC_TEST_INSTANCE</c> to a short name, which moves the
/// mutex, the command pipe and the data folder into a namespace of their own, so a test copy can run
/// beside the real application without forwarding its commands to it, reading its settings, or
/// fighting it for the single-instance mutex.
/// </summary>
/// <remarks>
/// The name ends up in a kernel object name and in a folder beneath %LOCALAPPDATA%, so it is limited
/// to letters, digits, hyphens and underscores. An invalid value is reported to the caller instead of
/// falling back to the production namespace, because a test that silently used the real instance
/// would be worse than one that refused to start.
/// </remarks>
internal static class InstanceScope
{
    public const string VariableName = "OVERLYRIC_TEST_INSTANCE";

    /// <summary>
    /// Where a test instance keeps its data. Defaults to %LOCALAPPDATA%; the UI tests point it at a
    /// folder inside the repository so their config, cache and logs never reach the user's profile.
    /// </summary>
    public const string DataRootVariable = "OVERLYRIC_TEST_DATA_ROOT";

    private const string DefaultMutexName = "Overlyric.SingleInstance.v1";
    private const string DefaultPipeName = "Overlyric.Commands.v1";
    private const string DefaultFolderName = "Overlyric";
    private const string TestFolderName = "Overlyric-Test";
    private const int MaxNameLength = 32;

    /// <summary>The names this process was started with. Valid even when <see cref="Error"/> is set.</summary>
    public static InstanceNames Current { get; }

    /// <summary>True when this launch asked for a test namespace, or null when the launch is invalid.</summary>
    public static bool IsTestInstance { get; }

    /// <summary>Why a requested test instance name was refused, or null when the launch is valid.</summary>
    public static string? Error { get; }

    static InstanceScope()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rawName = Environment.GetEnvironmentVariable(VariableName);

        // The data root is a test-only relocation. A production launch ignores it even if it is set,
        // so a stray environment variable can never point the real application at another folder.
        var dataRoot = localApplicationData;
        if (!string.IsNullOrWhiteSpace(rawName))
        {
            var requestedRoot = Environment.GetEnvironmentVariable(DataRootVariable);
            if (!string.IsNullOrWhiteSpace(requestedRoot))
            {
                dataRoot = requestedRoot;
            }
        }

        if (TryResolve(rawName, dataRoot, out var names, out var error))
        {
            Current = names;
            IsTestInstance = !string.IsNullOrWhiteSpace(rawName);
            return;
        }

        // Current must still be something, because the diagnostic about the bad name is written
        // through the normal data folder before the process exits.
        Error = error;
        Current = Default(localApplicationData);
    }

    /// <summary>Resolves a raw environment value into the names one instance would own.</summary>
    internal static bool TryResolve(string? rawName, string localApplicationData, out InstanceNames names, out string? error)
    {
        names = Default(localApplicationData);
        error = null;

        var name = rawName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return true;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"{VariableName} must be at most {MaxNameLength} characters long.";
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                error = $"{VariableName} may only contain letters, digits, hyphens and underscores.";
                return false;
            }
        }

        names = new InstanceNames(
            $"{DefaultMutexName}.{name}",
            $"{DefaultPipeName}.{name}",
            Path.Combine(localApplicationData, TestFolderName, name));
        return true;
    }

    private static InstanceNames Default(string localApplicationData) => new(
        DefaultMutexName,
        DefaultPipeName,
        Path.Combine(localApplicationData, DefaultFolderName));
}
