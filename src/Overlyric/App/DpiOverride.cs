using System.Globalization;

namespace Overlyric.App;

/// <summary>
/// A test-only fixed render DPI. The UI tests capture windows and compare them against committed
/// baselines, so the layout has to be identical on every machine; the host's monitor scaling would
/// otherwise move every pixel. <c>OVERLYRIC_TEST_DPI</c> makes each window lay out at the value it
/// names instead of the monitor's DPI, and 96 gives a capture the size the baselines were taken at.
/// </summary>
/// <remarks>
/// Read once, and only in a test instance. A production launch ignores the variable even when it is
/// set, for the same reason the other test seams do: a stray environment value must never change
/// what the user sees. A value outside 96..480 is treated as unset rather than rounded, because a
/// typo should fail a test's own size checks, not silently render at a plausible-looking scale.
/// </remarks>
internal static class DpiOverride
{
    public const string VariableName = "OVERLYRIC_TEST_DPI";

    private const int Minimum = 96;
    private const int Maximum = 480;

    /// <summary>The forced DPI, or null when this launch draws at the monitor's real DPI.</summary>
    public static float? Value { get; }

    static DpiOverride()
    {
        if (!InstanceScope.IsTestInstance)
        {
            return;
        }

        Value = Parse(Environment.GetEnvironmentVariable(VariableName));
    }

    /// <summary>Parses one raw environment value. Anything that is not a plain integer in range means "no override".</summary>
    internal static float? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Invariant and integer-only: a decimal separator would be read differently under a comma
        // locale, and a fractional DPI is not something the seam promises.
        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return value is >= Minimum and <= Maximum ? value : (float?)null;
    }

    /// <summary>The DPI to draw at: the value a test asked for, or the one the window resolved.</summary>
    public static float For(float actual) => Value ?? actual;
}
