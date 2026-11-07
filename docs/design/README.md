# UI design file

[`overlyric-ui.figcraft`](overlyric-ui.figcraft) is the whole application as an editable FigCraft
document: every window drawn at the DIP size the code opens it, with the real theme colours, the real
geometry constants and the strings the draw code actually uses. [`preview.png`](preview.png) is a
flat render of the same page for browsing without FigCraft.

Page 1 is the implemented UI as it stood before the unified shell (Overlyric 0.4.0), and page 2 is
the approved design for that shell, implemented in the app on 2026-11-05 — see
[Proposal page](#proposal-page). Page 1 is kept as the pre-change record rather than regenerated:
the four destination windows on page 2 are the chrome the code now draws.

## Screens

| Frame | Size (DIP) | Drawn by |
| --- | --- | --- |
| Settings · Lyrics | 780 × 584 | `src/App/SettingsWindow.cs` |
| Settings · Appearance | 780 × 584 | `src/App/SettingsWindow.cs` |
| Settings · Timing | 780 × 584 | `src/App/SettingsWindow.cs` |
| Settings · Position | 780 × 584 | `src/App/SettingsWindow.cs` |
| Settings · Behaviour | 780 × 584 | `src/App/SettingsWindow.cs` |
| Settings · Players | 780 × 584 | `src/App/SettingsWindow.cs` |
| Settings · Players — priority | 780 × 584 | `src/App/SettingsWindow.cs` |
| Settings · Shortcuts | 780 × 584 | `src/App/SettingsWindow.cs` |
| Settings · Advanced | 780 × 584 | `src/App/SettingsWindow.cs` |
| Overlay · Single line | 460 × 200 | `src/Overlay/LyricsRenderer.cs`, `OverlayTheme.cs` |
| Overlay · Multi line | 560 × 330 | `src/Overlay/LyricsRenderer.cs` |
| Overlay · Unlocked controls | 800 × 210 | `src/Overlay/LyricsRenderer.cs` (`DrawControls`) |
| Overlay · Stage mode | 1200 × 675 | `src/App/AppHost.cs` (`StageMode` visual), `OverlayWindow.cs` |
| Overlay · Track notice | 320 × 120 | `DrawNotice`, `src/Overlay/OverlayNotice.cs` |
| Overlay · Notice (warning) | 320 × 120 | `DrawNotice` with `NoticeTone.Warning` |
| Overlay · Info pop-up | 1200 × 200 | `DrawNotice` inside the single-line overlay window |
| Overlay · Shortcut toast | 780 × 80 | `src/Overlay/ShortcutToastWindow.cs` |
| Viewer · full lyrics | 900 × 664 | `src/App/LyricViewerWindow.cs` |
| Viewer · no lyrics | 900 × 664 | `src/App/LyricViewerWindow.cs` (empty state) |
| Picker · choose lyrics | 900 × 624 | `src/App/LyricPickerWindow.cs` |
| Picker · searching | 900 × 624 | `src/App/LyricPickerWindow.cs` (query in the header) |
| Diagnostics window | 820 × 664 | `src/App/DiagnosticsWindow.cs`, `PanelWindow.cs` |

The settings frames use the window's default 780 × 584 opening size (540 DIP body + 44 DIP title
bar). The overlay frames are sized to their content, which is what the real window does: it hugs the
pill.

## Proposal page

Page 2, **Proposal · unified shell**, is the design approved on 2026-11-05 for unifying the
independent windows. It was implemented the same day; page 1 still shows the earlier chrome.
[`proposal-preview.png`](proposal-preview.png) is a flat render of it, and the page itself ends
with the review checklist and the open questions.

What it specifies:

* **Entry point** — a custom tray launcher, 340 × 552 DIP, anchored 12 DIP from the work-area edge
  and 8 DIP above the taskbar. Left-clicking the notification-area icon opens it. Its four rows show
  a page in the same shell; the page already showing carries an OPEN tag; overlay switches and Exit
  sit below.
* **Window menu** — the existing 44 DIP title bar gains the app mark and four destinations
  (Lyrics · Versions · Diagnostics · Settings) in a fixed order. The old window-name text is removed;
  page bodies, sizes and scroll positions are untouched.
* **States** — current (fill `#23303F` plus accent rail), hidden (dim), hover. The page already
  showing is the only one visible, so it always reads as current.
* **Navigation** — choosing a destination swaps the page in place, at the same screen position and
  size. One page is visible at a time; the page you leave is hidden, not destroyed, and keeps its
  own scroll and edits.
* **Approval** — five review statements, three decisions needed, and Q1–Q7 open questions, all
  marked TBD on purpose.

**Revision after approval.** The board was approved on 2026-11-05 with *open-or-raise* navigation
(each destination a separate window raised over the last). The reviewer changed that rule on
2026-11-06: the destinations are pages of one window. The implementation and the README follow the
single-window rule; the page-2 copy, flow diagram and launcher variant were updated for the
revision. The **Menu states** card still defines a "visible" state that the one-page shell does not
currently produce; it is kept as the component's full state set and may return if a second shell is
ever opened.

A later 2026-11-06 revision aligned the **Lyrics** header to the **Versions** one — cover and album on
the left, the song title and artist centred, the duration and player under them, badges and record id
on the right. The page-2 **Lyrics** frame carries that shape, so the two lyric pages line up layer for
layer: a 48 DIP cover at x 50, a 4 DIP inset under the title bar, the album caption centred under it,
and the header divider at 74 DIP. **Diagnostics** was brought onto the same grid — 28 DIP inset, 74
DIP header, 22 DIP heading and a 12.5 DIP subtitle — so switching tabs no longer moves the left edge
or the divider. See [docs/usage.md](../usage.md) for the running shape.

Frames on page 2: Tray · notification area (schematic) 860 × 120 · Tray launcher — nothing open
and one page showing, 340 × 552 · Settings 780 × 584 · Lyrics 900 × 664 · Versions 900 × 624 ·
Diagnostics 820 × 664 · Overlay (unlocked) 800 × 210 · Title bar anatomy 1600 × 360 · Menu states
720 × 360 · Navigation flow 1560 × 460 · Navigation rules 760 × 460 · Specification 1140 × 900 ·
Review and approval 640 × 900 · Assumptions and open questions 500 × 900.

The implementation follows the specification's scope: playback, players, layout, refresh and the
rest of the native tray menu are unchanged. The UI suite gained a scenario for the shared menu and
one for the launcher (a private desktop has no shell, so the launcher scenario replays the tray
callback the icon would send).

## The search flow

Searching lives in one window. The sheet's **Choose lyrics** button (footer and empty state) closes it
and opens the selector; the selector's header is an editable field that starts on the track's own
query, and results replace the automatic list instead of opening another screen. Both header lines
(the song and the artist) plus the album under the cover are editable in place, and the toolbar
button reads **Search**. `Ctrl+F` remains the
sheet's quick in-place field. The selector's **Open on LRCLIB** link uses the path form,
`lrclib.net/search/<query>`, because the `?q=` form lands on an empty page.

The two frames that carry this — **Viewer · no lyrics** and **Picker · searching** — are the ones to
look at when the flow changes again.

## How the numbers were taken

Every position is a constant from the draw code rather than an eyeballed value:

* Settings: `NavWidthDip 196`, `PadDip 30`, `RowHeightDip 46`, `RowsTopDip 132`,
  `ViewportTop 108`, `ViewportBottom clientHeight - 40`, and the control sizes in
  `Toggle`/`Segmented`/`Stepper`/`Slider`/`Button`.
* Page header (Lyrics, Versions, Diagnostics): `Pad 28`, `HeaderHeight 74`, the title at 22
  SemiBold, the sub-lines at 12.5, the album cover at 48 with its caption centred beneath it, and
  the divider at `HeaderHeight - 1`.
* Overlay: `PaddingX 26`, `PaddingY 12`, `CornerRadius 14`, `LineSpacing 6`,
  `ProgressBarHeight 3`, `ProgressBarInset 10`, `ActiveFontSize 30`, `InactiveFontSize 21`,
  `HeaderFontSize 13`, and the control bar's 32 DIP buttons, 5 DIP padding, 4/10 DIP gaps.
* Colours: the `0xAARRGGBB` constants (`ColorWindow 0xFF0F1116`, `ColorAccent 0xFF7CC4FF`,
  `BackgroundColor 0xB4101014`, `ProgressColor 0xCC7CC4FF`, ...) converted to `#RRGGBBAA`.

The rebuilt settings frames were exported and diffed against the app's own 780 × 584 captures from
`tools/uitest` (`.dsh-tmp/uitest/*/settings-sections/captures`): mean channel difference 2.08–3.93,
about 2–4 % of pixels, and those are text antialiasing rather than layout. The overlay frames were
checked the same way against the `overlay-demo-smoke` captures.

## What is a placeholder

Runtime text has to come from somewhere, so the file uses representative values and says so in the
layer names:

* track, artist, album, LRCLIB ids and version counts in the viewer and picker;
* cache sizes and entry counts in Advanced;
* the audio output name and monitor label in Timing and Position;
* the foreground app name in Behaviour.

Stage mode keeps the application's own type sizes (30/21 DIP); it does not invent a larger
"audience" scale, because the renderer does not apply one.

The tray menu is a native Win32 `TrackPopupMenu` and is deliberately not reproduced as vector art.

## Palette

The document carries 16 paint styles under `Overlyric/` — `Window`, `Nav`, `Nav selected`,
`Divider`, `Text`, `Text dim`, `Text faint`, `Control`, `Control selected`, `Accent`,
`Knob`, `Warning`, `Overlay backdrop`, `Overlay progress`, `Overlay progress track`,
`Overlay text` — so a change can be made in one place.

## Regenerating

The document was built through the FigCraft MCP server (the `figcraft` entry in the DSH MCP patch):
frames and layers were created from a small builder that mirrors the draw code, so a change to the
constants above can be applied by editing the builder and rebuilding the affected frame rather than
by nudging layers by hand. The document is a snapshot of Overlyric 0.4.0 (`3e56157`, 2026-11-05) plus the search-flow
change that moved the query into the selector's header.