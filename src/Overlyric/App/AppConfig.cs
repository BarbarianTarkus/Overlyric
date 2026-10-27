using System.Text.Json;
using System.Text.Json.Serialization;
using Overlyric.Media;
using Overlyric.Overlay;

namespace Overlyric.App;

/// <summary>A curated colour palette for the overlay text and accents.</summary>
public enum ThemePreset
{
    Default = 0,
    Warm = 1,
    Cool = 2,
    Mono = 3
}

/// <summary>Weight of the line being sung.</summary>
public enum LyricWeight
{
    Normal = 0,
    SemiBold = 1,
    Bold = 2
}

/// <summary>User settings, persisted as JSON under %LOCALAPPDATA%\Overlyric.</summary>
internal sealed class AppConfig
{
    public OverlayMode Mode { get; set; } = OverlayMode.SingleLine;

    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.BottomCenter;

    public int OffsetX { get; set; }

    /// <summary>
    /// Locked means the overlay is click-through: the mouse goes to whatever is behind it. Unlocked
    /// it can be dragged and takes the wheel for opacity, so it captures clicks over the lyrics
    /// while it is in that state. Persisted, because "why is my click being eaten" is a question
    /// nobody should have to ask twice.
    /// </summary>
    public bool Locked { get; set; } = true;

    /// <summary>Distance in physical pixels from the anchored screen edge.</summary>
    public int OffsetY { get; set; } = 90;

    /// <summary>
    /// Bookkeeping rather than a setting: 1 means <see cref="OffsetX"/>/<see cref="OffsetY"/> are
    /// already relative to the anchor. Files written before that was true stored the overlay's raw
    /// position, and <c>AppHost</c> repairs them once at startup. A fresh config - the default 0 -
    /// is unaffected by the repair, which is why the repair can safely run on it.
    /// </summary>
    public int PlacementVersion { get; set; }

    public bool ShowHeader { get; set; } = true;

    public bool ShowProgressBar { get; set; } = true;

    /// <summary>Multi-line mode only: how many lines to show at once.</summary>
    public int MultiLineCount { get; set; } = 5;

    /// <summary>0.0 - 1.0, applied to the overlay backdrop. Zero removes it entirely.</summary>
    public double BackgroundOpacity { get; set; } = 0.7;

    /// <summary>
    /// Seconds after a track starts before the track info fades out; 0 keeps it for the whole
    /// track. It is the most static text on screen, so it is the first thing an OLED wants gone.
    /// </summary>
    public int HeaderAutoHideSeconds { get; set; }

    /// <summary>
    /// Seconds the info pop-up stays on screen when a song starts or the overlay appears; 0 turns it
    /// off. It says the song and whether the lyric sheet carries word timings, line timings, no
    /// timings at all, or nothing - the question "is this as good as it gets" answered without
    /// opening anything.
    /// </summary>
    public int InfoPopupSeconds { get; set; } = 5;

    /// <summary>
    /// Send a Windows notification when lrclib has no lyrics for the track, or has the words but no
    /// timings. Both are invisible otherwise - the overlay is hidden or simply never moves - and
    /// both are reported once per track rather than once per retry.
    /// </summary>
    public bool NotifyWhenLyricsMissing { get; set; } = true;

    /// <summary>Draw the bar behind the line being sung.</summary>
    public bool ShowActiveLineHighlight { get; set; } = true;

    /// <summary>Show the optional second-language sidecar under the active line.</summary>
    public bool ShowTranslation { get; set; } = true;

    /// <summary>
    /// Full-monitor audience display for parties and streams: opaque backdrop, large centred
    /// lyrics, no header or progress bar. Off by default; it is a different surface, not a theme.
    /// </summary>
    public bool StageMode { get; set; }

    /// <summary>Stage backdrop as chroma-key green instead of black, for stream compositing.</summary>
    public bool StageChroma { get; set; }

    /// <summary>
    /// Sweep the active line as it is sung. Follows word-level timings when the sheet has them and
    /// shares the line's span out over its syllables when it does not.
    /// </summary>
    public bool Karaoke { get; set; } = true;

    /// <summary>
    /// Draw the not-yet-sung part of the active line in pure black instead of the dim inactive
    /// colour. On an OLED panel black is an unlit pixel, so the sweep is the only thing that lights
    /// up and the half of the line that is standing still emits nothing. Does nothing when
    /// <see cref="Karaoke"/> is off: there is no unsung half to draw then.
    /// </summary>
    public bool KaraokeBlackout { get; set; }

    /// <summary>Opacity of the lines around the active one, 0.05 - 1.0.</summary>
    public double InactiveLineOpacity { get; set; } = 0.54;

    /// <summary>Nudge the drawn content a few pixels once a minute, so no pixel is always lit.</summary>
    public bool PixelShift { get; set; }

    /// <summary>
    /// A hard outline instead of a soft shadow. Without a backdrop the shadow is not enough to keep
    /// light text legible over bright video, and an outline is.
    /// </summary>
    public bool TextOutline { get; set; }

    public bool HideWhenPaused { get; set; }

    /// <summary>Hide when lrclib has no lyrics for the track at all.</summary>
    public bool HideWhenNoLyrics { get; set; } = true;

    /// <summary>
    /// Hide when the track is instrumental. Kept separate from <see cref="HideWhenNoLyrics"/> because
    /// "this track has no words" and "we could not find the words" are different answers.
    /// </summary>
    public bool HideWhenInstrumental { get; set; } = true;

    /// <summary>
    /// Say so when audio is playing but no player publishes to Windows media controls, or when a
    /// player publishes a track without a playback position. Both look exactly like Overlyric being
    /// broken, so the default is on.
    /// </summary>
    public bool WarnWhenPlayerDoesNotPublish { get; set; } = true;

    /// <summary>
    /// Register the executable in the current user's Run key. Per-user and no elevation, in keeping
    /// with the rest of the application's state; the MSI cannot remove a value the app wrote, so the
    /// uninstall path is documented rather than guessed at.
    /// </summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Executable names that make the overlay hide while they own the foreground window. Names only,
    /// not paths: updates move programs around, and the match is case-insensitive.
    /// </summary>
    public List<string> HideApps { get; set; } = [];

    /// <summary>Which display the overlay prefers at startup and after a display change.</summary>
    public MonitorChoice Monitor { get; set; } = MonitorChoice.Cursor;

    /// <summary>
    /// The GDI device name for <see cref="MonitorChoice.Specific"/>, e.g. <c>\\.\DISPLAY2</c>. The
    /// name is stable across reconnects; the display number in it is not, so it is only a key.
    /// </summary>
    public string MonitorDevice { get; set; } = "";

    /// <summary>Last placement on each display, keyed by device name, so a dock does not move it.</summary>
    public Dictionary<string, MonitorPlacement> MonitorOffsets { get; set; } = [];

    /// <summary>Manual lyric timing correction in milliseconds; positive shows lyrics later.</summary>
    public int LyricsOffsetMs { get; set; }

    /// <summary>
    /// Per-track corrections, keyed by <see cref="TimingProfiles.TrackKey"/>. A stored entry is an
    /// override, not an addition: it wins over the output profile and over the global default.
    /// </summary>
    public Dictionary<string, int> TrackOffsets { get; set; } = [];

    /// <summary>
    /// Per-output-device corrections, keyed by the CoreAudio endpoint id. The name and the Bluetooth
    /// flag are carried with the value so the UI can explain it without re-reading the device.
    /// </summary>
    public Dictionary<string, OutputOffsetProfile> OutputOffsets { get; set; } = [];

    /// <summary>
    /// Whether a timing nudge is saved for the current track. Off means the nudge lasts for the
    /// session only; on is the default because "fix the offset once, not every song" is the job.
    /// </summary>
    public bool RememberTrackOffset { get; set; } = true;

    public string FontFamily { get; set; } = "Segoe UI Variable Display";

    /// <summary>The curated palette; the OLED preset still decides the backdrop and burn-in levers.</summary>
    public ThemePreset Theme { get; set; } = ThemePreset.Default;

    /// <summary>Weight of the active line. The surrounding lines stay normal.</summary>
    public LyricWeight ActiveLyricWeight { get; set; } = LyricWeight.SemiBold;

    public float ActiveFontSize { get; set; } = 30f;

    public float InactiveFontSize { get; set; } = 21f;

    /// <summary>
    /// Rebindable global hotkeys, keyed by action name and stored as text ("Ctrl+Alt+H"). A missing
    /// entry falls back to the built-in default and an empty string means "deliberately unbound",
    /// so a hand-edited file can never silently lose a shortcut.
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    /// <summary>
    /// The players to prefer, in order, when more than one is publishing to Windows media controls.
    /// Empty - the default - means "follow whatever Windows calls current", which is what the app did
    /// before this existed, so an old config keeps behaving exactly as it did.
    /// </summary>
    public List<SourcePreference> SourcePriority { get; set; } = [];

    /// <summary>
    /// App user model id of the player the user pinned, or empty for automatic. A pin outranks the
    /// priority list: it is the user saying "this one, now", and it survives a restart because a
    /// choice that has to be made again every morning is not a setting.
    /// </summary>
    public string SourcePinned { get; set; } = "";

    [JsonIgnore]
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Overlyric");

    [JsonIgnore]
    public static string ConfigPath { get; } = Path.Combine(DataDirectory, "config.json");

    [JsonIgnore]
    public static string CacheDirectory { get; } = Path.Combine(DataDirectory, "cache");

    /// <summary>Where imported local lyric sheets and their metadata live.</summary>
    [JsonIgnore]
    public static string LyricsDirectory { get; } = Path.Combine(DataDirectory, "lyrics");

    /// <summary>
    /// True when <see cref="Load"/> found no config file. Not persisted: it only decides whether the
    /// first-run notice is worth showing on this launch.
    /// </summary>
    [JsonIgnore]
    public bool IsFirstRun { get; private set; }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
                if (loaded is not null)
                {
                    return loaded.Normalise();
                }
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable config must never stop the app from starting.
        }

        return new AppConfig { IsFirstRun = true };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var json = JsonSerializer.Serialize(this, ConfigJsonContext.Default.AppConfig);

            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, ConfigPath, overwrite: true);
        }
        catch (Exception)
        {
            // Losing a settings write is not worth interrupting playback for.
        }
    }

    private AppConfig Normalise()
    {
        MultiLineCount = Math.Clamp(MultiLineCount, 1, 11);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.0, 1.0);
        InactiveLineOpacity = Math.Clamp(InactiveLineOpacity, 0.05, 1.0);
        HeaderAutoHideSeconds = Math.Clamp(HeaderAutoHideSeconds, 0, 120);
        InfoPopupSeconds = Math.Clamp(InfoPopupSeconds, 0, 20);
        ActiveFontSize = Math.Clamp(ActiveFontSize, 12f, 72f);
        InactiveFontSize = Math.Clamp(InactiveFontSize, 10f, 72f);

        if (Theme is not (ThemePreset.Default or ThemePreset.Warm or ThemePreset.Cool or ThemePreset.Mono))
        {
            Theme = ThemePreset.Default;
        }

        if (ActiveLyricWeight is not (LyricWeight.Normal or LyricWeight.SemiBold or LyricWeight.Bold))
        {
            ActiveLyricWeight = LyricWeight.SemiBold;
        }
        OffsetY = Math.Clamp(OffsetY, -4000, 4000);
        OffsetX = Math.Clamp(OffsetX, -4000, 4000);
        LyricsOffsetMs = TimingProfiles.Clamp(LyricsOffsetMs);

        // The config is editable by hand, so the two maps have to survive whatever they contain: a
        // blank key, a null profile, a huge value or ten thousand entries all degrade to something
        // the resolver can use. A missing map is normal for a file written before profiles existed.
        if (TrackOffsets is null)
        {
            TrackOffsets = [];
        }

        if (OutputOffsets is null)
        {
            OutputOffsets = [];
        }

        TimingProfiles.NormaliseTrackOffsets(TrackOffsets);
        TimingProfiles.NormaliseOutputOffsets(OutputOffsets);

        if (string.IsNullOrWhiteSpace(FontFamily))
        {
            FontFamily = "Segoe UI Variable Display";
        }

        // The config file is editable by hand, so both halves of the source setting have to survive
        // whatever it contains: a list of duplicates and blanks degrades to a usable order, and a pin
        // that names no player simply stops matching.
        SourcePriority = SourceSelector.NormalisePriority(SourcePriority);
        SourcePinned = (SourcePinned ?? string.Empty).Trim();

        // A mode or device the app does not understand degrades to the cursor rather than to a
        // black screen on a monitor that no longer exists.
        if (Monitor is not (MonitorChoice.Cursor or MonitorChoice.Primary or MonitorChoice.Specific))
        {
            Monitor = MonitorChoice.Cursor;
        }

        MonitorDevice = (MonitorDevice ?? string.Empty).Trim();

        if (Monitor == MonitorChoice.Specific && MonitorDevice.Length == 0)
        {
            Monitor = MonitorChoice.Cursor;
        }

        MonitorOffsets = MonitorSettings.Normalise(MonitorOffsets);
        HideApps = PresenceRules.NormaliseHideApps(HideApps);

        return this;
    }

    /// <summary>
    /// The OLED bundle in one place: no backdrop, no progress bar, the static header fades out, no
    /// highlight bar, dimmer surrounding lines, the unsung half of the current line unlit, pixel
    /// shift on, outline text on, and hide when paused. The sweep is included because the blackout
    /// has nothing to draw without it.
    /// </summary>
    public void ApplyOledPreset()
    {
        BackgroundOpacity = 0.0;
        ShowProgressBar = false;
        HeaderAutoHideSeconds = 8;
        ShowActiveLineHighlight = false;
        InactiveLineOpacity = 0.4;
        Karaoke = true;
        KaraokeBlackout = true;
        PixelShift = true;
        TextOutline = true;
        HideWhenPaused = true;
    }

    public OverlayTheme ToTheme()
    {
        // The palette moves the text and accent hues; the surrounding lines keep their hue and lose
        // their alpha, so "dim" still means dimmer rather than a different colour.
        var (activeText, accent) = Theme switch
        {
            ThemePreset.Warm => (0xFFFFF1E0, 0xFFFFB870),
            ThemePreset.Cool => (0xFFEAF4FF, 0xFF8CCBFF),
            ThemePreset.Mono => (0xFFF2F2F2, 0xFFD8DEE9),
            _ => (0xFFF5F7FA, 0xFF7CC4FF)
        };

        var inactive = ScaleAlpha(activeText, InactiveLineOpacity);
        var stage = StageMode;

        return new OverlayTheme
        {
            StageMode = stage,
            FontFamily = FontFamily,
            ActiveFontSize = stage ? MathF.Max(ActiveFontSize * 1.7f, 56f) : ActiveFontSize,
            InactiveFontSize = stage ? MathF.Max(InactiveFontSize * 1.5f, 36f) : InactiveFontSize,
            ActiveFontWeight = ActiveLyricWeight switch
            {
                LyricWeight.Normal => Vortice.DirectWrite.FontWeight.Normal,
                LyricWeight.Bold => Vortice.DirectWrite.FontWeight.Bold,
                _ => Vortice.DirectWrite.FontWeight.SemiBold
            },
            MultiLineCount = stage ? 7 : MultiLineCount,
            BackgroundColor = stage
                ? StageChroma ? 0xFF00FF00u : 0xFF000000u
                : ScaleAlpha(0xB4101014, BackgroundOpacity),
            ActiveTextColor = activeText,
            InactiveTextColor = inactive,
            HeaderTextColor = ScaleAlpha(activeText, 0.6),
            ProgressColor = ScaleAlpha(accent, 0.8),
            NoticeTitleColor = activeText,
            NoticeAccentColor = accent,
            ControlTextColor = activeText,
            ControlMutedColor = ScaleAlpha(activeText, 0.6),
            ControlAccentColor = accent,
            // The unsung half of the swept line is normally as dim as the lines around it. The
            // OLED preset makes it pure black instead, so it is an unlit pixel on the panel rather
            // than a second, static piece of grey text waiting to be sung.
            KaraokeUnlitColor = KaraokeBlackout ? 0xFF000000 : inactive,
            ShowActiveLineHighlight = stage ? false : ShowActiveLineHighlight,
            TextOutline = stage || TextOutline
        };
    }

    private static uint ScaleAlpha(uint argb, double factor)
    {
        var alpha = (uint)(((argb >> 24) & 0xFF) * Math.Clamp(factor, 0.0, 1.0));
        return (alpha << 24) | (argb & 0x00FFFFFF);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
