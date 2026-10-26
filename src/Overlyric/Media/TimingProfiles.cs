using System.Globalization;

namespace Overlyric.Media;

/// <summary>Which layer supplied the timing correction currently in force.</summary>
public enum TimingSource
{
    /// <summary>The global <c>LyricsOffsetMs</c>, the fallback for everything.</summary>
    Global = 0,

    /// <summary>A correction saved for the current output endpoint.</summary>
    Output = 1,

    /// <summary>A correction saved for the current track; the highest-priority layer.</summary>
    Track = 2,

    /// <summary>A correction made for this session only, not persisted.</summary>
    Session = 3
}

/// <summary>An offset in milliseconds together with the layer that supplied it.</summary>
public readonly record struct TimingResolution(int OffsetMs, TimingSource Source);

/// <summary>One saved output-device correction. The name is refreshed when the profile is used.</summary>
internal sealed class OutputOffsetProfile
{
    public string Name { get; set; } = "";

    public int OffsetMs { get; set; }

    /// <summary>Whether the endpoint was Bluetooth when the correction was saved.</summary>
    public bool Bluetooth { get; set; }
}

/// <summary>
/// The resolution order for the lyric timing correction, and the wording that names its source.
/// </summary>
/// <remarks>
/// <para>
/// The layers are <em>overrides</em>, never additive terms: a track correction replaces the device
/// profile, and the device profile replaces the global default. Summing them would double-correct as
/// soon as the same track moved to another output, and there would be no sentence that explains the
/// resulting number.
/// </para>
/// <para>
/// The type is deliberately free of Win32 and of <c>AppConfig</c>: the order, the clamping and the
/// labels are the part that can be wrong silently, so they are checked by <c>probe/TimingTests</c>
/// without a desktop or an audio endpoint.
/// </para>
/// </remarks>
internal static class TimingProfiles
{
    public const int MinOffsetMs = -10_000;

    public const int MaxOffsetMs = 10_000;

    /// <summary>Enough saved tracks for months of listening without letting a hand-edited file grow.</summary>
    public const int MaxTracks = 512;

    /// <summary>Machine-level cap: a handful of outputs is the real-world case.</summary>
    public const int MaxOutputs = 32;

    private const char KeySeparator = '\u001f';

    public static int Clamp(int offsetMs) => Math.Clamp(offsetMs, MinOffsetMs, MaxOffsetMs);

    /// <summary>
    /// A stable, case- and whitespace-insensitive key for one performance. The album and duration are
    /// deliberately not part of it: the same performance is reported with different album tags by
    /// different players and editions, and a correction that vanished on a tag edit would be worse
    /// than two records sharing one offset. Null means the metadata has nothing to key on.
    /// </summary>
    public static string? TrackKey(string? artist, string? title)
    {
        var titlePart = KeyPart(title);
        var artistPart = KeyPart(artist);

        if (titlePart.Length == 0 && artistPart.Length == 0)
        {
            return null;
        }

        return artistPart + KeySeparator + titlePart;
    }

    /// <summary>
    /// track override -> output profile -> global default. A stored zero is still a stored override,
    /// because it is the value the user chose; only the absence of an entry falls through.
    /// </summary>
    public static TimingResolution Resolve(
        int globalOffsetMs,
        IReadOnlyDictionary<string, int>? trackOffsets,
        string? trackKey,
        IReadOnlyDictionary<string, OutputOffsetProfile>? outputOffsets,
        AudioOutput? output)
    {
        if (trackKey is not null
            && trackOffsets is not null
            && trackOffsets.TryGetValue(trackKey, out var trackOffset))
        {
            return new TimingResolution(Clamp(trackOffset), TimingSource.Track);
        }

        if (output is { IsKnown: true }
            && outputOffsets is not null
            && outputOffsets.TryGetValue(output.Id, out var profile)
            && profile is not null)
        {
            return new TimingResolution(Clamp(profile.OffsetMs), TimingSource.Output);
        }

        return new TimingResolution(Clamp(globalOffsetMs), TimingSource.Global);
    }

    /// <summary>
    /// Adds a session-only correction on top of the resolved value. Session corrections exist so a
    /// one-off adjustment - "just for this song, this time" - does not quietly rewrite a saved value.
    /// </summary>
    public static TimingResolution AddSession(TimingResolution resolved, int sessionOffsetMs) =>
        sessionOffsetMs == 0
            ? resolved
            : new TimingResolution(Clamp(resolved.OffsetMs + sessionOffsetMs), TimingSource.Session);

    /// <summary>The value a nudge would store for this track, baking in any session correction.</summary>
    public static int NextTrackOffset(TimingResolution resolved, int sessionOffsetMs, int deltaMs) =>
        Clamp(resolved.OffsetMs + sessionOffsetMs + deltaMs);

    public static int NextSessionOffset(int sessionOffsetMs, int deltaMs) =>
        Clamp(sessionOffsetMs + deltaMs);

    /// <summary>"+0.25 s · this track", as the control bar and the shortcut toast print it.</summary>
    public static string Describe(TimingResolution resolution, AudioOutput? output) =>
        FormatOffset(resolution.OffsetMs) + " \u00b7 " + SourceLabel(resolution.Source, output);

    /// <summary>The second half of <see cref="Describe"/>: where the number came from.</summary>
    public static string SourceLabel(TimingSource source, AudioOutput? output) => source switch
    {
        TimingSource.Track => "this track",
        TimingSource.Output => output is { IsBluetooth: true }
            ? "Bluetooth headphones"
            : string.IsNullOrWhiteSpace(output?.Name) ? "this output" : output.Name,
        TimingSource.Session => "this session",
        _ => "default"
    };

    /// <summary>The offset as the rest of the application prints it: "+0.25 s", "0.00 s".</summary>
    public static string FormatOffset(int offsetMs) => offsetMs switch
    {
        0 => "0.00 s",
        _ => (offsetMs / 1000.0).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " s"
    };

    /// <summary>
    /// Repairs a hand-edited track map: drops blank keys, clamps values, and caps the entry count so
    /// the file cannot grow without bound. The caller reuses the same map.
    /// </summary>
    public static void NormaliseTrackOffsets(Dictionary<string, int>? trackOffsets)
    {
        if (trackOffsets is null)
        {
            return;
        }

        var clean = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (key, value) in trackOffsets)
        {
            if (string.IsNullOrWhiteSpace(key) || clean.Count >= MaxTracks)
            {
                continue;
            }

            clean[key] = Clamp(value);
        }

        trackOffsets.Clear();

        foreach (var (key, value) in clean)
        {
            trackOffsets[key] = value;
        }
    }

    /// <summary>Repairs the saved output profiles: blank ids and null profiles cannot be resolved.</summary>
    public static void NormaliseOutputOffsets(Dictionary<string, OutputOffsetProfile>? outputOffsets)
    {
        if (outputOffsets is null)
        {
            return;
        }

        var clean = new Dictionary<string, OutputOffsetProfile>(StringComparer.Ordinal);

        foreach (var (key, value) in outputOffsets)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null || clean.Count >= MaxOutputs)
            {
                continue;
            }

            clean[key] = new OutputOffsetProfile
            {
                Name = string.IsNullOrWhiteSpace(value.Name) ? "Output device" : value.Name.Trim(),
                OffsetMs = Clamp(value.OffsetMs),
                Bluetooth = value.Bluetooth
            };
        }

        outputOffsets.Clear();

        foreach (var (key, value) in clean)
        {
            outputOffsets[key] = value;
        }
    }

    private static string KeyPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
