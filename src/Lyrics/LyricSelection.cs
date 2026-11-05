using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlyric.Lyrics;

/// <summary>What a track's lyrics are chosen from: automatic matching, a local file, or a pinned record.</summary>
public enum LyricSelectionKind
{
    /// <summary>The automatic pipeline decides; no explicit choice.</summary>
    Automatic = 0,

    /// <summary>A file the listener imported; it wins over every network source.</summary>
    Local = 1,

    /// <summary>One explicit LRCLIB record, chosen in the selector or pinned from the clipboard.</summary>
    Lrclib = 2
}

/// <summary>One explicit lyric choice for one track. The last explicit action wins.</summary>
public sealed class LyricSelection
{
    public LyricSelectionKind Kind { get; set; }

    /// <summary>For <see cref="LyricSelectionKind.Local"/>: the stored file name, not the original path.</summary>
    public string Path { get; set; } = "";

    /// <summary>For <see cref="LyricSelectionKind.Lrclib"/>: the record id.</summary>
    public long RecordId { get; set; }

    public long AddedAt { get; set; }
}

/// <summary>
/// Remembers one explicit <see cref="LyricSelection"/> per track, replacing the implicit precedence
/// between a pinned record and the cache with one rule: the last explicit action wins.
/// </summary>
/// <remarks>
/// Kept in its own bounded file (derived state, like the version overrides it supersedes) rather than
/// in <c>config.json</c>, which a person edits. <see cref="Load"/> migrates the legacy
/// <c>versions.json</c> map on first run, so a record pinned before this existed still wins.
/// </remarks>
public sealed class LyricSelectionStore
{
    /// <summary>Enough history for months of listening; a hand-edited file cannot grow without bound.</summary>
    private const int MaxEntries = 512;

    private readonly string _path;
    private readonly Dictionary<string, LyricSelection> _map;
    private bool _dirty;

    private LyricSelectionStore(string path, Dictionary<string, LyricSelection> map)
    {
        _path = path;
        _map = map;
    }

    public int Count
    {
        get
        {
            lock (_map)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// Loads the selection file. When it does not exist yet, the legacy pinned-version map is migrated
    /// into it so an existing listener keeps their choices. Corrupt files degrade to an empty store,
    /// never to a crash.
    /// </summary>
    public static LyricSelectionStore Load(string path, string? legacyVersionsPath = null)
    {
        var map = new Dictionary<string, LyricSelection>(StringComparer.Ordinal);

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize(
                    json,
                    LyricSelectionJsonContext.Default.DictionaryStringLyricSelection);

                if (loaded is not null)
                {
                    foreach (var (key, value) in loaded)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && value is not null)
                        {
                            map[key] = value;
                        }
                    }

                    return new LyricSelectionStore(path, map);
                }
            }
        }
        catch (Exception)
        {
            // A corrupt selection file must never stop the app starting.
        }

        var store = new LyricSelectionStore(path, map);

        if (legacyVersionsPath is not null)
        {
            store.MigrateLegacy(legacyVersionsPath);
        }

        return store;
    }

    public bool TryGet(string key, out LyricSelection selection)
    {
        lock (_map)
        {
            if (_map.TryGetValue(key, out var found))
            {
                selection = found;
                return true;
            }
        }

        selection = null!;
        return false;
    }

    public void SetLocal(string key, string storedPath)
    {
        Set(key, new LyricSelection
        {
            Kind = LyricSelectionKind.Local,
            Path = storedPath,
            AddedAt = Now()
        });
    }

    public void SetLrclib(string key, long recordId)
    {
        Set(key, new LyricSelection
        {
            Kind = LyricSelectionKind.Lrclib,
            RecordId = recordId,
            AddedAt = Now()
        });
    }

    public void Clear(string key)
    {
        lock (_map)
        {
            if (_map.Remove(key))
            {
                _dirty = true;
            }
        }
    }

    public void Save()
    {
        Dictionary<string, LyricSelection> snapshot;

        lock (_map)
        {
            if (!_dirty)
            {
                return;
            }

            TrimUnlocked();
            snapshot = new Dictionary<string, LyricSelection>(_map, StringComparer.Ordinal);
            _dirty = false;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                snapshot,
                LyricSelectionJsonContext.Default.DictionaryStringLyricSelection);
            var temp = _path + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Losing a selection write is not worth interrupting playback for.
        }
    }

    private void Set(string key, LyricSelection selection)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_map)
        {
            _map[key] = selection;
            _dirty = true;
        }
    }

    private void MigrateLegacy(string legacyVersionsPath)
    {
        try
        {
            if (!File.Exists(legacyVersionsPath))
            {
                return;
            }

            var json = File.ReadAllText(legacyVersionsPath);
            var legacy = JsonSerializer.Deserialize(
                json,
                LyricSelectionJsonContext.Default.DictionaryStringInt64);

            if (legacy is null)
            {
                return;
            }

            foreach (var (key, recordId) in legacy)
            {
                if (string.IsNullOrWhiteSpace(key) || recordId <= 0)
                {
                    continue;
                }

                _map[key] = new LyricSelection
                {
                    Kind = LyricSelectionKind.Lrclib,
                    RecordId = recordId,
                    AddedAt = Now()
                };
            }

            if (_map.Count > 0)
            {
                _dirty = true;
                Save();
            }
        }
        catch (Exception)
        {
            // Migration is best effort: the legacy file stays untouched.
        }
    }

    private void TrimUnlocked()
    {
        while (_map.Count > MaxEntries)
        {
            string? oldestKey = null;
            var oldest = long.MaxValue;

            foreach (var (key, value) in _map)
            {
                if (value.AddedAt < oldest)
                {
                    oldest = value.AddedAt;
                    oldestKey = key;
                }
            }

            if (oldestKey is null)
            {
                return;
            }

            _map.Remove(oldestKey);
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Dictionary<string, LyricSelection>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class LyricSelectionJsonContext : JsonSerializerContext;
