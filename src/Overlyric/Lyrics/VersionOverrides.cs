using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlyric.Lyrics;

/// <summary>
/// Remembers which lrclib record the listener chose for a given track, so a manual version choice
/// survives across plays and restarts.
/// <para>
/// Kept in its own file rather than in the settings, because it is derived state that grows with
/// listening history rather than something a person edits by hand. It is bounded so it cannot grow
/// without limit.
/// </para>
/// </summary>
public sealed class VersionOverrides
{
    private const int MaxEntries = 256;

    private readonly string _path;
    private readonly Dictionary<string, long> _map;
    private bool _dirty;

    private VersionOverrides(string path, Dictionary<string, long> map)
    {
        _path = path;
        _map = map;
    }

    public static VersionOverrides Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize(json, VersionOverridesJsonContext.Default.DictionaryStringInt64);

                if (loaded is not null)
                {
                    return new VersionOverrides(path, loaded);
                }
            }
        }
        catch (Exception)
        {
            // A corrupt preference file must never stop the app starting.
        }

        return new VersionOverrides(path, new Dictionary<string, long>(StringComparer.Ordinal));
    }

    public bool TryGet(string key, out long id) => _map.TryGetValue(key, out id);

    public void Set(string key, long id)
    {
        _map[key] = id;
        _dirty = true;
    }

    public void Clear(string key)
    {
        if (_map.Remove(key))
        {
            _dirty = true;
        }
    }

    public void Save()
    {
        if (!_dirty)
        {
            return;
        }

        try
        {
            Trim();

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_map, VersionOverridesJsonContext.Default.DictionaryStringInt64);
            var temp = _path + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);

            _dirty = false;
        }
        catch (Exception)
        {
            // Losing a preference write is not worth interrupting playback for.
        }
    }

    private void Trim()
    {
        while (_map.Count > MaxEntries)
        {
            // The dictionary is insertion ordered, so its first key is the least recently added one.
            var oldest = _map.Keys.FirstOrDefault();

            if (oldest is null)
            {
                return;
            }

            _map.Remove(oldest);
        }
    }

    /// <summary>Builds the cache key for a pinned version, kept separate from the automatic answer.</summary>
    public static string VersionKey(string trackKey, long id) =>
        string.Concat(trackKey, "#", id.ToString(CultureInfo.InvariantCulture));
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class VersionOverridesJsonContext : JsonSerializerContext;
