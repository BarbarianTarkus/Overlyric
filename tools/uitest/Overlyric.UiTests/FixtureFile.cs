using System.Text.Json;

namespace Overlyric.UiTests;

/// <summary>
/// The checked-in settings fixture: a complete <c>config.json</c> with fixed values, so every
/// section draws the same thing on every machine and the baselines only move when the UI moves.
/// </summary>
/// <remarks>
/// The fixture is also the expectation: the toggle and stepper scenarios read the value they expect
/// back out of it instead of hard-coding a second copy, because two copies of "30" drift apart.
/// </remarks>
internal static class FixtureFile
{
    public static string ReadText(Options options) => File.ReadAllText(options.FixturePath);

    public static bool ReadBool(Options options, string propertyName) =>
        Read(options, propertyName).ValueKind == JsonValueKind.True;

    public static double ReadNumber(Options options, string propertyName) =>
        Read(options, propertyName).GetDouble();

    /// <summary>Copies the fixture to the instance's config path, creating the folder chain.</summary>
    public static void WriteConfig(Options options, string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, ReadText(options));
    }

    private static JsonElement Read(Options options, string propertyName)
    {
        using var document = JsonDocument.Parse(ReadText(options));

        if (!document.RootElement.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidOperationException(
                $"The fixture '{options.FixturePath}' does not define '{propertyName}'.");
        }

        return value.Clone();
    }
}
