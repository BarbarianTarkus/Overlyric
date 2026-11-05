# UI design file

[`overlyric-ui.figcraft`](overlyric-ui.figcraft) is the whole application as an editable FigCraft
document: every window drawn at the DIP size the code opens it, with the real theme colours, the real
geometry constants and the strings the draw code actually uses. [`preview.png`](preview.png) is a
flat render of the same page for browsing without FigCraft.

It is a copy of *what is implemented*, not a proposal: nothing here exists that the app does not
draw, and nothing the app draws is left out except the native tray menu.

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

## The search flow

Searching lives in one window. The sheet's **Choose lyrics** button (footer and empty state) closes it
and opens the selector; the selector's header is an editable field that starts on the track's own
query, and results replace the automatic list instead of opening another screen. `Ctrl+F` remains the
sheet's quick in-place field. The selector's **Open on LRCLIB** link uses the path form,
`lrclib.net/search/<query>`, because the `?q=` form lands on an empty page.

The two frames that carry this — **Viewer · no lyrics** and **Picker · searching** — are the ones to
look at when the flow changes again.

## How the numbers were taken

Every position is a constant from the draw code rather than an eyeballed value:

* Settings: `NavWidthDip 196`, `PadDip 30`, `RowHeightDip 46`, `RowsTopDip 132`,
  `ViewportTop 108`, `ViewportBottom clientHeight - 40`, and the control sizes in
  `Toggle`/`Segmented`/`Stepper`/`Slider`/`Button`.
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
