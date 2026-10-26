using System.Text;
using Overlyric.Lyrics;

namespace Overlyric.LocalLyricsTests;

/// <summary>
/// Checks the local lyric library without a desktop: encoding detection, preview facts, the stored
/// sheet and its metadata, the explicit selection file (including migration from versions.json), and
/// the export round-trip.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;
    private static string _root = "";

    private static int Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "OverlyricLocalLyricsTests-" + Guid.NewGuid().ToString("N"));

        try
        {
            DecodeChecks();
            PreviewChecks();
            StoreChecks();
            SelectionChecks();
            ExportChecks();
        }
        finally
        {
            TryDeleteDirectory();
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void DecodeChecks()
    {
        var utf8 = Encoding.UTF8.GetBytes("caf\u00e9 \u2013 \u00e9t\u00e9");
        Check("plain UTF-8 decodes", LyricTextCodec.Decode(utf8) == "caf\u00e9 \u2013 \u00e9t\u00e9", LyricTextCodec.Decode(utf8));

        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[00:01.00]Hi")).ToArray();
        Check("a UTF-8 BOM is consumed", LyricTextCodec.Decode(bom) == "[00:01.00]Hi", LyricTextCodec.Decode(bom));

        var utf16Le = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[00:02.00]Hola")).ToArray();
        Check("UTF-16LE with a BOM decodes", LyricTextCodec.Decode(utf16Le) == "[00:02.00]Hola", LyricTextCodec.Decode(utf16Le));

        var utf16Be = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("[00:03.00]Hallo")).ToArray();
        Check("UTF-16BE with a BOM decodes", LyricTextCodec.Decode(utf16Be) == "[00:03.00]Hallo", LyricTextCodec.Decode(utf16Be));

        var utf16NoBom = Encoding.Unicode.GetBytes("[00:04.00]Ciao");
        Check("BOM-less UTF-16LE is recognised from its zero bytes", LyricTextCodec.Decode(utf16NoBom) == "[00:04.00]Ciao", LyricTextCodec.Decode(utf16NoBom));

        var utf16BeNoBom = Encoding.BigEndianUnicode.GetBytes("[00:05.00]Hallo");
        Check("BOM-less UTF-16BE is recognised too", LyricTextCodec.Decode(utf16BeNoBom) == "[00:05.00]Hallo", LyricTextCodec.Decode(utf16BeNoBom));

        // Windows-1252: 0xE9 is e-acute and 0x80 is the euro sign, which is what makes it not Latin-1.
        var ansi = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)' ', 0x80, (byte)'5' };
        Check("Windows-1252 decodes its non-ASCII bytes", LyricTextCodec.Decode(ansi) == "caf\u00e9 \u20ac5", LyricTextCodec.Decode(ansi));

        var latin1 = new byte[] { (byte)'n', (byte)'a', (byte)'\u00ef', (byte)'v', (byte)'e' };
        Check("Latin-1 range bytes map to the same characters", LyricTextCodec.Decode(latin1) == "na\u00efve", LyricTextCodec.Decode(latin1));

        var garbage = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0xC3, 0x28, 0x80, 0x81 };
        Check("undecodable bytes never throw", LyricTextCodec.Decode(garbage).Length > 0, LyricTextCodec.Decode(garbage).Length);

        Check("null and empty input decode to empty text",
            LyricTextCodec.Decode(null).Length == 0 && LyricTextCodec.Decode([]).Length == 0, "");
    }

    private static void PreviewChecks()
    {
        var synced = "[ar:Someone]\r\n[00:01.00]Hello\r\n[00:03.50]World\r\n";
        var store = NewStore();

        var preview = store.PreviewText(synced, "song.lrc", 4.0);
        Check("a synced sheet is recognised", preview.Ok && preview.IsSynced && !preview.IsPlain, preview.IsSynced);
        Check("the preview counts the timed lines", preview.LineCount == 2, preview.LineCount);
        Check("the preview reports the first timestamp", preview.FirstStart == TimeSpan.FromSeconds(1), preview.FirstStart);
        Check("the preview reports the last timestamp", preview.LastStart == TimeSpan.FromSeconds(3.5), preview.LastStart);
        Check("the preview reports the duration delta against the track", preview.DurationDeltaSeconds == -0.5, preview.DurationDeltaSeconds);

        var over = store.PreviewText("[00:10.00]Only", "over.lrc", 4.0);
        Check("a sheet longer than the track reports a positive delta", over.DurationDeltaSeconds == 6.0, over.DurationDeltaSeconds);

        var plain = store.PreviewText("First line\r\nSecond line\r\nThird line\r\n", "plain.txt", 4.0);
        Check("plain text is not mistaken for synced text", plain.Ok && !plain.IsSynced && plain.IsPlain, plain.IsSynced);
        Check("the preview counts non-empty plain lines", plain.LineCount == 3, plain.LineCount);
        Check("plain text has no duration delta", plain.DurationDeltaSeconds is null, plain.DurationDeltaSeconds);

        var metadataOnly = store.PreviewText("[ar:Someone]\r\n[ti:Something]\r\n", "tags.lrc", 4.0);
        Check("a file with only metadata tags has no lyric lines", metadataOnly.IsSynced == false && metadataOnly.LineCount == 0, metadataOnly.LineCount);

        var empty = store.PreviewText("", "empty.lrc", 4.0);
        Check("an empty file is a failed preview, not a crash", !empty.Ok && empty.Error is not null, empty.Error);

        var missing = store.Preview(Path.Combine(_root, "does-not-exist.lrc"), 4.0);
        Check("a missing file is a failed preview with a sentence", !missing.Ok && missing.Error is not null, missing.Error);

        var coded = WriteFile("coded.lrc", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[00:01.00]Caf\u00e9")).ToArray());
        var decoded = store.Preview(coded, 4.0);
        Check("the preview decodes the file it was given", decoded.Ok && decoded.Lines.Count == 1 && decoded.Lines[0].Text == "Caf\u00e9", decoded.Lines.FirstOrDefault().Text);
    }

    private static void StoreChecks()
    {
        var store = NewStore();
        const string key = "artist\nsong\nalbum";
        var metadata = new LocalLyricsMetadata
        {
            Artist = "Artist",
            Title = "Song",
            Album = "Album",
            SourcePath = @"C:\Music\song.lrc",
            AddedAt = 123
        };

        Check("saving a local sheet succeeds", store.Save(key, "[00:01.00]One\r\n[00:05.00]Two\r\n", metadata), "");
        Check("the stored sheet loads back", store.TryLoad(key, out var loaded) && loaded?.Lines.Count == 2, "");
        Check("the stored metadata loads back", loaded?.Metadata?.Artist == "Artist" && loaded.Metadata.SourcePath.EndsWith("song.lrc", StringComparison.Ordinal), loaded?.Metadata?.Artist);
        Check("a different key is a miss", !store.TryLoad("nope", out _), "");
        Check("the store reports one entry and a positive size", store.GetStats().Entries == 1 && store.GetStats().Bytes > 0, store.GetStats());

        File.WriteAllText(Path.Combine(store.Directory, LocalLyricsStore.Hash(key) + ".json"), "not json at all");
        Check("corrupt metadata still yields the sheet", store.TryLoad(key, out var brokenMeta) && brokenMeta?.Lines.Count == 2 && brokenMeta.Metadata is null, brokenMeta?.Metadata);

        File.WriteAllBytes(Path.Combine(store.Directory, LocalLyricsStore.Hash(key) + ".lrc"), [0x00, 0x01, 0xFF, 0xFE, 0x80]);
        Check("a corrupt sheet is still readable without throwing", store.TryLoad(key, out var brokenSheet) && brokenSheet is not null, "");
        Check("removing a sheet deletes both files", store.Remove(key) && !store.TryLoad(key, out _), "");
        Check("removing a key with nothing stored is false", !store.Remove("absent"), "");
        Check("saving with a blank key is refused", !store.Save("  ", "x", metadata), "");
    }

    private static void SelectionChecks()
    {
        var dir = Path.Combine(_root, "selections");
        var path = Path.Combine(dir, "selections.json");
        var store = LyricSelectionStore.Load(path);
        const string key = "artist\nsong\nalbum";

        Check("an absent selection is a miss", !store.TryGet(key, out _), "");
        store.SetLocal(key, "abc.lrc");
        Check("a local selection is remembered", store.TryGet(key, out var local) && local.Kind == LyricSelectionKind.Local && local.Path == "abc.lrc", "");

        store.SetLrclib(key, 424242);
        Check("the last explicit action wins", store.TryGet(key, out var pinned) && pinned.Kind == LyricSelectionKind.Lrclib && pinned.RecordId == 424242, "");
        store.Save();

        var reloaded = LyricSelectionStore.Load(path);
        Check("a selection survives a restart", reloaded.TryGet(key, out var again) && again.RecordId == 424242, "");

        reloaded.Clear(key);
        reloaded.Save();
        Check("clearing a selection removes it", !LyricSelectionStore.Load(path).TryGet(key, out _), "");

        // Migration from the file the old version pinning used.
        var legacyPath = Path.Combine(_root, "migrate", "versions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, "{\"" + key.Replace("\n", "\\n") + "\":123456}");
        var migrated = LyricSelectionStore.Load(Path.Combine(_root, "migrate", "selections.json"), legacyPath);
        Check("a legacy pinned record migrates to a selection", migrated.TryGet(key, out var migratedSelection)
            && migratedSelection.Kind == LyricSelectionKind.Lrclib && migratedSelection.RecordId == 123456, "");
        Check("the legacy file is left untouched", File.Exists(legacyPath), "");
        Check("the migrated selection is persisted immediately", File.Exists(Path.Combine(_root, "migrate", "selections.json")), "");

        // Bounds: a hand-edited or very long-lived file is trimmed, not trusted.
        var bounded = LyricSelectionStore.Load(Path.Combine(_root, "bounded", "selections.json"));
        for (var i = 0; i < 600; i++)
        {
            bounded.SetLrclib("k" + i, i + 1);
        }

        bounded.Save();
        var boundedAfter = LyricSelectionStore.Load(Path.Combine(_root, "bounded", "selections.json"));
        Check("the selection file is bounded", boundedAfter.Count == 512, boundedAfter.Count);

        Check("blank keys are not stored", !bounded.TryGet("   ", out _), "");
    }

    private static void ExportChecks()
    {
        var lines = new List<LyricLine>
        {
            new(TimeSpan.Zero, "First"),
            new(TimeSpan.FromSeconds(1.234), "Second"),
            new(TimeSpan.FromSeconds(61.005), "Past a minute"),
            new(TimeSpan.FromSeconds(3599.999), "Almost an hour")
        };

        var text = LyricTextCodec.EncodeLrc("Artist", "Song", "Album", 3601, lines);
        Check("the export carries the metadata tags",
            text.Contains("[ar:Artist]", StringComparison.Ordinal)
            && text.Contains("[ti:Song]", StringComparison.Ordinal)
            && text.Contains("[al:Album]", StringComparison.Ordinal)
            && text.Contains("[length:60:01]", StringComparison.Ordinal), "");
        Check("the export uses CRLF", text.Contains("\r\n", StringComparison.Ordinal), "");
        Check("the export uses millisecond timestamps", text.Contains("[00:01.234]", StringComparison.Ordinal), text.Split('\n')[5]);

        var reparsed = LrcParser.Parse(LyricTextCodec.Decode(LyricTextCodec.EncodeUtf8(text)));
        var same = reparsed.Count == lines.Count;

        for (var i = 0; same && i < lines.Count; i++)
        {
            same = reparsed[i].Start == lines[i].Start && reparsed[i].Text == lines[i].Text;
        }

        Check("export and re-import reproduce every timestamp and line", same, reparsed.Count);

        var newlineText = LyricTextCodec.EncodeLrc("A\r\nB", "T", null, null, [new LyricLine(TimeSpan.FromSeconds(1), "one\r\ntwo")]);
        Check("metadata and lyric newlines cannot break the format",
            !newlineText.Contains("A\r\nB", StringComparison.Ordinal) && newlineText.Contains("onetwo", StringComparison.Ordinal), newlineText);

        var negative = LrcParser.Parse(LyricTextCodec.EncodeLrc("A", "T", null, null, [new LyricLine(TimeSpan.FromSeconds(-5), "before")]));
        Check("a negative timestamp is clamped to the start", negative.Count == 1 && negative[0].Start == TimeSpan.Zero, negative.FirstOrDefault().Start);

        var empty = LyricTextCodec.EncodeLrc("A", "T", null, null, []);
        Check("an empty sheet exports only its header", LrcParser.Parse(empty).Count == 0, "");
        Check("the file-name hash is deterministic and key-specific",
            LocalLyricsStore.Hash("a\nb") == LocalLyricsStore.Hash("a\nb")
            && LocalLyricsStore.Hash("a\nb") != LocalLyricsStore.Hash("a\nc")
            && LocalLyricsStore.Hash("a\nb").Length == 64, LocalLyricsStore.Hash("a\nb"));
    }

    private static LocalLyricsStore NewStore() =>
        new(Path.Combine(_root, "lyrics-" + Guid.NewGuid().ToString("N")));

    private static string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDeleteDirectory()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // A leftover temp directory is not worth failing the run over.
        }
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
