# Using Overlyric

Overlyric has no main window. This page is about steering it once it is running: the shortcuts, the
control bar, the tray, the settings window, and the lyric windows.

## Running Overlyric

Start `Overlyric.exe`. It lives in the notification area; start playing something in any player and
the overlay appears.

To preview the overlay without playing anything:

```powershell
Overlyric.exe --demo
```

Demo mode drives the overlay from a canned synced track, so you can judge the look, try both modes
and drag it around with nothing playing.

To open the configuration window without going through the tray:

```powershell
Overlyric.exe --settings
```

## Global hotkeys

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+H` | Show / hide the overlay |
| `Ctrl+Alt+M` | Switch between single-line and multi-line |
| `Ctrl+Alt+L` | Lock / unlock. Unlocked, the overlay becomes draggable and accepts the mouse wheel |
| `Ctrl+Alt+,` | Shift lyrics earlier by 0.25 s |
| `Ctrl+Alt+.` | Shift lyrics later by 0.25 s |
| `Ctrl+Alt+0` | Reset the timing offset |

These are the defaults. Every one of them can be rebound in **Settings → Shortcuts**: click the
shortcut, press the combination you want, and it is registered immediately. A combination that is
already used by another action, or already taken by another application, is refused with the reason
shown instead of being silently swallowed; a shortcut with no Ctrl, Alt or Win is refused too.
`Open settings` ships unbound, so it costs nothing until you want it.

Every global hotkey confirms itself with a small toast. It names the state the shortcut just entered,
and a timing nudge names the resulting total offset rather than repeating the step: `Ctrl+Alt+,`
reads `Offset -0.25s`, and a second press reads `Offset -0.50s`. The toast is drawn in its own
click-through window, opposite the overlay's anchor — so "Overlay hidden" can still say so after
`Ctrl+Alt+H` has made the lyric window disappear — and it dismisses itself after 1.8 seconds.

![The Shortcuts section](images/settings-shortcuts.png)

## Overlay controls

**Unlock the overlay and move the mouse over the lyrics**: a control bar appears in the same window.
It disappears again about half a second after the pointer leaves, and it never appears at all while
the overlay is locked — locked is click-through, which means no surface to hover.

| Control | What it does |
| --- | --- |
| **Settings** | Opens the settings window |
| **− / +** | Nudge the lyric timing by 0.25 s; the value shows the resulting total |
| **Reset** | Return the timing offset to zero; dimmed when there is nothing to reset |
| **Full lyrics** | Open the complete lyric sheet |
| **Choose lyrics** | Open the lyric selector |

![The overlay control bar](images/overlay-controls.png)

*Captured from the overlay window itself; the background is black because the window has a transparent
backdrop.*

The bar is drawn above the lyrics when there is room and below them when there is not, and it brings
its own backdrop, so it stays readable with the OLED preset's fully transparent backdrop. It is
deliberately outside the burn-in pixel shift: a button that moves under the pointer is a button that
is hard to hit. The settings window is topmost like the overlay, and the overlay stops re-asserting
its own z-order while one of Overlyric's windows is open — otherwise the lyrics would climb back over
the window you are reading every few ticks.

## Tray icon

Left-click toggles the overlay. Right-click opens a deliberately short menu: the track line
(informational — greyed out and not selectable), **Lyrics…** (which opens the selector window), the
three switches worth reaching for mid-song (show overlay, mode, lock), and **Settings…**. Everything
else moved into the settings window, because a menu that dismisses itself on every click is a poor
place to change anything.

## The lyric selector and viewer

**Choose lyrics** — from the overlay controls or the tray — opens the selector described under
[Choosing a version by hand](lyrics.md#choosing-a-version-by-hand). It is a normal window, not a menu: it can
show song details, a badge per record, lengths and match deltas side by side, and it stays open while
the listener compares choices and hears each one on the overlay behind it. The list scrolls with the
wheel, the scrollbar and the keyboard; **Automatic (best match)** is the first row.

![Choose lyrics in demo mode](images/lyric-picker.png)

*Demo tracks are deliberately not sent to LRCLIB, so the list is empty here; a real track lists its
records, best first.*

**Full lyrics** opens the complete sheet described under [The complete lyric sheet](lyrics.md#the-complete-lyric-sheet).
Both windows are fixed-size, dark, and close with **Esc**; they reuse the overlay's DirectComposition
stack and the settings window's palette, so they add no new UI framework.

![The full lyric sheet in demo mode](images/lyric-viewer.png)

*The sheet in demo mode. A real track also carries the LRCLIB record it came from; demo tracks do not.*

## Settings window

`Settings…` in the tray, or `Overlyric.exe --settings`, opens a fixed-size dark window with eight
sections down the left: **Overlay** (mode, lines shown, track info, progress bar, font sizes, the info
pop-up), **Shortcuts** (rebind the global hotkeys), **OLED** (burn-in controls, see below),
**Behaviour** (the hide rules, the player warning and the missing-lyrics notification), **Timing**
(lyric offset), **Position** (anchor, offsets, reset), **Advanced** (open the settings or cache folder,
version) and **Source** (which player to follow, and the order to prefer them in). **OLED** is covered
in [OLED and burn-in](oled.md).

![The settings window, on the Overlay section](images/settings-window.png)

*One user's configuration, not the defaults: this is the window as it opens after the settings have
been changed.*

Changes apply live and are saved as they are made, so the overlay behind the window shows the effect
immediately. `Esc` or the close button hides the window; the process keeps running and the overlay
stays up. Hiding is split into "hide when no lyrics" and "hide instrumentals", so a wordless track
can either vanish or simply say so.

## Karaoke

The active line sweeps as it is sung: the part already sung is painted bright and the rest stays
dim, so the eye follows the voice instead of the whole line changing colour at once.

How the sweep is timed depends on the sheet:

- **Word timings.** lrclib's `lyricsfile` carries `lines[].words[]` with a start and an end per word,
  and `hasWordSync` says whether a record has them. When it does, the sweep follows them exactly.
  That is why a record with word timings is ranked above one with only line timings.
- **Line timings only.** The line's own span is shared out over its singable characters — letters and
  digits; punctuation and spaces are not sung. This is an estimate: it assumes the words are
  delivered evenly, so it drifts inside a line with a long held note or an instrumental gap. It is
  the fallback that covers the whole catalogue, not a defect.

The sheet's own `end_ms` is used when it has one, which matters across instrumental breaks: "the
next line starts here" can be minutes away, and the sweep would otherwise crawl the whole time.

The sweep costs a frame rate rather than a tick rate. While it is on, the overlay redraws about 30
times a second instead of four, and the playback clock is held to a tighter leash (150 ms instead of
400 ms), because a syllable is shorter than a line. The **Karaoke sweep** toggle in **Overlay** turns
all of that off and returns the overlay to the cheaper behaviour.

Word timings are rare in practice: lrclib does not align audio, it only publishes word timings that
someone already submitted. The interpolated path is the common one.

On an OLED panel the unsung half of a line is also the part that stands still and stays lit.
**Black out unsung words** in **OLED** — part of the preset — paints it pure black instead of the dim
inactive colour, so the line lights up word by word and the panel stays off ahead of the voice. With
the sweep off there is no unsung half to draw, so the setting does nothing; that is why the preset
also turns the sweep on. The surrounding lines are unaffected: they keep the dim colour, because
reading ahead is the reason they are there.

## Moving and locking the overlay

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

Unlocked is also the only state in which the control bar can appear. Locked, the overlay is invisible
to the mouse and there is nothing to hover, which is the whole point of the lock.