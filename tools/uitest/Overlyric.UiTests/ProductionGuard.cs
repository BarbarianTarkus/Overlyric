using System.Security.Cryptography;
using System.Text;

namespace Overlyric.UiTests;

/// <summary>
/// Proof that the suite left the user's instance and desktop alone.
/// </summary>
/// <remarks>
/// The suite runs a second copy of the application, so "it did not touch the user's data" is a claim
/// that has to be measured, not asserted. The production folder is identified by the same path the
/// application uses (no test seams), and it is snapshotted before and after: any change to
/// config.json or to the folder's entries fails the run. The user's desktop is measured too - the
/// foreground window and the cursor position - because posting input to a private desktop is only
/// useful if it provably did not move either of them.
/// </remarks>
internal static class ProductionGuard
{
    /// <summary>One reading of the production data folder.</summary>
    public sealed record ProductionSnapshot(
        bool Exists,
        string? ConfigHash,
        long ConfigLength,
        long ConfigLastWriteTicks,
        IReadOnlyDictionary<string, string> Entries)
    {
        public static ProductionSnapshot Empty { get; } = new(false, null, 0, 0, new Dictionary<string, string>());
    }

    /// <summary>One reading of the user's interactive desktop.</summary>
    public sealed record UserDesktopState(nint ForegroundWindow, int CursorX, int CursorY, uint LastInputTick);

    /// <summary>What the desktop check found: hard failures, and observations that are explained.</summary>
    public sealed record DesktopCheck(IReadOnlyList<string> Problems, IReadOnlyList<string> Notes);

    public static string ProductionDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Overlyric");

    public static string ProductionConfigPath => Path.Combine(ProductionDataDirectory, "config.json");

    public static UserDesktopState CaptureUserDesktop()
    {
        NativeMethods.GetCursorPos(out var point);
        return new UserDesktopState(
            NativeMethods.GetForegroundWindow(),
            point.X,
            point.Y,
            NativeMethods.LastInputTick());
    }

    /// <summary>
    /// The user's foreground window and cursor must be exactly where they were when the run started.
    /// </summary>
    /// <remarks>
    /// The one thing that makes this check trustworthy rather than flaky is
    /// <c>GetLastInputInfo</c>: a person using the machine while the suite runs moves the cursor and
    /// changes the foreground window themselves, and blaming the harness for that would train
    /// everyone to ignore the check. So a change is a failure only when the session recorded no
    /// input event in the same window of time - no user input plus a changed desktop means something
    /// synthetic touched it, and the suite has no call that could have been the harness.
    /// </remarks>
    public static DesktopCheck VerifyUserDesktop(UserDesktopState before)
    {
        // Two readings: a running console or a settling menu can change the foreground for a single
        // instant, and a change that has already settled is not a change the run caused.
        Thread.Sleep(120);
        var after = CaptureUserDesktop();

        if (SameDesktop(after, before))
        {
            return new DesktopCheck([], []);
        }

        Thread.Sleep(200);
        var confirm = CaptureUserDesktop();

        if (SameDesktop(confirm, before))
        {
            return new DesktopCheck([], []);
        }

        var userInput = unchecked(confirm.LastInputTick - before.LastInputTick) != 0
            && before.LastInputTick != 0;

        var changes = new List<string>();

        if (confirm.ForegroundWindow != before.ForegroundWindow)
        {
            changes.Add($"the user's foreground window changed from 0x{before.ForegroundWindow:X} to 0x{confirm.ForegroundWindow:X} during the run");
        }

        if (confirm.CursorX != before.CursorX || confirm.CursorY != before.CursorY)
        {
            changes.Add($"the cursor moved from ({before.CursorX},{before.CursorY}) to ({confirm.CursorX},{confirm.CursorY}) during the run");
        }

        if (userInput)
        {
            return new DesktopCheck(
                [],
                [.. changes.Select(change => change + " (Windows recorded user input in that window, so this is the user, not the suite)")]);
        }

        return new DesktopCheck(changes, []);
    }

    private static bool SameDesktop(UserDesktopState left, UserDesktopState right) =>
        left.ForegroundWindow == right.ForegroundWindow
        && left.CursorX == right.CursorX
        && left.CursorY == right.CursorY;

    public static ProductionSnapshot CaptureProductionData()
    {
        var directory = ProductionDataDirectory;

        try
        {
            if (!Directory.Exists(directory))
            {
                return ProductionSnapshot.Empty;
            }

            // Membership only, and deliberately not content: the production application is allowed
            // to be running while the suite runs, and it appends to its own diagnostics log and
            // refreshes its cache by itself. Comparing every entry's size and timestamp would fail
            // the safety check for the user's activity. What must not change is which names exist;
            // config.json is the one entry whose content is also pinned, because a test instance
            // leaking into the real namespace is exactly the mistake worth failing a run over.
            var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                entries[Path.GetFileName(entry)] = File.Exists(entry) ? "file" : "directory";
            }

            var configPath = Path.Combine(directory, "config.json");
            var configHash = File.Exists(configPath) ? Hash(configPath) : null;
            var length = 0L;
            var ticks = 0L;

            if (File.Exists(configPath))
            {
                var info = new FileInfo(configPath);
                length = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }

            return new ProductionSnapshot(true, configHash, length, ticks, entries);
        }
        catch (Exception exception)
        {
            // An unreadable production folder is not something to fail over silently; recording the
            // error as an entry makes the comparison itself report the difference.
            return new ProductionSnapshot(
                true,
                null,
                0,
                0,
                new Dictionary<string, string> { ["<unreadable>"] = exception.GetType().Name });
        }
    }

    public static IReadOnlyList<string> VerifyProductionData(ProductionSnapshot before)
    {
        var after = CaptureProductionData();
        var problems = new List<string>();

        if (before.Exists != after.Exists)
        {
            problems.Add(before.Exists
                ? $"the production data folder '{ProductionDataDirectory}' disappeared during the run"
                : $"the production data folder '{ProductionDataDirectory}' was created during the run");
            return problems;
        }

        if (before.ConfigHash != after.ConfigHash || before.ConfigLength != after.ConfigLength)
        {
            problems.Add($"the production config '{ProductionConfigPath}' changed during the run");
        }

        foreach (var (name, state) in before.Entries)
        {
            if (!after.Entries.TryGetValue(name, out var now))
            {
                problems.Add($"'{name}' was removed from '{ProductionDataDirectory}' during the run");
            }
            else if (now != state)
            {
                problems.Add($"'{name}' in '{ProductionDataDirectory}' changed kind during the run (was {state}, now {now})");
            }
        }

        foreach (var name in after.Entries.Keys)
        {
            if (!before.Entries.ContainsKey(name))
            {
                problems.Add($"'{name}' was created in '{ProductionDataDirectory}' during the run");
            }
        }

        return problems;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Describe(ProductionSnapshot snapshot)
    {
        var builder = new StringBuilder();

        builder.Append(snapshot.Exists ? "present" : "absent");

        if (snapshot.ConfigHash is not null)
        {
            builder.Append(", config sha256 ").Append(snapshot.ConfigHash[..16]).Append('…');
        }

        return builder.ToString();
    }
}
