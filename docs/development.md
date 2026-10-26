# Development

How to build Overlyric, what it costs at runtime, and the probes that established the platform facts
the design rests on. All of this is for working on the app, not for using it.

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

## Icon

`src\Overlyric\Assets\Overlyric.svg` is the artwork; `Overlyric.ico` beside it is the rendered set
that ships — 16, 20, 24, 32, 40, 48, 64, 128 and 256 px, small frames as 32-bit DIBs and the two
largest as PNG. One file feeds every Windows surface, so they cannot drift apart:

- the executable's resources (`<ApplicationIcon>` in `Overlyric.csproj`) — Explorer, the taskbar,
  Alt+Tab and the MSI's Add/Remove Programs entry;
- the notification-area icon and the window class icons, read back out of the executable's own
  resources by `AppIcon`;
- both installer shortcuts (`installer\Overlyric.wxs`).

Re-render it after editing the SVG:

```powershell
python tools\make-icon.py
```

That needs Python with Pillow and Chrome or Edge to rasterise each size at 4× before downsampling
(`--svg`, `--out` and `--chrome` override the defaults). It rewrites
`src\Overlyric\Assets\Overlyric.ico` in place; rebuild and restart the app to see the change,
because Windows caches shell icons per file.

## Installer

The per-user MSI is built from the publish output, so the payload always matches the binary that was
just produced:

```powershell
pwsh installer\build-installer.ps1
```

It reads the version from `src\Overlyric\Overlyric.csproj` (or accepts `-Version`), refuses to ship a
`.pdb`, regenerates its file list from the actual publish directory, and writes
`dist\Overlyric-<version>-x64.msi`. `-SkipPublish` reuses an existing publish output; `-CertificatePath`
signs the MSI when a PFX is supplied. The script fails loudly if a prerequisite is missing, and prints
the SHA256 plus the exact install and uninstall command lines. The installer is per-user: no elevation,
no machine-wide registry writes, and it never touches another user's profile.

## Resource budget

The design targets a working set well under 100 MB and near-zero CPU:

- **NativeAOT** — a single self-contained native executable, no .NET runtime to install, no JIT, no
  warm-up. The runtime baseline is a fraction of a framework-dependent app's.
- **No UI framework.** No WPF, no WinUI, no WinForms — just Win32 plus Direct2D. WPF alone would
  typically cost 80–110 MB before drawing a single lyric.
- **No per-frame allocations.** Text layouts are cached per lyric line, so a redraw caused by the
  progress bar advancing allocates nothing at all.

## Measured

Measured on Windows 11 (build 26100), 3840×2160 at 150 % scaling, with TIDAL playing and the overlay
rendering over a running full-screen game:

| Metric | Value |
| --- | --- |
| Working set | **~65 MB**, flat over 3+ minutes |
| Private commit | ~99 MB |
| CPU | **~0.2 s per 150 s** of playback (≈0.15 % of one core) |
| Executable | 9.7 MB, single file, no .NET runtime installed |
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

## Probes

`probe\` holds the throwaway projects used to establish the facts this design rests on. They are kept
because they are the quickest way to re-check a platform assumption.

| Project | Question it answers |
| --- | --- |
| `SmtcProbe` | What does GSMTC actually report, and does the player update its timeline? `--sessions` lists every session at once, marks the one Windows calls current, and reports each change while several are alive |
| `SmtcAot` | Does the Windows SDK WinRT projection survive NativeAOT? |
| `VorticeAot` | Does the Vortice COM interop survive NativeAOT, and what does DirectWrite report? |
| `NowPlaying` | What is playing right now, what does lrclib hold for it, and what does the pipeline resolve the track to — the reproduction harness for a lyric-matching failure |
| `LyricsTests` | 34 checks over LRC parsing, caching (including matcher-version staleness, forget and clear) and a live LRCLIB round-trip |
| `ResilienceTests` | 97 checks: transient-vs-definitive caching, the one-request outage, a validation error never being filed as "no lyrics", the tombstone lifetime, instrumental classification, metadata cleaning (title and album release suffixes, movement listings and duplicated artist prefixes), the manual free-text search, the album-suffix retry, candidate ranking, the synced-same-duration rule, the album-scoped supplement, the stored candidate list and version pinning |
| `ApiBudget` | What one lyric resolution costs in requests and bytes on every path - a hit, an album edition suffix, a search that settles it, a track lrclib does not hold, an outage, the selector, a replay - against a stub that counts them |
| `ShortcutsTests` | 35 checks over the hotkey text form: parsing, canonical modifier order, punctuation stored by name, and every malformed string falling back to "unbound" instead of throwing |
| `ShortcutFeedbackTests` | 33 checks over the shortcut toast: the total-offset title and the layer it came from (`Offset -0.25s · this track`), direction and step, the clamp saying nothing moved, and every state label |
| `KaraokeTests` | 22 checks over the sweep (monotonic, punctuation not sung ahead of the words, word timings honoured, mismatched timings falling back to the line span) and the lyricsfile reader (line ends, words, colons inside text, garbage refused without throwing) |
| `ClockTests` | 19 checks over the playback clock: attaching mid-song, frozen vs live timelines, pause/resume, seeks, sentinel stamps |
| `TimingTests` | 34 checks over timing-profile resolution: track -> output -> global overrides, a stored zero as an override, session-only nudges, clamping and cleanup of hand-edited maps, and the labels (`+0.25 s · this track`, `+0.15 s · Bluetooth headphones`) |
| `SourcePreferenceTests` | 46 checks over the source selector: pin beating the list and Windows, list order respected, a listed player that is not running skipped, duplicate app ids resolved to the one actually playing, and a hand-edited list cleaned and capped without throwing |
| `ThemeTests` | 18 checks over the OLED preset and the colours it produces: every preset value moved off its opposite, the unsung half turning pure black, the surrounding lines staying dim, and the transparent backdrop staying absent |
| `LyricWindowsTests` | 61 checks over the wording the selector and viewer share: offset and length formatting, match deltas, timestamps, badge precedence, candidate labels, the sheet's manual-search state, finding the active line and the gap-progress rule |

Helper scripts: `probe\screenshot.py` (capture the screen) and `probe\crop.py` (crop and upscale a
region for close inspection). Together they give a way to confirm visually that the overlay is on
screen:

```powershell
Overlyric.exe --demo
python probe\screenshot.py shot.png
python probe\crop.py shot.png shot_zoom.png 1480 1700 2400 2010 1.4
```

## Project layout

```
src\Overlyric\
  Program.cs                 entry point, single-instance guard, DPI awareness
  App\
    AppConfig.cs             settings + source-generated JSON (AOT-safe)
    AppHost.cs               message loop, tick, lyric-to-overlay pipeline
    StartupRegistration.cs   per-user Run key for "start with Windows"
    PanelWindow.cs           shared frameless Direct2D window for the lyric panels
    LyricPickerWindow.cs     the selector: track details, records and badges
    LyricViewerWindow.cs     the complete sheet, with its own LRCLIB search
    LyricSheet.cs            the document the selector and viewer share
    LyricBadges.cs           word-synced / synced / plain / instrumental verdicts
    LyricText.cs             shared offsets, lengths, timestamps, active line and gap progress
    SettingsWindow.cs        the configuration window
    ShortcutFeedback.cs      wording for the toast a global shortcut raises
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
    LrclibPacing.cs          request pacing and the Retry-After gate every request passes
    LrcParser.cs             LRC parsing
    InstrumentalDetector.cs  decides whether a track has anything to sing
    MetadataNormaliser.cs    strips release noise so the right record is found
    LyricsCandidate.cs       one selectable lrclib record
    VersionOverrides.cs      remembers manually chosen versions
    LyricsCache.cs           on-disk cache: answers, expiring tombstones, the candidate list
    LyricsService.cs         pinned -> cache -> exact -> ranked search, one resolution at a time
  Overlay\
    OverlayWindow.cs         topmost click-through Win32 window
    OverlayAnchor.cs         which monitor edge the overlay hangs from
    ShortcutToastWindow.cs   click-through window that confirms a global shortcut
    CompositionHost.cs       D3D11 + DirectComposition + D2D device stack
    LyricsRenderer.cs        Direct2D/DirectWrite drawing
    OverlayTheme.cs          theme and per-frame visual model
  Interop\
    NativeMethods.cs         the Win32 surface actually used
    WindowBase.cs            AOT-safe window class registration and dispatch
    MessageWindow.cs         hidden window for hotkeys, tray and timers
probe\                       throwaway feasibility probes kept for reference
```