# Overlyric

A Windows 11 lyric overlay that shows the words to whatever is playing — **from any player**, with
no per-app integration. It reads the now-playing track straight from Windows' own media controls,
fetches synced `.lrc` lyrics from [LRCLIB](https://lrclib.net/), and paints them into a GPU-composited
overlay that stays pinned above everything, including borderless-windowed games.

| Single line (live TIDAL over a running game) | Multi line |
| --- | --- |
| ![Single-line overlay](docs/overlay-single-line.png) | ![Multi-line overlay](docs/overlay-multi-line.png) |

---

## How it works

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

### Player-agnostic by design

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

### Matching the right record

lrclib usually holds many records for the same song — studio, live, remastered, assorted
compilations — and they differ in whether they carry timings at all. Finding the one that matches the
performance actually playing takes some cleaning, some ranking, and — as often as not — a second
question.

**Release suffixes are stripped before querying.** TIDAL reports
`A Song For All Seasons (2019 Remaster)`, and lrclib holds records whose names carry that exact
suffix — but with *plain* lyrics only. Asking for the suffixed name returns precisely that family, so
the app concluded the track had no timings and displayed "Lyrics not synced". Asking for
`A Song For All Seasons` instead returns twenty candidates including a synced record with an identical
duration and album. The suffix was not merely untidy; it selected the wrong record.

Qualifiers that identify a genuinely different performance — `(Live)`, `(Acoustic)` — are deliberately
**not** stripped, so a live take never silently resolves to the studio cut.

**Movement listings are cut down to the work.** Some players report a multi-part piece as one
enormous title: TIDAL sends `Song of Scheherazade / I. Fanfare - II. / The Betrayal / III. The Sultan /
…` as the track name, and no lrclib record carries anything like it, so searching the whole string
finds nothing. Cutting at the first spaced slash — but only when what follows actually looks like a
movement list, so a title such as `Love / Hate` survives — reduces it to `Song of Scheherazade`, which
returns twenty candidates including six with timings. The same cleaning is applied to the on-screen
header, which would otherwise stretch across the entire display.

**Candidates are ranked, and duration dominates.** A synced lyric sheet for a different performance is
worse than untimed lyrics for the right one, because it would drift out of step with the music. So a
candidate that is minutes out of length can never win on the strength of having timings; among
candidates whose duration agrees, timings, an exact title and a matching album break the tie.

**When the pick carries no timings, the first record in rank order that has them and runs for the same
length wins.** Untimed lyrics for the right performance beat a synced sheet for the wrong one, but they
are not the best answer available — timings for *that* performance are. So when the ranked pick is not
synced, the app takes the first record in rank order that carries real timings **and** runs for the same
length as the track being played. "The same length" is `|candidate - track| <= max(2 s, 0.5 % of the
track)` — the band the ranking already treats as an agreeing duration, and exactly lrclib's own window
at that size: `/api/get` matches a duration within two seconds, measured directly (asking it for 251 s
returns the 249.051429 s record, asking for 252 s returns 404). Only a piece longer than 400 s widens
it, because that is where the proportional term overtakes the two seconds. Exact equality is not a
thing that exists in this data: the player reported 246.4 s for a sheet that runs 244.744127 s, 1.656 s
apart, so a test for equality would never have fired. A synced sheet for a different performance fails
on length alone and is never chosen by this rule — the same signal the ranking already trusts. An
instrumental verdict is not overridden on the strength of timings either: `Metallica – Orion` still
comes back an instrumental with no lines. When no synced record matches the length, the plain result is
kept, and when the player publishes no duration the rule cannot apply at all, because there is nothing
to compare against.

**That answer lives behind a second request, because lrclib caps `/api/search` at twenty records.**
Measured directly: `track_name=Winning` returns 20 records, `track_name=For+Nobody` returns 20, and
`q=Winning` returns 20. The cap can hide the very record the rule is looking for. For the track playing
while this was written — Gentle Giant, *For Nobody*, from *The Missing Piece*, 246.4 s — the
track-and-artist search returned twenty records and **not one of them carried timings**, while the same
query plus `album_name=The Missing Piece` is the only one that contains the synced record 16359843,
"09 - For nobody", 244.744127 s, twenty lines, first line `Running away, I'll leave you my address,` at
0:46.88. So when — and only when — the first search leaves the track unsynced, the search is repeated
with the album and the two lists are ranked together; the app now resolves that track to the synced
record. A track the first search already answers well still costs exactly one `/api/search`, because the
album-scoped question is only asked when the first one left the track untimed. The merged list is also
what the tray's **Lyrics version** menu shows: for the live case it held 35 records, with id 16359843
appearing exactly once.

### Choosing a version by hand

The automatic pick is a guess, however good, so the tray menu has a **Lyrics version** submenu listing
the alternatives, best first, each described as `11:00 · synced · A Song For All Seasons (2019
Remastered & Expanded Edition)`. Picking one pins that exact lrclib record, and the choice is
remembered across plays and restarts in `%LOCALAPPDATA%\Overlyric\versions.json`. **Automatic (best
match)** returns to the ranked pick.

Automatic is a guess, and sometimes a close one: for Gentle Giant's *Winning* (256 s) the automatic pick
is the synced record 36334456 (257 s), while the record a listener had pinned by hand, 14916772
(257.52 s), sits just below it in rank order and is equally inside the duration window.

The candidate list — the merged, ranked list described above — is fetched up front only when the
resolved lyrics are missing or untimed — exactly where a version change is likely — so a track with
good synced lyrics costs no extra request.

### The adaptive playback clock

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
| Detects a track change | Resets the anchor, clears liveness detection, resets manual offset |

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

### When nothing reaches Overlyric

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

### Why the overlay is built this way

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

### Game compatibility, honestly

The overlay sits above **borderless-windowed** games and all ordinary windows. It cannot draw above a
game running in **exclusive full-screen** mode: Windows bypasses the desktop compositor there, and
only overlays that inject code into the game process (with all the anti-cheat risk that implies) can.
Overlyric deliberately does no injection, no hooking and no DLL loading into other processes.

---

## Resource budget

The design targets a working set well under 100 MB and near-zero CPU:

- **NativeAOT** — a single self-contained native executable, no .NET runtime to install, no JIT, no
  warm-up. The runtime baseline is a fraction of a framework-dependent app's.
- **No UI framework.** No WPF, no WinUI, no WinForms — just Win32 plus Direct2D. WPF alone would
  typically cost 80–110 MB before drawing a single lyric.
- **No per-frame allocations.** Text layouts are cached per lyric line, so a redraw caused by the
  progress bar advancing allocates nothing at all.

### Measured

Measured on Windows 11 (build 26100), 3840×2160 at 150 % scaling, with TIDAL playing and the overlay
rendering over a running full-screen game:

| Metric | Value |
| --- | --- |
| Working set | **~65 MB**, flat over 3+ minutes |
| Private commit | ~99 MB |
| CPU | **~0.2 s per 150 s** of playback (≈0.15 % of one core) |
| Executable | 9.2 MB, single file, no .NET runtime installed |
| Handles / threads | ~1280 / ~25 |

Getting there took fixing three things that are easy to get wrong, all found by measuring rather than
guessing:

1. **A leaked `IDWriteTextLayout` per frame.** The single-line header layout was created on every
   redraw and never disposed. At four redraws a second that leaked ~250 KB/s. Layouts are now owned
   by a cache and released in `Dispose`.
2. **`SetDpi` on every frame.** Re-applying the same DPI made Direct2D invalidate and rebuild its
   device-dependent resources each frame. It is now only applied when the value changes.
3. **A lazy garbage collector.** The WinRT media polling allocates, and with the default policy the
   GC let that accumulate into the working set instead of reclaiming it. `System.GC.Conserve = 5`
   (set in the project file, so it is baked into the binary) keeps the collector eager. This is the
   right trade for a background overlay: measured CPU went *down*, not up.

Verify it yourself with `probe\screenshot.py` and the loop in `probe\` — see [Probes](#probes).

---

## Requirements

- Windows 10 1809 (build 17763) or newer; Windows 11 recommended.
- x64.
- To build: .NET SDK 10 and the MSVC toolchain (Visual Studio 2022 with "Desktop development with
  C++") for NativeAOT linking.

## Build

```powershell
dotnet publish src\Overlyric\Overlyric.csproj -c Release
```

The executable lands in:

```
src\Overlyric\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\Overlyric.exe
```

## Run

Just start `Overlyric.exe` — it lives in the notification area. Start playing something in any
player and the overlay appears.

To preview the overlay without playing anything:

```powershell
Overlyric.exe --demo
```

Demo mode drives the overlay from a canned synced track, so you can judge the look, try both modes
and drag it around with nothing playing.

## Controls

### Global hotkeys

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+H` | Show / hide the overlay |
| `Ctrl+Alt+M` | Switch between single-line and multi-line |
| `Ctrl+Alt+L` | Lock / unlock. Unlocked, the overlay becomes draggable and accepts the mouse wheel |
| `Ctrl+Alt+,` | Shift lyrics earlier by 0.25 s |
| `Ctrl+Alt+.` | Shift lyrics later by 0.25 s |
| `Ctrl+Alt+0` | Reset the timing offset |

### Tray icon

Left-click toggles the overlay. Right-click opens the menu: the **Lyrics version** submenu, mode, lock,
track info, progress bar, line count, opacity, lyric timing, screen anchor, and folders. Hiding is
split into "hide when no lyrics" and "hide instrumentals", so a wordless track can either vanish or
simply say so.

Scroll the wheel over the overlay while it is unlocked to change the backdrop opacity. Drag it while
unlocked to reposition: the placement is stored as an offset **from the anchored edge** — a margin
above the bottom, below the top, or either side of the centre — so it reopens exactly where it was
left, and switching between single-line and multi-line grows the overlay away from that edge rather
than sliding it up the screen.

Placements saved before the offsets meant this are repaired once, at startup and against the monitor
the overlay is about to open on: Overlyric used to store the window's own coordinates, so a
bottom-anchored overlay was read as a bottom margin the height of the screen and reappeared in the
top corner. `PlacementVersion` records that the conversion happened; it is bookkeeping rather than a
setting.

**Locked means click-through, and it takes two styles to get there.** The window carries
`WS_EX_TRANSPARENT` while it is locked, but that style only makes Windows ignore the window when the
window is *also* layered — the documentation is explicit that hit testing then passes the mouse to
what is underneath. Without `WS_EX_LAYERED`, which this window has to be given explicitly because
DirectComposition does not set it, a locked overlay still answers for its own rectangle and eats
every click aimed at the application behind it. Measured over the overlay's centre with
`WindowFromPoint`: with `WS_EX_TRANSPARENT` alone the overlay answers; adding `WS_EX_LAYERED` with
alpha 255 — so the layered path never dims what DirectComposition draws — makes the window below
answer instead, and the overlay stays on screen.

Unlocking is the adjustment mode: the overlay then takes the mouse, so it can be dragged and takes
the wheel, and it is the only state in which a click over the lyrics does not reach the application
behind. It is a toggle — `Ctrl+Alt+L` or the tray menu, never automatic — and the state is remembered
in `Locked`, so an overlay left unlocked on purpose is still unlocked next time.

---

## Configuration

`%LOCALAPPDATA%\Overlyric\config.json`

| Key | Meaning |
| --- | --- |
| `Mode` | `SingleLine` or `MultiLine` |
| `Anchor` | `BottomCenter`, `TopCenter` or `Center` |
| `OffsetX` / `OffsetY` | Pixel offset from the anchor |
| `Locked` | Locked is click-through; unlocked is draggable and takes the wheel |
| `PlacementVersion` | Bookkeeping: the offsets have been converted to anchor-relative ones (see above) |
| `ShowHeader` | Show `Artist — Title` above the lyrics |
| `ShowProgressBar` | Show the track progress bar |
| `MultiLineCount` | Lines visible in multi-line mode |
| `BackgroundOpacity` | Backdrop opacity, 0.15–1.0 |
| `HideWhenPaused` | Hide while playback is paused |
| `HideWhenNoLyrics` | Hide when lrclib has no lyrics for the track |
| `HideWhenInstrumental` | Hide when the track is instrumental |
| `WarnWhenPlayerDoesNotPublish` | Say so when audio is playing but no player publishes to Windows media controls, or when a player publishes a track without a playback position |
| `LyricsOffsetMs` | Manual timing correction |
| `FontFamily`, `ActiveFontSize`, `InactiveFontSize` | Typography |

Fetched lyrics are cached as one JSON file per track in `%LOCALAPPDATA%\Overlyric\cache`, including
negative entries, so a track LRCLIB has never heard of is not re-queried on every replay.

Each entry also records the version of the matching rules that produced it. When those rules change —
the release-suffix fix and the synced-same-duration rule both did — an entry written by the older rules
is not wrong on its face, but it is the answer the old rules gave, and serving it would hide the fix
from tracks that were already played. So an entry from an older matcher reads as a miss: the track is
resolved once more against lrclib and the entry is rewritten. A negative tombstone is bookkeeping of the
same kind and is aged out the same way, because a search that has since learned to ask a better question
may well find what the old one missed. Like `PlacementVersion`, this is bookkeeping rather than a
setting.

Manual version choices live in `%LOCALAPPDATA%\Overlyric\versions.json`, keyed by track. It is derived
state rather than settings, so it is kept out of `config.json` and bounded to 256 entries. Delete it to
forget every pinned version.

---

## Notes on LRCLIB

The client uses `/api/get` with artist, title, album and duration for an exact match, falling back to
`/api/search` and picking the best candidate by duration, preferring entries that carry synced lyrics
and upgrading an untimed pick to the first timed record of the same length (see [Matching the right
record](#matching-the-right-record)); when that first search leaves the track unsynced it is repeated
with the album, because lrclib caps a search at twenty records. Every request sends a descriptive
`User-Agent`, as LRCLIB asks. Network failures degrade to "no lyrics"; they never disturb playback.

**A transient outage must never become a permanent verdict.** LRCLIB throttles bursts and regularly
answers `503 Server is busy`, which was observed repeatedly during development. Treating that as "this
track has no lyrics" and negative-caching it would hide a track's lyrics forever after one bad
moment. So the client distinguishes three outcomes:

| Response | Meaning | Cached? |
| --- | --- | --- |
| `200` with lyrics | Found | Yes, as lyrics |
| `404`, or a record with no lyrics at all | Definitively absent | Yes, as a tombstone |
| `429`, `5xx`, timeout, network error | Unknown — ask again later | **No** |

Throttled and failed requests are retried once before giving up, and only a definitive answer is
remembered. This is covered by `probe\ResilienceTests`, which points the client at a local stub server
returning `503`, `429`, `404` and lyric-less `200` responses and asserts both the classification and
the caching decision.

Unsynced lyrics are shown only after the search for a synced sheet of the same length has come up empty:
then single-line mode says `Lyrics not synced` rather than guessing, and multi-line mode scrolls the
text proportionally to track position.

### Instrumental tracks

"There is nothing to sing here" and "we could not find the words" are different answers, and a third
case matters just as much: **lrclib records whose entire lyric body is placeholder text.** Contributors
routinely write `Instrumental` or `[Instrumental]` instead of setting the record's `instrumental` flag,
and parsed naively that becomes a real lyric line — so the overlay ends up displaying the word
"Instrumental" in time with the music. The flag cannot be relied on, so the body itself is inspected.

`InstrumentalDetector` reduces each line to comparable words — dropping `[mm:ss.xx]` timestamps and
`[ar:…]` metadata tags, discarding brackets, musical symbols and punctuation — then checks whether the
**whole** body consists of phrases that mean "no words here":

| Recognises | Examples |
| --- | --- |
| The word itself | `Instrumental`, `[Instrumental]`, `Instrumental Break`, `Instrumental Version` |
| Explicit statements | `No lyrics`, `Without vocals`, `Lyrics not available` |
| Nothing to sing | `♪`, `Silence`, `Tacet`, `Spoken` |
| Instrumental forms and section names | `[Intro]`, `[Solo]`, `[Outro]`, `Overture`, `[Chorus]` |

The safety property is that a verdict requires *every* line to be a placeholder. A real song with words
plus an `[Instrumental Break]` or `[Chorus]` marker in the middle is unaffected, because its other
lines are not placeholders — that case is pinned down by a test.

Detection works in three layers, in order:

1. lrclib's `instrumental` flag (authoritative — confirmed against real data; `Metallica – Orion`
   returns `"instrumental":true` with both lyric bodies `null`).
2. The lyric body itself, as described above, for records where the flag was left unset.
3. The title or album, **only** when lrclib has no record at all. A title like
   `Song (Instrumental)` or an album called `Instrumentals` gives a better answer than
   "no lyrics found". `Karaoke` is deliberately *not* treated as instrumental: a karaoke track has no
   lead vocal but the words are exactly what you want to see.

Both kinds of empty answer are cached, so an instrumental is not re-queried on every replay, and they
can be hidden independently — some people want the overlay to stay put and simply say "Instrumental"
on a wordless track rather than vanishing.

---

## Probes

`probe\` holds the throwaway projects used to establish the facts this design rests on. They are kept
because they are the quickest way to re-check a platform assumption.

| Project | Question it answers |
| --- | --- |
| `SmtcProbe` | What does GSMTC actually report, and does the player update its timeline? |
| `SmtcAot` | Does the Windows SDK WinRT projection survive NativeAOT? |
| `VorticeAot` | Does the Vortice COM interop survive NativeAOT, and what does DirectWrite report? |
| `NowPlaying` | What is playing right now, what does lrclib hold for it, and what does the pipeline resolve the track to — the reproduction harness for a lyric-matching failure |
| `LyricsTests` | 30 checks over LRC parsing, caching (including matcher-version staleness) and a live LRCLIB round-trip |
| `ResilienceTests` | 66 checks: transient-vs-definitive caching, instrumental classification, metadata cleaning, candidate ranking, the synced-same-duration rule, the album-scoped supplement and version pinning |
| `ClockTests` | 19 checks over the playback clock: attaching mid-song, frozen vs live timelines, pause/resume, seeks, sentinel stamps |

Helper scripts: `probe\screenshot.py` (capture the screen) and `probe\crop.py` (crop and upscale a
region for close inspection). Together they give a way to confirm visually that the overlay is on
screen:

```powershell
Overlyric.exe --demo
python probe\screenshot.py shot.png
python probe\crop.py shot.png shot_zoom.png 1480 1700 2400 2010 1.4
```

---

## Project layout

```
src\Overlyric\
  Program.cs                 entry point, single-instance guard, DPI awareness
  App\
    AppConfig.cs             settings + source-generated JSON (AOT-safe)
    AppHost.cs               message loop, tick, lyric-to-overlay pipeline
    TrayIcon.cs              Shell_NotifyIcon wrapper and context menu
    HotKeys.cs               RegisterHotKey manager, command ids
  Media\
    MediaSessionWatcher.cs   GSMTC polling and snapshots
    PlaybackClock.cs         adaptive, self-clocking position source
    NowPlayingSnapshot.cs    immutable now-playing state
    AudioSessions.cs         CoreAudio: is any application audibly playing?
    PlayerWarningWatch.cs    when to tell the user their player publishes nothing usable
  Lyrics\
    LrclibClient.cs          lrclib.net HTTP client: exact, ranked and album-scoped search
    LrcParser.cs             LRC parsing
    InstrumentalDetector.cs  decides whether a track has anything to sing
    MetadataNormaliser.cs    strips release noise so the right record is found
    LyricsCandidate.cs       one selectable lrclib record
    VersionOverrides.cs      remembers manually chosen versions
    LyricsCache.cs           on-disk cache with negative entries, stamped with the matcher version
    LyricsService.cs         pinned -> cache -> exact -> ranked search
  Overlay\
    OverlayWindow.cs         topmost click-through Win32 window
    CompositionHost.cs       D3D11 + DirectComposition + D2D device stack
    LyricsRenderer.cs        Direct2D/DirectWrite drawing
    OverlayTheme.cs          theme and per-frame visual model
  Interop\
    NativeMethods.cs         the Win32 surface actually used
    WindowBase.cs            AOT-safe window class registration and dispatch
    MessageWindow.cs         hidden window for hotkeys, tray and timers
probe\                       throwaway feasibility probes kept for reference
```
