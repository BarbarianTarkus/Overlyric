namespace Overlyric.App;

/// <summary>
/// A small rolling log of the decisions that are otherwise invisible: track changes, lookup
/// verdicts, timing changes, device switches and why the overlay is hidden when it is.
/// </summary>
/// <remarks>
/// It exists for the diagnostics window and for support: a bug is usually a decision that was made
/// without evidence in front of the listener. The in-memory ring is bounded, the on-disk file is
/// bounded, and a write failure is swallowed - logging must never be able to break playback.
/// </remarks>
internal sealed class DiagnosticLog
{
    /// <summary>Lines kept in memory for the diagnostics window.</summary>
    public const int Capacity = 256;

    /// <summary>The log file is cut in half when it passes this size, so it cannot grow unbounded.</summary>
    public const long MaxBytes = 256 * 1024;

    private readonly Queue<string> _lines;
    private readonly Lock _gate = new();
    private readonly string? _path;

    public DiagnosticLog(string? path = null, int capacity = Capacity)
    {
        _path = path;
        CapacityValue = Math.Max(16, capacity);
        _lines = new Queue<string>(CapacityValue);
    }

    public int CapacityValue { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    public void Add(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = string.Concat(DateTimeOffset.Now.ToString("HH:mm:ss.fff"), " ", category, ": ", message);

        lock (_gate)
        {
            _lines.Enqueue(line);

            while (_lines.Count > CapacityValue)
            {
                _lines.Dequeue();
            }
        }

        Append(line);
    }

    /// <summary>The most recent lines, oldest first.</summary>
    public IReadOnlyList<string> Recent(int count = 20)
    {
        lock (_gate)
        {
            if (count <= 0 || _lines.Count == 0)
            {
                return [];
            }

            return _lines.Skip(Math.Max(0, _lines.Count - count)).ToArray();
        }
    }

    /// <summary>The whole ring, one line each, for "copy the log".</summary>
    public string Dump()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _lines);
        }
    }

    private void Append(string line)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var info = new FileInfo(_path);

            if (info.Exists && info.Length > MaxBytes)
            {
                // Keep the second half rather than deleting everything: the old events still have
                // context, and the file cannot keep growing.
                var kept = File.ReadAllLines(_path);
                File.WriteAllLines(_path, kept.Skip(kept.Length / 2));
            }

            File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // Logging is diagnostics, never a reason to interrupt playback.
        }
    }
}
