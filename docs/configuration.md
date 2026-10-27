# Configuration

Every setting is stored as JSON and can be edited by hand; the settings window writes the same file
live. If the file is missing or malformed, the app starts with defaults instead of refusing to run.

## Settings file

`%LOCALAPPDATA%\Overlyric\config.json`

Settings → Advanced has **Open** buttons for this folder and the lyric cache.

| Key | Default | Meaning |
| --- | --- | --- |
| `Mode` | `SingleLine` | `SingleLine`, `SingleLineWithNext` or `MultiLine` |
| `Anchor` | `BottomCenter` | `BottomCenter`, `TopCenter` or `Center` |
| `OffsetX` / `OffsetY` | `0` / `90` | Pixel offset from the anchor |
| `Locked` | `true` | Locked is click-through; unlocked is draggable and takes the wheel |
| `PlacementVersion` | `0` | Bookkeeping: the offsets have been converted to anchor-relative ones (see [Moving and locking the overlay](usage.md#moving-and-locking-the-overlay)) |
| `ShowHeader` | `true` | Show `Artist — Title` above the lyrics |
| `ShowProgressBar` | `true` | Show the track progress bar |
| `MultiLineCount` | `5` | Lines visible in multi-line mode, 1–11 |
| `BackgroundOpacity` | `0.7` | Backdrop opacity, 0.0–1.0. At 0 the backdrop is not drawn at all |
| `HeaderAutoHideSeconds` | `0` | Seconds before the track info fades out; 0 keeps it for the whole track |
| `InfoPopupSeconds` | `5` | Seconds the song-start pill stays up; 0 turns it off |
| `NotifyWhenLyricsMissing` | `true` | Notify once per track when LRCLIB has no lyrics, or words without timings |
| `ShowActiveLineHighlight` | `true` | Draw the highlight bar behind the active line |
| `ShowTranslation` | `true` | Draw the optional `.trans.lrc` sidecar under the active line |
| `InactiveLineOpacity` | `0.54` | Opacity of the surrounding lines, 0.05–1.0 |
| `PixelShift` | `false` | Nudge the drawing a few pixels once a minute, against burn-in |
| `TextOutline` | `false` | Hard outline instead of a soft shadow, for a transparent backdrop |
| `Karaoke` | `true` | Sweep the active line as it is sung |
| `KaraokeBlackout` | `false` | Draw the not-yet-sung part of the active line in pure black instead of the dim colour |
| `HideWhenPaused` | `false` | Hide while playback is paused |
| `HideWhenNoLyrics` | `true` | Hide when LRCLIB has no lyrics for the track |
| `HideWhenInstrumental` | `true` | Hide when the track is instrumental |
| `WarnWhenPlayerDoesNotPublish` | `true` | Say so when audio is playing but no player publishes to Windows media controls, or when a player publishes a track without a playback position |
| `StartWithWindows` | `false` | Write the executable into the current user's Run key |
| `HideApps` | `[]` | Executable names that hide the overlay while they own the foreground window, e.g. `["Teams.exe", "zoom"]`. Names only, case-insensitive |
| `Monitor` | `Cursor` | Which display the overlay prefers: `Cursor`, `Primary` or `Specific` |
| `MonitorDevice` | `""` | GDI device name for `Specific`, e.g. `\\.\DISPLAY2`. Display numbers are reassigned, so the name is the key and the number is not |
| `MonitorOffsets` | `{}` | Remembered placement per display device name (`X`, `Y`), so docking and undocking keeps the overlay where it was put |
| `LyricsOffsetMs` | `0` | Global timing correction in milliseconds; positive shows lyrics later. The fallback when the track or the output has no saved correction |
| `TrackOffsets` | `{}` | Per-track corrections in milliseconds, keyed by a normalised artist/title key. An entry overrides the output profile and the global default |
| `OutputOffsets` | `{}` | Per-output-device corrections, keyed by the CoreAudio endpoint id; each value carries `Name`, `OffsetMs` and `Bluetooth`. Used when the track has no override |
| `RememberTrackOffset` | `true` | A timing nudge is saved for the current track. Off makes nudges session-only; holding Shift has the same effect per nudge |
| `Theme` | `Default` | Curated palette: `Default`, `Warm`, `Cool` or `Mono` |
| `StageMode` | `false` | Full-monitor audience display with large centred lyrics and an opaque backdrop |
| `StageChroma` | `false` | Stage backdrop as chroma-key green instead of black |
| `ActiveLyricWeight` | `SemiBold` | Weight of the line being sung: `Normal`, `SemiBold` or `Bold` |
| `FontFamily` | `Segoe UI Variable Display` | Typeface |
| `ActiveFontSize` / `InactiveFontSize` | `30` / `21` | Font sizes in DIPs |
| `Shortcuts` | `{}` | Action name → chord text, e.g. `"ToggleOverlay": "Ctrl+Alt+H"`. A missing entry uses the built-in default and an empty string means deliberately unbound, so a hand-edited file cannot silently lose a shortcut |
| `SourcePriority` | `[]` | Players to prefer, in order, when several publish at once. Each entry is an app user model id plus a label |
| `SourcePinned` | `""` | App user model id of the pinned player, or empty for automatic. A pin outranks the priority list |

Numbers are clamped on load (for example `MultiLineCount` to 1–11 and every timing offset to
±10,000 ms), so a hand-edited value that is out of range is corrected rather than rejected. The two
timing maps are also repaired: blank keys, null profiles and duplicate whitespace are dropped, names
are refreshed from the device, and the maps are capped (512 tracks, 32 outputs).

## Timing profiles

A timing correction belongs to the narrowest thing that explains it, and the layers override rather
than add:

```text
track override  ->  output profile  ->  global default (LyricsOffsetMs)
```

A nudge is saved for the track by default, so a correction survives replaying the song. A saved
output profile is used when the track has no override; it follows the default output device, which is
re-read every five seconds and on every track change. A stored zero is an override too: it means
"this track needs no correction" rather than "fall through".

```json
{
  "LyricsOffsetMs": 0,
  "RememberTrackOffset": true,
  "TrackOffsets": { "tidal\u001fa song for all seasons": 250 },
  "OutputOffsets": {
    "\\?\SWD\\MMDEVAPI\\{0.0.0.0000.00000000}.{endpoint-guid}": {
      "Name": "WH-1000XM5",
      "OffsetMs": 150,
      "Bluetooth": true
    }
  }
}
```

Window output profiles are not shown as separate top-level keys because the endpoint id is long and
machine-specific; the settings window writes them and `Settings → Timing` reads the name back.

## Cache

Fetched lyrics are cached as one JSON file per track in `%LOCALAPPDATA%\Overlyric\cache`, including
negative entries, so a track LRCLIB has never heard of is not re-queried on every replay.

Each entry also records the version of the matching rules that produced it. When those rules change —
the release-suffix fix and the synced-same-duration rule both did — an entry written by the older rules
is not wrong on its face, but it is the answer the old rules gave, and serving it would hide the fix
from tracks that were already played. So an entry from an older matcher reads as a miss: the track is
resolved once more against LRCLIB and the entry is rewritten. A negative tombstone is bookkeeping of the
same kind and is aged out the same way, because a search that has since learned to ask a better question
may well find what the old one missed. Like `PlacementVersion`, this is bookkeeping rather than a
setting.

Deleting a cache file is safe: the next play looks the track up again. The tray's **Refresh lyrics**
does the same for the current track without opening the folder, and **Settings → Advanced** shows how
many entries are cached, how much space they use, and a **Clear** button for all of them. Refresh
never discards an explicit local file or pinned record: the selection is the listener's decision, and
only the automatic answer behind it is discarded.

## Local lyric files

Imported sheets live under `%LOCALAPPDATA%\Overlyric\lyrics`, one pair per track:

```text
lyrics\<sha256 of the track key>.lrc    the lyric document, exactly as imported
lyrics\<sha256 of the track key>.json   artist, title, album, source path, added
selections.json                          track key -> explicit choice
```

The file name is a hash of the track key (artist and title, normalised), so arbitrary metadata never
becomes a path. The `.json` sidecar keeps the folder readable when it is opened from **Settings →
Advanced**. A file the listener deletes from there simply stops being found: the selection falls back
to the automatic pipeline and the app says so once for that track.

`selections.json` is a bounded map (512 entries) with one entry per track:

```json
{
  "tidal\na song for all seasons\n": {
    "Kind": "Local",
    "Path": "a song for all seasons.lrc",
    "RecordId": 0,
    "AddedAt": 1793000000
  }
}
```

`Kind` is `Local`, `Lrclib` or `Automatic`; the last explicit action wins, and deleting the file is
safe - it only forgets the choices. The older `versions.json` (pinned record ids) is read once and
migrated into `selections.json`; the legacy file is left untouched, and deleting it afterwards is
safe too.

Import accepts UTF-8, UTF-16 and Windows-1252 text, with or without a BOM. Export writes
CRLF, `[mm:ss.fff]` timestamps and the `[ar:]`/`[ti:]`/`[al:]`/`[length:]` header, so it can be
re-imported without losing a millisecond. Other ANSI code pages (Cyrillic, Greek, CJK) are not
decoded; save those files as UTF-8.

## Diagnostics log

The rolling decision log lives at `%LOCALAPPDATA%\Overlyric\diagnostics.log`. It records track
changes, lookup verdicts, timing nudges and Presence decisions, and it is the same content the
diagnostics window's "Copy as JSON" includes. It is capped at 256 KB and cut in half when it passes
that size; delete it at any time and it starts again.

## Start with Windows

`StartWithWindows` writes the executable into the current user's
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key. It needs no elevation and no service.
Turning the toggle off removes the value. The per-user uninstaller does not know about a value the app
wrote, so remove it with the toggle before uninstalling, or delete the **Overlyric** value by hand.

## Explicit choices

Manual version choices used to live in `%LOCALAPPDATA%\Overlyric\versions.json`. They now live in
`selections.json` alongside local files (see [Local lyric files](#local-lyric-files)); the legacy map
is migrated on first load. The choices are derived state rather than settings, so they stay out of
`config.json`; delete `selections.json` to forget every explicit choice and return every track to
automatic matching.