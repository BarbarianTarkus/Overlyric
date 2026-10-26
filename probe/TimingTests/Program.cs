using Overlyric.Media;

namespace Overlyric.TimingTests;

/// <summary>
/// Checks the lyric-timing resolution rules: track override beats output profile beats the global
/// default; a stored zero is still an override; session corrections do not rewrite saved values; and
/// the labels name the layer that supplied the number.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        ResolutionChecks();
        SessionChecks();
        KeyChecks();
        LabelChecks();
        NormaliseChecks();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void ResolutionChecks()
    {
        var speaker = new AudioOutput("endpoint-speakers", "Speakers (Realtek)", false);
        var headphones = new AudioOutput("endpoint-bt", "WH-1000XM5", true);
        var track = TimingProfiles.TrackKey("TIDAL", "Song");
        var otherTrack = TimingProfiles.TrackKey("TIDAL", "Another song");

        Check("with nothing saved the global default applies",
            TimingProfiles.Resolve(120, null, null, null, null) == new TimingResolution(120, TimingSource.Global),
            TimingProfiles.Resolve(120, null, null, null, null));

        var tracks = new Dictionary<string, int> { [track!] = 250 };
        var outputs = new Dictionary<string, OutputOffsetProfile>
        {
            [speaker.Id] = new() { Name = speaker.Name, OffsetMs = -150 },
            [headphones.Id] = new() { Name = headphones.Name, OffsetMs = 80, Bluetooth = true }
        };

        Check("an output profile beats the global default",
            TimingProfiles.Resolve(0, tracks, otherTrack, outputs, speaker) == new TimingResolution(-150, TimingSource.Output),
            TimingProfiles.Resolve(0, tracks, otherTrack, outputs, speaker));

        Check("a track override beats the output profile",
            TimingProfiles.Resolve(0, tracks, track, outputs, speaker) == new TimingResolution(250, TimingSource.Track),
            TimingProfiles.Resolve(0, tracks, track, outputs, speaker));

        Check("the layers are overrides, not sums",
            TimingProfiles.Resolve(0, tracks, track, outputs, speaker).OffsetMs == 250,
            TimingProfiles.Resolve(0, tracks, track, outputs, speaker).OffsetMs);

        var zeroOverride = new Dictionary<string, int> { [track!] = 0 };
        Check("a stored zero is an override, not a fall-through",
            TimingProfiles.Resolve(900, zeroOverride, track, outputs, speaker) == new TimingResolution(0, TimingSource.Track),
            TimingProfiles.Resolve(900, zeroOverride, track, outputs, speaker));

        Check("a device with no profile falls back to the default",
            TimingProfiles.Resolve(120, null, otherTrack, outputs, new AudioOutput("endpoint-hdmi", "HDMI", false))
                == new TimingResolution(120, TimingSource.Global),
            TimingProfiles.Resolve(120, null, otherTrack, outputs, new AudioOutput("endpoint-hdmi", "HDMI", false)));

        Check("no endpoint at all still resolves the default",
            TimingProfiles.Resolve(75, null, null, outputs, null) == new TimingResolution(75, TimingSource.Global),
            TimingProfiles.Resolve(75, null, null, outputs, null));

        // The endpoint id is the key: two outputs can share a friendly name without sharing a profile.
        var sameName = new AudioOutput("endpoint-other", "Speakers (Realtek)", false);
        Check("a profile is keyed by endpoint id, not by friendly name",
            TimingProfiles.Resolve(0, null, otherTrack, outputs, sameName).Source == TimingSource.Global,
            TimingProfiles.Resolve(0, null, otherTrack, outputs, sameName));

        Check("the resolver clamps a hand-edited huge value",
            TimingProfiles.Resolve(0, new Dictionary<string, int> { [track!] = 99_999 }, track, null, null).OffsetMs == TimingProfiles.MaxOffsetMs,
            TimingProfiles.Resolve(0, new Dictionary<string, int> { [track!] = 99_999 }, track, null, null).OffsetMs);
    }

    private static void SessionChecks()
    {
        var baseResolution = new TimingResolution(100, TimingSource.Output);

        Check("no session correction keeps the resolved source",
            TimingProfiles.AddSession(baseResolution, 0) == baseResolution,
            TimingProfiles.AddSession(baseResolution, 0));

        Check("a session correction adds to the resolved value",
            TimingProfiles.AddSession(baseResolution, 50) == new TimingResolution(150, TimingSource.Session),
            TimingProfiles.AddSession(baseResolution, 50));

        Check("a session correction is clamped",
            TimingProfiles.AddSession(new TimingResolution(9_900, TimingSource.Global), 500).OffsetMs == TimingProfiles.MaxOffsetMs,
            TimingProfiles.AddSession(new TimingResolution(9_900, TimingSource.Global), 500).OffsetMs);

        Check("saving a nudge bakes the session correction in",
            TimingProfiles.NextTrackOffset(baseResolution, 50, 25) == 175,
            TimingProfiles.NextTrackOffset(baseResolution, 50, 25));

        Check("a session nudge is clamped",
            TimingProfiles.NextSessionOffset(TimingProfiles.MaxOffsetMs, 1) == TimingProfiles.MaxOffsetMs,
            TimingProfiles.NextSessionOffset(TimingProfiles.MaxOffsetMs, 1));

        Check("the clamp is the same one the config uses",
            TimingProfiles.Clamp(-20_000) == TimingProfiles.MinOffsetMs
            && TimingProfiles.Clamp(20_000) == TimingProfiles.MaxOffsetMs,
            $"{TimingProfiles.Clamp(-20_000)}/{TimingProfiles.Clamp(20_000)}");
    }

    private static void KeyChecks()
    {
        var first = TimingProfiles.TrackKey("TIDAL", "A Song For All Seasons");
        var same = TimingProfiles.TrackKey("tidal", "  a   song for all seasons  ");

        Check("track keys ignore case and spacing", first is not null && first == same, $"{first} vs {same}");
        Check("a missing artist still keys by title",
            TimingProfiles.TrackKey(null, "Song") is not null && TimingProfiles.TrackKey(null, "Song") != first,
            TimingProfiles.TrackKey(null, "Song"));
        Check("an artist-only key is not the same as a title-only key",
            TimingProfiles.TrackKey("Artist", null) != TimingProfiles.TrackKey(null, "Artist"),
            $"{TimingProfiles.TrackKey("Artist", null)} vs {TimingProfiles.TrackKey(null, "Artist")}");
        Check("no metadata means no key", TimingProfiles.TrackKey(null, null) is null && TimingProfiles.TrackKey("  ", "") is null, "");
        Check("punctuation still distinguishes two performances",
            TimingProfiles.TrackKey("Artist", "Song (Live)") != TimingProfiles.TrackKey("Artist", "Song Live"),
            $"{TimingProfiles.TrackKey("Artist", "Song (Live)")} vs {TimingProfiles.TrackKey("Artist", "Song Live")}");
    }

    private static void LabelChecks()
    {
        var speakers = new AudioOutput("endpoint-speakers", "Speakers (Realtek)", false);
        var bluetooth = new AudioOutput("endpoint-bt", "WH-1000XM5", true);

        Check("a track correction is labelled as such",
            TimingProfiles.Describe(new TimingResolution(250, TimingSource.Track), speakers) == "+0.25 s \u00b7 this track",
            TimingProfiles.Describe(new TimingResolution(250, TimingSource.Track), speakers));

        Check("a Bluetooth profile is named by what it is",
            TimingProfiles.Describe(new TimingResolution(150, TimingSource.Output), bluetooth) == "+0.15 s \u00b7 Bluetooth headphones",
            TimingProfiles.Describe(new TimingResolution(150, TimingSource.Output), bluetooth));

        Check("another output profile is named by the device",
            TimingProfiles.Describe(new TimingResolution(-150, TimingSource.Output), speakers) == "-0.15 s \u00b7 Speakers (Realtek)",
            TimingProfiles.Describe(new TimingResolution(-150, TimingSource.Output), speakers));

        Check("the global default is labelled default",
            TimingProfiles.Describe(new TimingResolution(0, TimingSource.Global), speakers) == "0.00 s \u00b7 default",
            TimingProfiles.Describe(new TimingResolution(0, TimingSource.Global), speakers));

        Check("a session nudge is labelled as a session value",
            TimingProfiles.Describe(new TimingResolution(250, TimingSource.Session), speakers) == "+0.25 s \u00b7 this session",
            TimingProfiles.Describe(new TimingResolution(250, TimingSource.Session), speakers));

        Check("the offset format matches the rest of the app",
            TimingProfiles.FormatOffset(0) == "0.00 s"
            && TimingProfiles.FormatOffset(-250) == "-0.25 s"
            && TimingProfiles.FormatOffset(500) == "+0.50 s"
            && TimingProfiles.FormatOffset(-10_000) == "-10.00 s",
            $"{TimingProfiles.FormatOffset(0)}/{TimingProfiles.FormatOffset(-250)}/{TimingProfiles.FormatOffset(500)}/{TimingProfiles.FormatOffset(-10_000)}");
    }

    private static void NormaliseChecks()
    {
        var tracks = new Dictionary<string, int>
        {
            ["  "] = 5,
            ["t\u001fsong"] = 20_000,
            ["t\u001fother"] = -20_000
        };

        for (var i = 0; i < TimingProfiles.MaxTracks + 10; i++)
        {
            tracks["padding-" + i] = i;
        }

        TimingProfiles.NormaliseTrackOffsets(tracks);
        Check("a hand-edited track map drops blank keys", !tracks.ContainsKey("  "), tracks.Count);
        Check("a hand-edited track value is clamped", tracks["t\u001fsong"] == TimingProfiles.MaxOffsetMs, tracks["t\u001fsong"]);
        Check("a hand-edited negative value is clamped", tracks["t\u001fother"] == TimingProfiles.MinOffsetMs, tracks["t\u001fother"]);
        Check("the track map is capped", tracks.Count == TimingProfiles.MaxTracks, tracks.Count);

        var outputs = new Dictionary<string, OutputOffsetProfile>
        {
            [""] = new() { Name = "blank", OffsetMs = 1 },
            ["endpoint-a"] = new() { Name = "  ", OffsetMs = 20_000 },
            ["endpoint-b"] = null!
        };

        TimingProfiles.NormaliseOutputOffsets(outputs);
        Check("a hand-edited output map drops blank ids", !outputs.ContainsKey(""), outputs.Count);
        Check("a null profile is dropped", !outputs.ContainsKey("endpoint-b"), outputs.Count);
        Check("an output value is clamped", outputs["endpoint-a"].OffsetMs == TimingProfiles.MaxOffsetMs, outputs["endpoint-a"].OffsetMs);
        Check("a blank output name gets a usable label", outputs["endpoint-a"].Name == "Output device", outputs["endpoint-a"].Name);
    }

    private static void Check(string what, bool ok, object? detail)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine($"PASS | {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL | {what}   (got: {detail})");
    }
}
