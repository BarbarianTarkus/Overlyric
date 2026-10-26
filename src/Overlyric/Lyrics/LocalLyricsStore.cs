using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlyric.Lyrics;

/// <summary>The facts an imported local sheet remembers about the track it came from.</summary>
public sealed class LocalLyricsMetadata
{
    public string Artist { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Album { get; set; }

    /// <summary>The file the listener picked, for "where did this come from".</summary>
    public string SourcePath { get; set; } = "";

    public long AddedAt { get; set; }

    /// <summary>Free-form note from the import, e.g. "plain lyrics: cannot follow the music".</summary>
    public string? Note { get; set; }
}

/// <summary>One stored local sheet, read back from disk.</summary>
public sealed class LocalLyricsDocument
{
    public required string Text { get; init; }

    public required IReadOnlyList<LyricLine> Lines { get; init; }

    public bool IsSynced { get; init; }

    public LocalLyricsMetadata? Metadata { get; init; }
}

/// <summary>
/// What an import would store, shown before the listener commits: line count, the span of the sheet,
/// how it compares with the track's length, and whether it can follow the music at all.
/// </summary>
public sealed class LocalLyricsPreview
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public string FileName { get; init; } = "";

    public string Text { get; init; } = "";

    public IReadOnlyList<LyricLine> Lines { get; init; } = [];

    public bool IsSynced { get; init; }

    /// <summary>Timed lines, or the count of plain text lines when there are no timestamps.</summary>
    public int LineCount { get; init; }

    public TimeSpan? FirstStart { get; init; }

    public TimeSpan? LastStart { get; init; }

    /// <summary>
    /// Last timestamp minus the track length. Negative means the sheet ends before the track does,
    /// which is usually a cut version or a different edit; positive means it runs past the end.
    /// </summary>
    public double? DurationDeltaSeconds { get; init; }

    public bool IsPlain => !IsSynced && Text.Trim().Length > 0;
}

/// <summary>
/// The local lyric library: one <c>.lrc</c> plus one metadata <c>.json</c> per track under
/// <c>%LOCALAPPDATA%\Overlyric\lyrics</c>, named by a hash of the track key.
/// </summary>
/// <remarks>
/// <para>
/// A local sheet is a listener's explicit answer, so it is read from disk on every play instead of
/// being cached: editing the file in Notepad takes effect on the next lookup. The hash keeps
/// arbitrary metadata out of file names, and the metadata sidecar keeps the artist/title/album
/// readable when the folder is opened.
/// </para>
/// <para>
/// Missing, unreadable or malformed files are never exceptions here; the caller decides between
/// "fall back to automatic" and "keep the last sheet".
/// </para>
/// </remarks>
public sealed class LocalLyricsStore
{
    private readonly string _directory;

    public LocalLyricsStore(string directory)
    {
        _directory = directory;
    }

    public string Directory => _directory;

    /// <summary>Builds a preview of a file without storing it. Never throws.</summary>
    public LocalLyricsPreview Preview(string sourcePath, double? trackDurationSeconds)
    {
        try
        {
            var bytes = File.ReadAllBytes(sourcePath);
            return BuildPreview(
                LyricTextCodec.Decode(bytes),
                Path.GetFileName(sourcePath),
                trackDurationSeconds);
        }
        catch (Exception)
        {
            return new LocalLyricsPreview
            {
                Ok = false,
                Error = "That file could not be read.",
                FileName = Path.GetFileName(sourcePath)
            };
        }
    }

    /// <summary>Same preview for text that came from somewhere other than a file (a test, a paste).</summary>
    public LocalLyricsPreview PreviewText(string text, string fileName, double? trackDurationSeconds) =>
        BuildPreview(text, fileName, trackDurationSeconds);

    /// <summary>Saves the decoded sheet and its metadata. Returns false when nothing could be written.</summary>
    public bool Save(string key, string text, LocalLyricsMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var basePath = BasePath(key);
            var lrcPath = basePath + ".lrc";
            var metadataPath = basePath + ".json";

            WriteAtomic(lrcPath, LyricTextCodec.EncodeUtf8(text));
            WriteAtomic(
                metadataPath,
                LyricTextCodec.EncodeUtf8(JsonSerializer.Serialize(
                    metadata,
                    LocalLyricsJsonContext.Default.LocalLyricsMetadata)));

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryLoad(string key, out LocalLyricsDocument document)
    {
        document = null!;

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        try
        {
            var basePath = BasePath(key);
            var lrcPath = basePath + ".lrc";

            if (!File.Exists(lrcPath))
            {
                return false;
            }

            var text = LyricTextCodec.Decode(File.ReadAllBytes(lrcPath));
            var lines = LrcParser.Parse(text);
            var metadata = ReadMetadata(basePath + ".json");

            document = new LocalLyricsDocument
            {
                Text = text,
                Lines = lines,
                IsSynced = lines.Count > 0,
                Metadata = metadata
            };

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var basePath = BasePath(key);
        var removed = false;

        foreach (var path in new[] { basePath + ".lrc", basePath + ".json" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed = true;
                }
            }
            catch (Exception)
            {
                // One unreadable file should not stop the other from being removed.
            }
        }

        return removed;
    }

    /// <summary>Entry and byte count for the Advanced section; best effort.</summary>
    public (int Entries, long Bytes) GetStats()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return (0, 0);
            }

            var entries = 0;
            long bytes = 0;

            foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.lrc"))
            {
                entries++;
                bytes += new FileInfo(path).Length;
            }

            foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
            {
                bytes += new FileInfo(path).Length;
            }

            return (entries, bytes);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    private LocalLyricsPreview BuildPreview(string text, string fileName, double? trackDurationSeconds)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new LocalLyricsPreview
            {
                Ok = false,
                Error = "The file has no text in it.",
                FileName = fileName
            };
        }

        var lines = LrcParser.Parse(text);
        var plainLineCount = lines.Count == 0 ? CountPlainLines(text) : 0;
        var first = lines.Count > 0 ? lines[0].Start : (TimeSpan?)null;
        var last = lines.Count > 0 ? lines[^1].Start : (TimeSpan?)null;

        return new LocalLyricsPreview
        {
            Ok = true,
            FileName = fileName,
            Text = text,
            Lines = lines,
            IsSynced = lines.Count > 0,
            LineCount = lines.Count > 0 ? lines.Count : plainLineCount,
            FirstStart = first,
            LastStart = last,
            DurationDeltaSeconds = last is { } lastStart && trackDurationSeconds is > 0
                ? lastStart.TotalSeconds - trackDurationSeconds.Value
                : null
        };
    }

    private static int CountPlainLines(string text)
    {
        var count = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            // Metadata tags and blanks are not lyrics; counting them would overstate a plain sheet.
            if (line.Length == 0 || (line[0] == '[' && line[^1] == ']'))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    private static LocalLyricsMetadata? ReadMetadata(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                File.ReadAllText(path),
                LocalLyricsJsonContext.Default.LocalLyricsMetadata);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private string BasePath(string key) => System.IO.Path.Combine(_directory, Hash(key));

    /// <summary>SHA-256 of the track key, lowercase hex: stable, filesystem-safe and collision-resistant.</summary>
    internal static string Hash(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var builder = new StringBuilder(digest.Length * 2);

        foreach (var value in digest)
        {
            builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LocalLyricsMetadata))]
internal sealed partial class LocalLyricsJsonContext : JsonSerializerContext;
