using Overlyric.Interop;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.App;

/// <summary>
/// "Why is it showing this?" - a read-only window with the live pipeline state, the timing in force,
/// the Presence rules and the last events, plus "copy as JSON" for a bug report.
/// </summary>
/// <remarks>
/// Every value is pulled fresh on each draw, because the point of the window is to answer a question
/// about the present. The rows are fixed-height text, so the layout does not depend on the content.
/// </remarks>
internal sealed class DiagnosticsWindow : PanelWindow
{
    private const int CopyControl = 1;
    private const int OpenLogControl = 2;
    private const int ScrollbarControl = 3;

    private const float Pad = 24f;
    private const float HeaderHeight = 76f;
    private const float FooterHeight = 42f;
    private const float RowHeight = 24f;
    private const float LabelWidth = 176f;

    private readonly Func<DiagnosticsSnapshot> _snapshot;
    private readonly Action _openLogFolder;

    private string? _message;
    private float _scroll;
    private float _contentHeight;

    public DiagnosticsWindow(Func<DiagnosticsSnapshot> snapshot, Action openLogFolder)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(openLogFolder);

        _snapshot = snapshot;
        _openLogFolder = openLogFolder;
    }

    protected override string ClassName => "OverlyricDiagnosticsWindow";

    protected override float DefaultWidthDip => 820f;

    protected override float DefaultHeightDip => 620f;

    protected override float MinWidthDip => 660f;

    protected override float MinHeightDip => 420f;

    protected override string Title => "Diagnostics";

    private float ListTop => HeaderHeight;

    private float ListBottom => HeightDip - FooterHeight;

    public void ShowDiagnostics()
    {
        _scroll = 0f;
        _message = null;
        ShowPanel();
        Invalidate();
    }

    protected override void DrawBody(ID2D1DeviceContext context)
    {
        var snapshot = _snapshot();

        Label(context, "Diagnostics", Pad, 12f, WidthDip - (2 * Pad), 30f, 20f, FontWeight.SemiBold, ColorText);
        Label(
            context,
            "What Overlyric is playing, what it found, and why the overlay is where it is.",
            Pad,
            44f,
            WidthDip - (2 * Pad),
            20f,
            11.5f,
            FontWeight.Normal,
            ColorTextDim);
        Fill(context, R(Pad, HeaderHeight - 1f, WidthDip - Pad, HeaderHeight), ColorLine);

        var rows = BuildRows(snapshot);
        _contentHeight = (rows.Count * RowHeight) + 8f;

        var viewport = MathF.Max(0f, ListBottom - ListTop);
        var maxScroll = MathF.Max(0f, _contentHeight - viewport);
        _scroll = Math.Clamp(_scroll, 0f, maxScroll);

        context.PushAxisAlignedClip(R(0f, ListTop, WidthDip, ListBottom), AntialiasMode.Aliased);

        var y = ListTop + 4f - _scroll;

        foreach (var (label, value, header) in rows)
        {
            if (y + RowHeight >= ListTop && y <= ListBottom)
            {
                if (header)
                {
                    Label(context, label, Pad, y + 4f, WidthDip - (2 * Pad), RowHeight, 11f, FontWeight.SemiBold, ColorAccent);
                }
                else
                {
                    Label(context, label, Pad, y, LabelWidth, RowHeight, 11.5f, FontWeight.Normal, ColorTextFaint);
                    Label(context, value, Pad + LabelWidth, y, WidthDip - (2 * Pad) - LabelWidth - 12f, RowHeight, 11.5f, FontWeight.Normal, ColorText);
                }
            }

            y += RowHeight;
        }

        context.PopAxisAlignedClip();

        if (maxScroll > 0f)
        {
            Scrollbar(context, WidthDip - 16f, ListTop, viewport, viewport, _contentHeight, _scroll, ColorAccent);
            Hit(ScrollbarControl, R(WidthDip - 22f, ListTop, WidthDip - 10f, ListBottom));
        }

        DrawFooter(context, snapshot);
    }

    private static List<(string Label, string Value, bool Header)> BuildRows(DiagnosticsSnapshot snapshot)
    {
        var rows = new List<(string Label, string Value, bool Header)>();

        void Section(string label) => rows.Add((label, string.Empty, true));

        void Row(string label, string value) => rows.Add((label, value, false));

        Section("Session");
        Row("Track", snapshot.HasSession
            ? string.Concat(
                snapshot.Artist ?? string.Empty,
                snapshot.Artist is { Length: > 0 } && snapshot.Title is { Length: > 0 } ? " - " : "",
                snapshot.Title ?? string.Empty)
            : "Nothing playing");
        Row("Player", snapshot.SourceApp ?? "none");
        Row("Playback", snapshot.IsPlaying ? "playing" : "paused");
        Row("Position", snapshot.Position);
        Row("Clock", string.Concat(
            snapshot.ClockIsLive ? "live timeline" : "self-clocked",
            snapshot.PositionIsUsable ? string.Empty : " (position not usable)",
            snapshot.SeekCount > 0 ? $" · {snapshot.SeekCount} seeks" : string.Empty));

        Section("Lyrics");
        Row("Source", snapshot.RecordId is { } id ? $"{snapshot.LyricSource} · lrclib #{id}" : snapshot.LyricSource);
        Row("Sheet", $"{snapshot.LineCount} lines" + (snapshot.Synced ? " · synced" : " · untimed")
            + (snapshot.WordSynced ? " · word timings" : string.Empty));
        Row("Verdict", snapshot.Instrumental ? "instrumental" : snapshot.LyricStatus ?? "showing");

        Section("Timing");
        Row("Offset", $"{snapshot.OffsetLabel} · {snapshot.OffsetSource}");
        Row("Output", snapshot.OutputDevice + (snapshot.OutputBluetooth ? " · Bluetooth" : string.Empty));
        Row("Display", snapshot.Monitor);

        Section("Presence");
        Row("Foreground", snapshot.ForegroundApp ?? "unknown");
        Row("Hidden for app", snapshot.HiddenForApp ? "yes" : "no");
        Row("Fullscreen notice", snapshot.FullscreenNotice ? "active" : "no");

        Section("Storage");
        Row("Lyric cache", $"{snapshot.CacheEntries} entries · {FormatBytes(snapshot.CacheBytes)}");
        Row("Local lyrics", $"{snapshot.LocalFiles} files · {FormatBytes(snapshot.LocalBytes)}");
        Row("Build", snapshot.Version);

        Section("Recent events");

        foreach (var line in snapshot.Log)
        {
            rows.Add((line, string.Empty, false));
        }

        return rows;
    }

    private void DrawFooter(ID2D1DeviceContext context, DiagnosticsSnapshot snapshot)
    {
        Fill(context, R(Pad, ListBottom, WidthDip - Pad, ListBottom + 1f), ColorLine);

        Button(context, CopyControl, "Copy as JSON", R(Pad, ListBottom + 7f, Pad + 120f, ListBottom + 31f), true);
        Button(context, OpenLogControl, "Open log folder", R(Pad + 128f, ListBottom + 7f, Pad + 128f + 126f, ListBottom + 31f), true);

        var message = _message ?? $"Captured {snapshot.CapturedAt:HH:mm:ss} · Esc closes";
        Label(context, message, Pad + 266f, ListBottom + 9f, MathF.Max(60f, WidthDip - Pad - 266f), 20f, 11f, FontWeight.Normal, ColorTextFaint);
    }

    private void Button(ID2D1DeviceContext context, int id, string text, Rect rect, bool enabled)
    {
        var hot = enabled && HoverId == id;
        FillRound(context, rect.Left, rect.Top, rect.Width, rect.Height, 7f, hot ? ColorControlHot : ColorControl);
        Label(context, text, rect.Left, rect.Top, rect.Width, rect.Height, 11.5f, FontWeight.Normal, enabled ? ColorText : ColorTextFaint, TextAlignment.Center);

        if (enabled)
        {
            Hit(id, rect);
        }
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
        : $"{Math.Max(0, bytes) / 1024.0:0} KB";

    protected override void OnActivate(int id)
    {
        switch (id)
        {
            case CopyControl:
                _message = Clipboard.WriteText(Handle, _snapshot().ToJson())
                    ? "Copied the diagnostics JSON to the clipboard."
                    : "The clipboard is busy; try again.";
                Invalidate();
                return;

            case OpenLogControl:
                _openLogFolder();
                return;
        }
    }

    protected override void OnBodyWheel(int notches)
    {
        var viewport = MathF.Max(0f, ListBottom - ListTop);
        var maxScroll = MathF.Max(0f, _contentHeight - viewport);

        if (maxScroll <= 0f || notches == 0)
        {
            return;
        }

        var next = Math.Clamp(_scroll + (notches > 0 ? -72f : 72f) * Math.Max(1, Math.Abs(notches) / 120), 0f, maxScroll);

        if (MathF.Abs(next - _scroll) > 0.5f)
        {
            _scroll = next;
            Invalidate();
        }
    }

    protected override bool OnBodyKey(int virtualKey)
    {
        var viewport = MathF.Max(0f, ListBottom - ListTop);
        var maxScroll = MathF.Max(0f, _contentHeight - viewport);

        if (maxScroll <= 0f)
        {
            return false;
        }

        var next = virtualKey switch
        {
            0x26 => _scroll - 48f,
            0x28 => _scroll + 48f,
            0x21 => _scroll - (viewport * 0.85f),
            0x22 => _scroll + (viewport * 0.85f),
            0x24 => 0f,
            0x23 => maxScroll,
            _ => float.MinValue
        };

        if (next == float.MinValue)
        {
            return false;
        }

        next = Math.Clamp(next, 0f, maxScroll);

        if (MathF.Abs(next - _scroll) > 0.5f)
        {
            _scroll = next;
            Invalidate();
        }

        return true;
    }
}
