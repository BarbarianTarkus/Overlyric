# Overlyric documentation

The [README](../README.md) is the short version. These pages are the reference: the reasoning behind
the design, the exact behaviour of each feature, and the measurements the claims rest on.

## Start here

| Page | What it covers |
| --- | --- |
| [How it works](architecture.md) | The media-session pipeline, choosing a player, the adaptive clock, the failure diagnosis, and why the overlay is built on DirectComposition |
| [Lyrics](lyrics.md) | Matching the right LRCLIB record, the manual version selector, the full sheet, and instrumental detection |
| [Using Overlyric](usage.md) | Global hotkeys, the overlay control bar, the tray menu, the settings window, karaoke, moving and locking the overlay |
| [OLED and burn-in](oled.md) | The burn-in controls and the one-click OLED preset |
| [Configuration](configuration.md) | Every key in `config.json`, the lyric cache and pinned versions |
| [Development](development.md) | Requirements, build, the icon, resource budget, the probes, and the project layout |
| [SonarQube](sonarqube.md) | The self-hosted instance, how an analysis runs, what is in scope, and the tools agents get |
| [Fullscreen feasibility](fullscreen-overlay-feasibility.md) | Why borderless works, why true exclusive fullscreen cannot, and what the alternatives would cost |

The supporting research for the fullscreen question lives in [`feasibility/`](feasibility/).

## Screenshots

Every screenshot is of the running application. The game in the overlay captures is *Core Keeper*.
Settings and demo captures show one user's configuration, not the defaults.

![The karaoke sweep in demo mode](images/demo.gif)

**The karaoke sweep**, captured from the overlay window in `--demo`. The overlay has a transparent
backdrop, so the black is the capture background rather than a theme.

### Overlay

| Single line | Multi line |
| --- | --- |
| ![Single-line overlay over a running game](images/overlay-single-line.png) | ![Multi-line overlay in demo mode](images/overlay-multi-line.png) |
| Following live TIDAL. The sweep has lit the first half of the line. | `--demo` driving a canned, synced track over the same game. |

### Lyrics windows

| Full lyrics | Choose lyrics |
| --- | --- |
| ![The full lyric sheet](images/lyric-viewer.png) | ![The lyric selector in demo mode](images/lyric-picker.png) |
| The sheet in demo mode. A real track carries its LRCLIB record id; demo tracks do not. | Demo tracks are deliberately not sent to LRCLIB, so the list is empty here. A real track lists its records. |

### Settings

| Lyrics | Shortcuts |
| --- | --- |
| ![The Lyrics settings section](images/settings-window.png) | ![The Shortcuts settings section](images/settings-shortcuts.png) |
| Mode, lines shown, text sizes, the karaoke sweep, and the track info and info pop-up it owns. | Click a chord, press the new combination, and it registers immediately. |

| Appearance | Players |
| --- | --- |
| ![The Appearance settings section](images/settings-oled.png) | ![The Players settings section](images/settings-source.png) |
| After **Apply preset**: no backdrop, no highlight bar, dimmed surrounding lines. | Who is followed, and the order the rest are tried in; the list comes before Windows' own choice. |

| Timing | Advanced |
| --- | --- |
| ![The Timing settings section](images/settings-timing.png) | ![The Advanced settings section](images/settings-advanced.png) |
| The current output, the offset in force, and which override layer supplied it. | The folders, the cache size and clearing it, and the version in the footer. |

![The Behaviour settings section](images/settings-behaviour.png)

**Behaviour** is where the overlay gets out of the way: pause / no-lyrics / instrumental hides, the
per-app hide list (add the app currently in front), the two warnings, and start with Windows.

The [README](../README.md) uses a shorter selection.