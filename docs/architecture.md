# How Overlyric works

This is the deep dive: where the track comes from, how the clock stays honest when a player does not,
and how the pixels reach the screen without a UI framework. Lyric matching has its own page
([Lyrics](lyrics.md)); the short version is the [README](../README.md).

## The pipeline

```
┌──────────────────────────┐
│ TIDAL / Spotify / Chrome │   any app that integrates with the Windows
│ Windows Media Player ... │   media controls (SMTC)
└────────────┬─────────────┘
             │  Windows.Media.Control
             ▼
   ┌────────────────────┐
   │ MediaSessionWatcher│  title, artist, album, duration, play state
   └─────────┬──────────┘
             │
             ▼
   ┌────────────────────┐        ┌──────────────┐
   │   PlaybackClock    │◀──────▶│  lrclib.net  │
   │  (adaptive timing) │        │  /api/get    │
   └─────────┬──────────┘        │  /api/search │
             │                   └──────┬───────┘
             │                          │
             │                   ┌──────▼───────┐
             │                   │ LyricsCache  │  %LOCALAPPDATA%
             │                   └──────────────┘
             ▼
   ┌────────────────────┐
   │   OverlayWindow    │  WS_EX_NOREDIRECTIONBITMAP + DirectComposition
   │  LyricsRenderer    │  Direct2D + DirectWrite
   └────────────────────┘
```

## Player-agnostic by design

There is no TIDAL plugin, no Spotify plugin. Windows exposes **GSMTC**
(`Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`), the same source that
drives the media keys, the volume-flyout artwork and the lock-screen controls. Any application that
registers with it shows up automatically: TIDAL, Spotify, Edge/Chrome/Firefox (YouTube, SoundCloud),
VLC, MusicBee, foobar2000, the built-in Media Player, and so on.

**A player that does not register cannot be supported; that is the one thing Overlyric cannot work
around.** AIMP is the standard example: nothing in `AIMP.exe` or its DLLs refers to `Windows.Media`
or `SystemMediaTransportControls` at all, so with AIMP 5.4.0.2727 playing, GSMTC reports zero
sessions — `probe\SmtcProbe` prints `NO_SESSION` — and there is no setting to enable. AIMP does
answer the media *keys*, through `aimp_mediakeys.dll`, but that is a different mechanism: handling
the keys does not publish what is playing, and publishing is what Windows shows in the media flyout
and what Overlyric reads.

**A plug-in can fix the first half of that and leave the second.** The AIMP community plug-in
"Windows Media Control" (`aimp_win10_mediacontrol`, v2.0.0.4) makes AIMP register, and the source
app, the title and the play state do arrive. The timeline does not: measured with `probe\SmtcProbe`,
that session holds `position = 00:00:00`, `end = 00:00:00` and `LastUpdatedTime` at `1601-01-01` —
the zero of FILETIME — for the entire track, changing at no point. A track published without a
position is no more usable for synced lyrics than nothing published at all, so Overlyric says so
instead of parking on the first line; see [When nothing reaches
Overlyric](#when-nothing-reaches-overlyric).

The plug-ins are not equivalent, so it is worth measuring rather than trusting the name.
`aimp-smtc-sync` (v0.2.0) publishes a full, live timeline — measured with `probe\SmtcProbe`,
position and `LastUpdatedTime` both advance, 82 timeline events in eight seconds, and its position
agrees with AIMP's own to within 10 ms — and synced lyrics follow it exactly as they follow Spotify.
That is the difference between a plug-in that makes AIMP *visible* and one that makes it *usable*.

Polling is deliberately shaped to stay cheap. `TryGetMediaPropertiesAsync` is by far the most
expensive call in the loop, and it only needs to run when the *track* changes — which can be detected
from the track duration, a plain property read that cannot change within a track. So the expensive
call is gated on the duration changing, plus a slow safety net for players that behave unusually,
while playback state and position are read cheaply every second. Without that gate the property call
dominated the managed heap long before the collector ran.

## Choosing which player to follow

Windows has no API for this. `GetCurrentSession()` is the session the system believes the user would
most likely want to control — the one the media keys apply to — which is a guess about intent, not a
fact about audio. Measured with `probe\SmtcProbe --sessions`, with AIMP paused and a browser video
open: Windows held the browser session as current for the whole window and never switched, while that
session reported `Playing` with its position frozen at `00:00:06` and a timestamp an hour old.

So the overlay decides for itself, in a fixed order: the player the user **pinned**, then their
**priority list** in order, then whatever Windows calls current. A pin deliberately outranks even a
playing source — it is the user saying "this one", and a setting that second-guesses that is not worth
having. Nothing in the list matching falls back to Windows rather than to silence, so configuring a
list can never leave the overlay with nothing to follow, and with both empty the watcher follows
`GetCurrentSession()` exactly as it did before the setting existed.

The order is edited in Settings → Players: six fixed slots, moved with the arrows. The cap is the
list's shape rather than the window's height - the window scrolls now - but a list that grew past six
would still be a list nobody finishes editing. The pin is one click in the
tray menu's **Players** submenu, which lists every player publishing at that moment. Both are stored in
`config.json` as the app user model id plus a label, so the list stays readable while a player is
closed — the identity is a string like `Brave._crx_…` that nobody would recognise.
`probe\SourcePreferenceTests` covers the rule in 46 checks: pin beating list and Windows, order
respected, a listed player that is not running skipped, two sessions under one app id resolved to the
one actually playing, and a hand-edited list cleaned and capped without throwing.

## The adaptive playback clock

This is the part that makes synced lyrics actually work in the real world, and it exists because of a
concrete finding while building this:

> **TIDAL reports metadata to Windows but never updates the timeline.** Measured over 90 seconds of
> continuous playback, TIDAL's SMTC session held `position = 00:00:00.011`, `LastUpdatedTime` frozen
> at the moment the track started, and raised **zero** `TimelinePropertiesChanged`,
> `MediaPropertiesChanged` or `PlaybackInfoChanged` events.

So `PlaybackClock` does not assume anything. It observes each player at runtime and adapts:

| Player behaviour | What Overlyric does |
| --- | --- |
| Publishes a progressing timeline (browsers, Spotify) | Re-anchors to it, but only snaps when drift exceeds 400 ms, which suppresses jitter |
| Publishes a frozen timeline (TIDAL) | Self-clocks from a `Stopwatch`, anchored to the reported position aged by the wall clock since `LastUpdatedTime` |
| Detects a track change | Resets the anchor and liveness detection; the host then re-applies the persisted manual offset |

The clock clears its own correction on a new track. The host owns the persisted value, so it pushes
it back on the same revision change that starts the lyric lookup. Without that step the control bar would keep claiming `+0.25 s` while the new song played
unshifted.

Because the clock is a monotonic `Stopwatch` rather than a polled counter, it stays accurate to well
under one lyric line across an entire song.

**The reported position is a snapshot, not "now", and ageing it is what makes a mid-song start
work.** `Position` is only true at `LastUpdatedTime`. For a player that keeps its timeline current
the two differ by a fraction of a second, but TIDAL stamps both once when the track begins and never
touches them again — so opening Overlyric mid-song read `00:00:00.017` while the track was minutes
in, and the lyrics restarted from the first line. Adding the wall-clock age recovers the real
position: the probe read `position = 00:00:00.017` with `LastUpdatedTime = 19:38:59.716`, which at
19:52:47 places the track at 13:48. Started 16:39 into that track, the overlay showed
`Sheherazade this day is yours`, and four minutes later `The finest silk made in the land` — in each
case the line the synced lyrics give for the position the wall clock implies, where the raw number
had shown `The Sultan` from 3:42. Ageing is applied only while playing, because a paused player's
position is not a stale anchor but the exact spot it stopped at; a track that was *already* paused
when Overlyric started is therefore seeded once, from the first observation taken while it plays.
`probe\ClockTests` pins all of this down.

A stamp is also only used when it can be one: it must be a real date within the last six hours. A
player that publishes no timeline tends to leave the field at zero, which Windows hands over as
`1601-01-01`, and ageing *that* would put the track four centuries in — last line of the song,
progress bar full. Rejecting it falls back to the raw position, and the clock then reports that its
position is not usable, which is what the next section is about.

## Timing profiles

A timing error comes from the record, the player and the audio path, and those do not vary together:
the same track can be 250 ms late on speakers and 100 ms late on headphones. One global correction
is the right first feature and the wrong final one, so the correction is resolved in three
**overriding** layers — track, then output device, then the global default.

`TimingProfiles` owns the rule and is deliberately free of Win32 and of the config type, so
`probe\TimingTests` checks the order, the clamping and the labels without a desktop. A stored zero
counts as an override: "this track needs no correction" and "no entry for this track" are different
answers, and silently falling through would resurrect a device correction the listener had removed.

The output device comes from CoreAudio, the same layer that already answers the audible-session
question. `AudioEndpoint` reads `IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eConsole)`,
then the endpoint id, the friendly name and the enumerator name (which marks Bluetooth). It polls on
the five-second path and on every track change, not per frame: a device switch is rare, and the
CPU budget is the reason the tick is adaptive in the first place. The id is the key; the friendly
name is only a label, because two outputs can share one and users rename them. A Bluetooth endpoint
is never given a guessed offset — Windows reports engine latency, not codec latency.

A nudge writes the track layer. Holding Shift, turning **Remember this track** off, or nudging with
no usable track metadata instead writes a session-only correction: it is added to the resolved value
for as long as the track or output lasts and is never persisted, so a one-off adjustment cannot
quietly rewrite what the next play will use. The control bar, the shortcut toast and the Timing
section all print the layer in force next to the number.

## Fitting the machine

The overlay can follow the pointer, stay on the primary display, or be pinned to one named monitor.
The identity that is stored is the monitor's GDI device name (`MONITORINFOEXW.szDevice`), not its
number: Windows reassigns numbers when a display is unplugged, so "Display 2" is not a fact about the
hardware. The friendly label comes from `EnumDisplayDevicesW` and is only what the settings row
prints.

Placement is remembered per display: the same margin from the anchor is wrong on a laptop panel and
a docked ultrawide, so each device name keeps its own `OffsetX`/`OffsetY`. Dragging the overlay
stores the offsets under the display it landed on. On startup, a display change (`WM_DISPLAYCHANGE`)
or a settings click, the chosen monitor is resolved again and a specific monitor that is no longer
connected falls back to the primary with one tray sentence rather than opening off-screen.

The foreground process is polled on the same slow cadence. It powers two rules. A listed executable
on the hide list makes the overlay invisible while it owns the foreground window; a process that
cannot be inspected (protected or elevated) reads as unknown and never matches. The other is the
exclusive-fullscreen notice: `SHQueryUserNotificationState` saying busy or D3D-fullscreen, a
foreground window that is not ours or the shell, on the overlay's own display, with extended frame
bounds covering that display's bounds - all four together, sustained for ten seconds, announced
once through the tray. It is a heuristic, not a detector: borderless fullscreen usually reports
"accepts notifications", and a game on another display is excluded by the monitor comparison.

## Choosing the words: automatic, pinned or local

The automatic matcher is the default, but it is not the only answer the listener may want. Three
sources can supply a sheet, and exactly one of them is in force at a time - they never stack:

1. **A local file** the listener imported. It wins over everything, because it is a deliberate act.
2. **A pinned LRCLIB record**, chosen in the selector or pinned from the clipboard.
3. **The automatic pipeline**: cache, exact lookup, ranked search.

All three are one explicit `LyricSelection` per track in `selections.json`, so "which source wins"
is a stored fact rather than an implicit precedence between two files. The last explicit action wins:
import a file, pin a record, or choose Automatic. The store migrates the old `versions.json` map once,
so earlier pins keep working.

A local sheet is read from disk on every play rather than cached: editing it in Notepad takes effect
on the next lookup, which is the whole point of "let me supply or correct the lyrics". Its
`LyricsResult` carries `Source = Local`, and the windows badge it; the automatic result stays in the
cache for the day the listener switches back. When the file behind a selection is deleted, the
resolution falls back to automatic and the host says so once for that track - a silent switch would
look like the import never happened.

## When nothing reaches Overlyric

The failure this app has to be honest about is silence. If a player never publishes to Windows media
controls, or publishes a track without a position, there is nothing to sync to — and from the outside
that looks exactly like Overlyric being broken: no overlay at all, or the same line on screen for a
whole song.

So Overlyric asks CoreAudio — the audio engine, not GSMTC — whether anything is *audibly* playing,
and compares that with what GSMTC publishes. (Audible is the peak meter, not merely a session that
exists: a game left open holds a stream for hours while making no sound.)

| What is happening | What Overlyric says |
| --- | --- |
| Nothing publishing, nothing audible | Nothing. There is no player to blame |
| Audio is playing, but nothing publishes | "Audio is playing, but no player publishes to Windows media controls" — check whether the player supports them, or look for a plug-in that adds them (AIMP has one) |
| A track publishes, but no position | The overlay reads `Player is not reporting a playback position`, and the tray explains that the player or its plug-in has to publish the position too |
| A track and a position publish | The lyrics, as normal |

Each answer appears in the tray tooltip, in the tray menu for as long as the situation lasts, and
once as a notification — once per situation, because a warning that repeats is a warning nobody
reads. The condition must hold for ten seconds first, so a track that reports `00:00` before its
first timeline update never triggers it. `WarnWhenPlayerDoesNotPublish` turns the whole thing off,
and the tray menu has the same toggle.

This is deliberately diagnostic rather than clever: there is no attempt to guess a position for a
player that refuses to give one, because a wrong lyric is worse than an honest sentence.

## Publishing

Publishing is the one outbound path that creates data on someone else's service, so it is the most
guarded: only a local sheet can be published (an LRCLIB record would be duplicated, not corrected),
the viewer shows the exact fields and span and requires an explicit confirmation, and the host
re-runs the validation even though the button was disabled - a guard that only exists in the UI is
not a guard. The client then requests a proof-of-work challenge, solves it on all cores
(`SHA-256(prefix + decimal nonce)` at or below the target), and sends one request with the
single-use token. There is no retry with a used token and no background publish; 400/429/503 are
turned into sentences, and the local sheet stays the listener's copy either way.

## Stage mode

The audience surface is the same renderer and window, sized differently: the window is placed over
its monitor's full bounds rather than a pill over the desktop, the theme carries an opaque black or
chroma-green backdrop, and the renderer fills the whole window before drawing the centred seven-line
block. It shows no header, no progress bar and no control bar, and turning it on locks the overlay,
because a stage is something the audience watches rather than something the player drags. The
settings and config keep it opt-in; the normal overlay is one toggle away.

## Scripting and the second instance

The app has one overlay and one tray by design, so a second launch cannot be allowed to create a
second copy. It also should not exit silently: "I clicked the shortcut and nothing happened" is the
failure mode that motivated the command surface. The single-instance mutex still decides who owns
the process; the loser forwards its arguments as one line over a named pipe
(`Overlyric.Commands.v1`) and exits 0. The owner's pipe thread only parses and enqueues - the
commands run on the UI thread in the tick loop, so a forwarded command and a menu click go through
the same implementation.

The grammar is deliberately small and strict: show/hide/toggle, layout, offset nudge/reset, lock,
preview, settings, diagnostics, refresh and exit. An unknown switch is an error (exit code 2, logged),
because a script that quietly did nothing is worse than one that says why.

## Saying what the lookup found

The overlay is deliberately quiet, but a track change is the one moment a listener wants to know what
is playing and whether the words will actually follow the music. So a song starting — or the overlay
being switched back on mid-song — raises a small pill inside the same overlay window for
`InfoPopupSeconds` seconds (default five; `0` turns it off, the **Info pop-up** stepper in Settings →
Overlay). It names the song, says one line about the lyric sheet, and then disappears on its own:
nothing has to be dismissed. While the pill is up the dim track header is hidden, because the pill
already names the song; its height stays reserved, so the lyrics do not jump when the header comes and
goes.

| The pill's second line | What it means |
| --- | --- |
| `Word-synced lyrics` | The record carries `lines[].words[]`, so the karaoke sweep follows real word timings |
| `Synced lyrics (line timings)` | Timed lines only; the sweep is shared out across the line |
| `Lyrics not synced` | The words were found, but with no timings, so they cannot follow the music |
| `No lyrics found` | lrclib has no record with lyrics for this track |
| `Instrumental — no lyrics to sync` | There is nothing to sing, which is an answer rather than a failure |
| `Player reports no playback position` | The words exist, but there is no position to place them against |
| `Looking up lyrics…` | Provisional: the lookup has not answered yet |

The lookup takes a moment, so the pill first reads `Looking up lyrics…`; when the answer lands it is
replaced by the verdict with a fresh deadline. A slow lookup therefore cannot keep the pill on screen,
because the deadline belongs to the showing rather than to the request.

**A miss the eyes cannot see is announced once per track.** With "hide when no lyrics" on, a missing
sheet leaves no overlay at all, and untimed lyrics leave text that never moves; from the outside both
look like Overlyric doing nothing. So when the lookup settles with either answer the tray raises a
notification — "no lyrics found", or "lyrics are not synced" — and `NotifyWhenLyricsMissing`
(Settings → Behaviour) turns it off. It is deliberately silent for an outage, because an outage is not
evidence about the track: lrclib throttles bursts and regularly answers `503`, so `LookupDetailedAsync`
reports whether an empty answer is definitive and only a definitive one is announced. That is the same
rule the cache tombstones follow, and it is what stops one bad moment from telling the listener their
track has no lyrics.

**The notification is a tray balloon, not a toast, and that was measured rather than assumed.** A
WinRT toast needs an AUMID registered with the shell, which for an unpackaged executable means a Start
Menu shortcut carrying `System.AppUserModel.ID`. `probe\ToastProbe` walks the whole path:
`CreateToastNotifier` returns a notifier, `ToastNotifier.Setting` throws `0x80070490`
(`ERROR_NOT_FOUND`, the missing registration surfacing), and `Show()` returns without throwing while
no toast appears. A silent no-op is worse than an exception, because the application believes it has
told the user something. Overlyric therefore uses `Shell_NotifyIcon` with `NIF_INFO` — the same tray
balloon the player warning already uses, with no AUMID or shortcut requirement.

`probe\InfoTests` pins this logic down in 50 checks: the pop-up lifetime and its restart-on-verdict
rule, every verdict line and its tone, and the notification decision, including the
transient-versus-definitive distinction.

## Why the overlay is built this way

- **`WS_EX_NOREDIRECTIONBITMAP` + DirectComposition.** Windows keeps no redirection surface for the
  window and the compositor blends the result on the GPU. There is no per-frame CPU pixel copy — the
  thing that makes classic `UpdateLayeredWindow` overlays expensive. `WS_EX_LAYERED` is set as well,
  but only for its hit-testing rule: at alpha 255 the layered path never touches the pixels
  DirectComposition produces.
- **Direct2D + DirectWrite** for the actual drawing, so text is properly antialiased at any DPI with
  real font shaping, instead of GDI's transparent-background compromises.
- **`WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`** mean the overlay can never steal focus from a game or a
  full-screen video.
- **Redraw-on-change.** A frame is drawn only when the visible content actually changes — the lyric
  line, or a progress bar step of 1/200. Idle cost is effectively zero.

## Game compatibility, honestly

The overlay sits above **borderless-windowed** games and all ordinary windows. It cannot draw above a
game running in **exclusive full-screen** mode: Windows bypasses the desktop compositor there, and
only overlays that inject code into the game process (with all the anti-cheat risk that implies) can.
Overlyric deliberately does no injection, no hooking and no DLL loading into other processes.