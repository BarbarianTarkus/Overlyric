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
| `LyricsOffsetMs` | `0` | Manual timing correction in milliseconds; positive shows lyrics later |
| `FontFamily` | `Segoe UI Variable Display` | Typeface |
| `ActiveFontSize` / `InactiveFontSize` | `30` / `21` | Font sizes in DIPs |
| `Shortcuts` | `{}` | Action name → chord text, e.g. `"ToggleOverlay": "Ctrl+Alt+H"`. A missing entry uses the built-in default and an empty string means deliberately unbound, so a hand-edited file cannot silently lose a shortcut |
| `SourcePriority` | `[]` | Players to prefer, in order, when several publish at once. Each entry is an app user model id plus a label |
| `SourcePinned` | `""` | App user model id of the pinned player, or empty for automatic. A pin outranks the priority list |

Numbers are clamped on load (for example `MultiLineCount` to 1–11 and `LyricsOffsetMs` to
±10,000 ms), so a hand-edited value that is out of range is corrected rather than rejected.

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
many entries are cached, how much space they use, and a **Clear** button for all of them.

## Start with Windows

`StartWithWindows` writes the executable into the current user's
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key. It needs no elevation and no service.
Turning the toggle off removes the value. The per-user uninstaller does not know about a value the app
wrote, so remove it with the toggle before uninstalling, or delete the **Overlyric** value by hand.

## Pinned versions

Manual version choices live in `%LOCALAPPDATA%\Overlyric\versions.json`, keyed by track. It is derived
state rather than settings, so it is kept out of `config.json` and bounded to 256 entries. Delete it to
forget every pinned version.