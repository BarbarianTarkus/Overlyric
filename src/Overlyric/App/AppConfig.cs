using System.Text.Json;
using System.Text.Json.Serialization;
using Overlyric.Overlay;

namespace Overlyric.App;

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

    /// <summary>Draw the bar behind the line being sung.</summary>
    public bool ShowActiveLineHighlight { get; set; } = true;

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

    /// <summary>Manual lyric timing correction in milliseconds; positive shows lyrics later.</summary>
    public int LyricsOffsetMs { get; set; }

    public string FontFamily { get; set; } = "Segoe UI Variable Display";

    public float ActiveFontSize { get; set; } = 30f;

    public float InactiveFontSize { get; set; } = 21f;

    /// <summary>
    /// Rebindable global hotkeys, keyed by action name and stored as text ("Ctrl+Alt+H"). A missing
    /// entry falls back to the built-in default and an empty string means "deliberately unbound",
    /// so a hand-edited file can never silently lose a shortcut.
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    [JsonIgnore]
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Overlyric");

    [JsonIgnore]
    public static string ConfigPath { get; } = Path.Combine(DataDirectory, "config.json");

    [JsonIgnore]
    public static string CacheDirectory { get; } = Path.Combine(DataDirectory, "cache");

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

        return new AppConfig();
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
        ActiveFontSize = Math.Clamp(ActiveFontSize, 12f, 72f);
        InactiveFontSize = Math.Clamp(InactiveFontSize, 10f, 72f);
        OffsetY = Math.Clamp(OffsetY, -4000, 4000);
        OffsetX = Math.Clamp(OffsetX, -4000, 4000);
        LyricsOffsetMs = Math.Clamp(LyricsOffsetMs, -10000, 10000);

        if (string.IsNullOrWhiteSpace(FontFamily))
        {
            FontFamily = "Segoe UI Variable Display";
        }

        return this;
    }

    public OverlayTheme ToTheme() => new()
    {
        FontFamily = FontFamily,
        ActiveFontSize = ActiveFontSize,
        InactiveFontSize = InactiveFontSize,
        MultiLineCount = MultiLineCount,
        BackgroundColor = ScaleAlpha(0xB4101014, BackgroundOpacity),
        // The surrounding lines keep their hue and lose their alpha, so "dim" means dimmer rather
        // than a different colour.
        InactiveTextColor = ScaleAlpha(0xFFF5F7FA, InactiveLineOpacity),
        ShowActiveLineHighlight = ShowActiveLineHighlight,
        TextOutline = TextOutline
    };

    private static uint ScaleAlpha(uint argb, double factor)
    {
        var alpha = (uint)(((argb >> 24) & 0xFF) * Math.Clamp(factor, 0.0, 1.0));
        return (alpha << 24) | (argb & 0x00FFFFFF);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
