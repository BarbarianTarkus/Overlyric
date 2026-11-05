using System.Globalization;

using Overlyric.App;

namespace Overlyric.UiSeamsTests;

/// <summary>
/// Checks the two test seams that let an automated run stay out of the user's way: which environment
/// value selects the production instance names, what a test instance turns into, which values are
/// refused, and how the test-only fixed DPI is parsed and applied.
/// </summary>
internal static class Program
{
    private const string LocalApplicationData = @"C:\Users\test\AppData\Local";

    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        ProductionChecks();
        TestInstanceChecks();
        DpiChecks();
        RefusalChecks();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void ProductionChecks()
    {
        Check("no value keeps the production mutex", Resolve(null).MutexName == "Overlyric.SingleInstance.v1", Resolve(null).MutexName);
        Check("no value keeps the production pipe", Resolve(null).PipeName == "Overlyric.Commands.v1", Resolve(null).PipeName);
        Check("no value keeps %LOCALAPPDATA%\\Overlyric",
            Resolve(null).DataDirectory == Path.Combine(LocalApplicationData, "Overlyric"),
            Resolve(null).DataDirectory);
        Check("an empty value is a normal launch", Resolve("").MutexName == "Overlyric.SingleInstance.v1", Resolve("").MutexName);
        Check("a whitespace value is a normal launch", Resolve("   ").MutexName == "Overlyric.SingleInstance.v1", Resolve("   ").MutexName);
    }

    private static void TestInstanceChecks()
    {
        var scope = Resolve("uitest-1");
        Check("a test name moves the mutex", scope.MutexName == "Overlyric.SingleInstance.v1.uitest-1", scope.MutexName);
        Check("a test name moves the pipe", scope.PipeName == "Overlyric.Commands.v1.uitest-1", scope.PipeName);
        Check("a test name moves the folder",
            scope.DataDirectory == Path.Combine(LocalApplicationData, "Overlyric-Test", "uitest-1"),
            scope.DataDirectory);
        Check("surrounding whitespace is trimmed", Resolve("  stream-check_2  ").MutexName.EndsWith(".stream-check_2", StringComparison.Ordinal), Resolve("  stream-check_2  ").MutexName);
        Check("digits, hyphens and underscores are accepted", Resolve("A1-b_2").MutexName == "Overlyric.SingleInstance.v1.A1-b_2", Resolve("A1-b_2").MutexName);
        Check("the longest allowed name is accepted", Resolve(new string('a', 32)).MutexName.Length == "Overlyric.SingleInstance.v1.".Length + 32, "");
        Check("a test instance never shares the production names",
            Resolve("x").MutexName != Resolve(null).MutexName
            && Resolve("x").PipeName != Resolve(null).PipeName
            && Resolve("x").DataDirectory != Resolve(null).DataDirectory, "");
        Check("two test names are distinct namespaces", Resolve("one").DataDirectory != Resolve("two").DataDirectory, "");
        Check("the data root decides where a test instance writes",
            InstanceScope.TryResolve("run", Path.Combine(LocalApplicationData, "custom-root"), out var rooted, out _)
            && rooted.DataDirectory == Path.Combine(LocalApplicationData, "custom-root", "Overlyric-Test", "run"),
            "");
    }

    private static void DpiChecks()
    {
        ParseCheck("an absent value is no override", null, null);
        ParseCheck("an empty value is no override", "", null);
        ParseCheck("a whitespace value is no override", "   ", null);
        ParseCheck("a non-numeric value is no override", "abc", null);
        ParseCheck("a value below 96 is refused", "95", null);
        ParseCheck("a value above 480 is refused", "481", null);
        ParseCheck("96 is accepted", "96", 96f);
        ParseCheck("144 is accepted", "144", 144f);
        ParseCheck("480 is accepted", "480", 480f);
        ParseCheck("surrounding whitespace is tolerated", " 144 ", 144f);
        ParseCheck("a fractional value is refused", "96.0", null);
        ParseCheck("a signed value is refused", "+96", null);

        // The probe is a normal launch, so no override is in force and For() must hand back the DPI
        // the window resolved. A failure here means the probe itself was started inside a test
        // instance, and the pass-through contract cannot be observed from it.
        var actual = DpiOverride.For(240f);
        Check("a real DPI passes through when no test value is set", actual == 240f, Show(actual));
    }

    private static void RefusalChecks()
    {
        Refused("a slash", "bad/name");
        Refused("a backslash", @"bad\name");
        Refused("a dot", "bad.name");
        Refused("a space", "bad name");
        Refused("a colon", "bad:name");
        Refused("path traversal", "..");
        Refused("a name one over the limit", new string('a', 33));
    }

    private static void Refused(string label, string? rawName)
    {
        var ok = InstanceScope.TryResolve(rawName, LocalApplicationData, out _, out var error);
        Check($"{label} is refused", !ok && !string.IsNullOrWhiteSpace(error), error ?? "no error");
        Check($"{label} is refused with a readable reason", error is not null && error.Contains(InstanceScope.VariableName, StringComparison.Ordinal), error ?? "no error");
    }

    private static InstanceNames Resolve(string? rawName)
    {
        if (!InstanceScope.TryResolve(rawName, LocalApplicationData, out var names, out var error))
        {
            throw new InvalidOperationException($"expected '{rawName}' to resolve: {error}");
        }

        return names;
    }

    private static void ParseCheck(string label, string? raw, float? expected)
    {
        var actual = DpiOverride.Parse(raw);
        Check(label, actual == expected, Show(actual));
    }

    private static string Show(float? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static void Check(string label, bool passed, string actual)
    {
        _checks++;
        Console.WriteLine(passed ? $"  ok   {label}" : $"  FAIL {label} (got: {actual})");
        if (!passed)
        {
            _failures++;
        }
    }
}
