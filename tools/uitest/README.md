# UI tests

`tools/uitest` is a self-contained console suite that drives the real application on a **private
Windows desktop** and checks what it draws and what it persists. It is the only automated test that
exercises the custom-drawn Win32/Direct2D surface: the application has no XAML, no web view and no
UI Automation, so there is nothing for a normal UI-test framework to attach to.

Contents:

| Path | What it is |
| --- | --- |
| `Overlyric.UiTests/` | the harness (`dotnet` console project, no NuGet packages) |
| `fixtures/settings.json` | the `config.json` every scenario starts from |
| `baselines/<scenario>/<step>.png` | the committed captures the suite compares against |

## Why a private desktop

The suite must not steal focus, move the cursor, cover the user's windows or interfere with a
running Overlyric. Windows queues input and composes windows per **desktop**, so the suite creates
its own (`CreateDesktopW`), launches a worker process onto it (`STARTUPINFO.lpDesktop`), and never
touches the interactive desktop at all. The proof is measured, not assumed:

* `GetForegroundWindow()` and `GetCursorPos()` are sampled before the run and after it, and the
  run fails if they changed and Windows recorded no user input in that window of time
  (`GetLastInputInfo`). A user moving their mouse while the suite runs is reported as a note, not
  as a failure: they are allowed to use the machine.
* The production data folder (`%LOCALAPPDATA%\Overlyric`) is snapshotted before and after. Its
  `config.json` is pinned by SHA-256 and length, and the folder's entry names must not appear,
  disappear or change kind. Sizes and timestamps of the running production application's own files
  (its `diagnostics.log`, cache churn) are intentionally **not** compared: the claim is that the
  suite never wrote there, not that the user's app was idle.

The parent also opens a kill-on-close job object and puts the worker in it, so if the parent is killed
(a stopped script, a closed terminal, a CI cancel) the kernel kills the worker and the invisible test
copy of the application along with it instead of leaving them running on a dead desktop.

Inside that desktop the worker finds the application's window with `EnumWindows`, captures it with
`PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` into a 32bpp DIB, and drives it by posting
`WM_MOUSEMOVE`/`WM_LBUTTONDOWN`/`WM_LBUTTONUP`/`WM_KEYDOWN` to its client coordinates. The suite
never calls `SendInput`, `SetCursorPos`, `SetForegroundWindow` or anything else that could reach the
user's session. It kills only the process trees it started, and closes the desktop it created.

## Requirements

* Windows 10/11, .NET 10 SDK.
* The application built in Debug:
  `dotnet build src\Overlyric.csproj -c Debug`
  (the suite defaults to `src\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Overlyric.exe`;
  pass `--exe` to use another build).

## Running

```powershell
# first time, or after changing the window on purpose: record the baselines
dotnet run --project tools\uitest\Overlyric.UiTests -- --update

# normal run: compare against the committed baselines
dotnet run --project tools\uitest\Overlyric.UiTests

# list the scenarios
dotnet run --project tools\uitest\Overlyric.UiTests -- --list

# one scenario
dotnet run --project tools\uitest\Overlyric.UiTests -- --filter settings-toggle

# prove the captures are deterministic: run everything twice, the second pass compares
dotnet run --project tools\uitest\Overlyric.UiTests -- --repeat 2

# watch a failing step: run on your own desktop (debugging only, see below)
dotnet run --project tools\uitest\Overlyric.UiTests -- --visible --filter settings-toggle
```

`--visible` skips the private desktop so the windows are visible while the run happens. That is the
only mode without the no-interruption guarantee: windows can take focus and appear over the user's
work, the application calls `SetForegroundWindow` when it opens, and the desktop check is
downgraded to notes. Use it to watch a step; use the default mode for everything else.

Exit code is 0 when every scenario passes, the safety checks hold and every expected scenario run
was reported; 1 otherwise. A full run takes about 10 seconds (see the per-scenario times printed at
the end of the run). The artifact directory is printed last:
`<repo>\.dsh-tmp\uitest\<runId>\` (`.dsh-tmp/` is ignored locally; add it to `.gitignore` in a clone
where it is not).

## Scenarios

| Scenario | What it proves |
| --- | --- |
| `settings-sections` | clicks the eight navigation entries and compares each section to its baseline |
| `menu-navigates-windows` | clicks the shared Window menu across Settings → Lyrics → Versions → Diagnostics → Settings and proves open-or-raise and Escape |
| `launcher-opens-windows` | replays the tray callback, checks the launcher, and opens Versions from its rows |
| `settings-toggle-persists` | the Karaoke switch flips and the value reaches `config.json` (and back) |
| `settings-stepper` | `+`/`-` on Lyric size move the persisted `ActiveFontSize` by the window's step (2) |
| `settings-escape-closes` | `WM_KEYDOWN(VK_ESCAPE)` hides the settings window |
| `overlay-demo-smoke` | `--demo` overlay: `WS_EX_TRANSPARENT`/`WS_EX_NOACTIVATE`, 1200x200 px, and a layout switch to 1040x430 px |

The last scenario sends `--layout=multi` to the instance's isolated command pipe
(`\\.\pipe\Overlyric.Commands.v1.<instance>`). Some tool sandboxes deny named-pipe access to every
process, including a pipe created and connected inside one process; the scenario detects that with a
probe, records the pipe step as **SKIP** with the reason, and posts the same command id to the
application's message window instead. The layout assertion still runs; only the pipe transport is
unverified in such a runner, and the skip is printed.

## Environment seams

Every scenario launches a **fresh instance** with:

| Variable | Effect |
| --- | --- |
| `OVERLYRIC_TEST_INSTANCE=uitest-<8 hex>` | moves the mutex, the command pipe and the data folder into a namespace of their own |
| `OVERLYRIC_TEST_DATA_ROOT=<repo>\.dsh-tmp\uitest\<runId>\<scenario>\<repeat>` | keeps the instance's data inside the repository |
| `OVERLYRIC_TEST_DPI=96` | makes every window lay out at 96 DPI regardless of the monitor |

The data folder the app creates under that root is `<data root>\Overlyric-Test\<instance>` (the
`InstanceScope` naming); the fixture is written to `config.json` there before the process starts.

The DPI seam is why the baselines are machine-independent. The suite asserts the observable proof -
the settings window's client area is exactly **780x584 pixels** - and records what the OS reports as
the monitor DPI in the result JSON for diagnosis. It deliberately does **not** assert
`GetDpiForWindow(hwnd) == 96`: the OS keeps reporting the real monitor DPI, and only the
application's own layout/size math is overridden.

## The fixture

`fixtures/settings.json` is a complete `config.json` (the app's JSON is PascalCase with string
enums). It exists so every section draws the same thing on every machine, and it doubles as the
expectation: the toggle and stepper scenarios read the expected value out of it instead of
hard-coding a second copy. Notable choices:

* `StartWithWindows: false` - a true value would make the application write the current user's Run
  key on startup, which a test must never do.
* The shortcuts are unusual chords (`Ctrl+Alt+Shift+F5`...) so they cannot collide with whatever the
  host already owns; a collision would draw the chip in the "taken" colour and churn the baseline.
* `SourcePriority` names two fixed players so the Players section is mostly static. Live media
  state still reaches that section (and is masked, below).
* `PlacementVersion: 1` so the app's legacy-placement repair is a no-op.

## Captures, masks and thresholds

Captures land in the artifact directory under `<scenario>/repeat-<n>/captures/` - always, pass or
fail - and the diff (if any) is written next to them as `<step>.diff.png`, the capture with every
failing pixel painted red.

Both the baseline and the capture are 32bpp PNGs, and `Frame.Load` reads those bytes directly
instead of drawing the file onto a second surface. GDI+ would colour-manage that draw on a machine
with a display profile, rewriting every value while the two images still looked identical - the
comparison is meant to be about drawn pixels, not about the monitor's profile.

A pixel differs when any colour channel moves by more than 8; a step fails when more than
`max(32, 0.02% of unmasked pixels)` differ. Eight leaves room for text rasterisation jitter; the
floor of 32 keeps a couple of rounding pixels from failing a frame.

Masked regions are DIP rectangles listed in `SettingsGeometry.MasksForSection`, each with the reason
it is masked:

| Section | Masked | Why |
| --- | --- | --- |
| Timing | row 0 note | the default audio endpoint's friendly name is the host's hardware |
| Behaviour | row 6 note | prints the current foreground application |
| Players | status value, pin switch, row 7 | follow live media sessions on the host (is a player publishing, and which) |
| Advanced | cache and local-library notes | print live cache/file counts |
| Shortcuts | the text band of each chip | a chip is painted in the warning colour when its chord is already owned somewhere in the session (the production app, another suite run, any program), which is machine state, not this window's design; the chip's shape and position stay compared |
| all sections | footer | prints the product version, which changes every release |

Everything else - labels, control positions, values, switch states, selection state - comes from
the fixture and must be pixel-identical. `--repeat 2` on top of an `--update` is the self-check:
the first pass writes the baselines, the second compares against them, so a baseline that is not
deterministic fails immediately instead of on the next developer's machine.

## Updating the baselines

1. Build the app and the harness.
2. `--update --repeat 2` and confirm both passes are green.
3. Run plain `--update` once more, then a plain `run` twice more, and commit the PNGs under
   `tools/uitest/baselines/`.

Baselines are Windows captures of this application at 96 DPI; they are not portable to another OS
or to an intentional visual redesign.

## Adding a scenario

1. Add a class to `Scenarios.cs` implementing `IScenario` and register it in
   `ScenarioRegistry.All`.
2. Launch through `ScenarioContext.LaunchSettings()` / `LaunchDemo()`; they handle the seams, the
   fixture, the window wait and the geometry assertions.
3. Use `Click`, `Key`, `PostCommand` for input and `Check`, `WaitForConfig`, `CaptureStep`,
   `CaptureArtifact` for assertions.
4. Put coordinates in `SettingsGeometry` next to the window constants they mirror, not inline.
5. Re-record baselines and check the new capture by eye once.

## Known limits

* The overlay is a layered window; `PrintWindow` is not a promise for that surface, so the demo
  scenario asserts window facts and keeps its captures as review artifacts only.
* Live media sessions on the host are visible to the app under test (Windows media sessions are not
  per-desktop). That is why Players and the Timing output row are masked; it is also why a run on a
  machine that is actively playing something still produces stable baselines.
* Global hotkey ownership is session-wide, not per-desktop: a second suite run at the same time, or
  another program holding the fixture's chords, makes the Shortcuts chips paint in their "taken"
  colour. The chip text band is masked for that reason. Do not run two suites at once.
* `--visible` is a debugging aid and the only mode that does not promise the interactive desktop is
  left alone.
* A non-interactive session without a shell still runs the suite; only the shell-facing surfaces
  (tray icon) are unavailable to the app, and no scenario depends on them.
