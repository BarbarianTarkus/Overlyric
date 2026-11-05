using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlyric.App;

/// <summary>
/// The state the diagnostics window shows: what is playing, what the clock and the lyric lookup made
/// of it, where the sheet came from, the timing in force, and which Presence rule is hiding what.
/// </summary>
/// <remarks>
/// Deliberately a flat snapshot of facts, not a live object graph: the window asks the host for this
/// on the UI thread, and "copy as JSON" has to be a stable, greppable artifact a listener can paste
/// into a bug report.
/// </remarks>
internal sealed class DiagnosticsSnapshot
{
    public DateTimeOffset CapturedAt { get; init; }

    public string Version { get; init; } = "";

    public bool HasSession { get; init; }

    public string? SourceApp { get; init; }

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public bool IsPlaying { get; init; }

    public double? DurationSeconds { get; init; }

    public string Position { get; init; } = "";

    public bool ClockIsLive { get; init; }

    public bool PositionIsUsable { get; init; }

    public long SeekCount { get; init; }

    public string LyricSource { get; init; } = "none";

    public long? RecordId { get; init; }

    public int LineCount { get; init; }

    public bool Synced { get; init; }

    public bool WordSynced { get; init; }

    public bool Instrumental { get; init; }

    public string? LyricStatus { get; init; }

    public int CacheEntries { get; init; }

    public long CacheBytes { get; init; }

    public int LocalFiles { get; init; }

    public long LocalBytes { get; init; }

    public int OffsetMs { get; init; }

    public string OffsetLabel { get; init; } = "0.00 s";

    public string OffsetSource { get; init; } = "default";

    public string OutputDevice { get; init; } = "";

    public bool OutputBluetooth { get; init; }

    public string Monitor { get; init; } = "";

    public string? ForegroundApp { get; init; }

    public bool HiddenForApp { get; init; }

    public bool FullscreenNotice { get; init; }

    public bool MicrophoneInUse { get; init; }

    public IReadOnlyList<string> Log { get; init; } = [];

    /// <summary>Pretty JSON, because the point is a pasteable artifact rather than a wire format.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, DiagnosticsJsonContext.Default.DiagnosticsSnapshot);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DiagnosticsSnapshot))]
internal sealed partial class DiagnosticsJsonContext : JsonSerializerContext;
