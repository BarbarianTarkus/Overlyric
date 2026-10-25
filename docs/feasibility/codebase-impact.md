# Fullscreen gaming overlay for lyrics — codebase impact

Scope: what a fullscreen overlay for lyrics would take *in this repository*, and what each candidate
option costs and breaks. Investigation only: nothing was built, run or modified except this file.
Analysed against the working tree (dirty: the overlay-notice work, `installer/` and the README
additions are uncommitted), which is what ships next. `src/` is 11.5k C# LOC; one project
(`Overlyric.slnx:1-5`).

## 1. The overlay pipeline as it exists

### 1.1 Window and pixels

- `OverlayWindow` is a plain `WindowBase` window, class `OverlyricOverlayWindow`
  (`src/Overlyric/Overlay/OverlayWindow.cs:55`), style `WS_POPUP | WS_CLIPSIBLINGS` (`:57`), extended
  style `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP |
  WS_EX_TRANSPARENT | WS_EX_LAYERED` (`:59-60`). It never activates (`ShowWindow ... SW_SHOWNA`,
  `:168-177`).
- It is a **fixed-size pill**, not a screen-sized surface: 1200×200 DIP single line, 1040×430 DIP
  multi-line (`:30-33`, `:404-407`), positioned inside the monitor's **work area** (`rcWork`) at an
  anchor plus offset (`ApplyBounds`, `:362-394`).
- Pixels come from DirectComposition: D3D11 device → `CreateSwapChainForComposition` with
  `B8G8R8A8_UNorm`, premultiplied alpha, `FlipSequential` (`src/Overlyric/Overlay/CompositionHost.cs:130-150`),
  a DComp target for the HWND and a visual holding the swap chain (`:97-102`), with Direct2D drawing
  into the back buffer (`:104-106`, `:152-166`). `WS_EX_NOREDIRECTIONBITMAP` means Windows keeps no
  redirection surface, so the compositor is the only thing that can show these pixels.
- Frames are drawn only when content changes: `OverlayWindow.Render()` (`:194-219`) is called from
  `OnTick` only when `VisualEquals` differs (`src/Overlyric/App/AppHost.cs:415-424`), plus on
  theme/DPI/mode changes. `Present(1, …)` (`CompositionHost.cs:220`) is the only present.
- Click-through is layered-window hit testing: `SetLayeredWindowAttributes(alpha 255)`
  (`OverlayWindow.cs:106-117`), `HTTRANSPARENT` while locked (`:271-296`), `HTCAPTION` over rendered
  content when unlocked; interactive bounds come from the renderer's `ContentBounds`
  (`:298-320`).

### 1.2 Timing, visibility and z-order

- One hidden `MessageWindow` owns the app timer and the tray/hotkey callbacks, deliberately not the
  overlay window so the overlay can be recreated (`src/Overlyric/Interop/MessageWindow.cs:5-9`,
  `:22-25`); `WM_TIMER` → `OnTick` (`AppHost.cs:341-347`), armed at `AppHost.cs:226-227`.
- Adaptive interval: 33 ms karaoke sweep, 250 ms playing, 1000 ms otherwise
  (`AppHost.cs:19-24`, `:963-980`). The timer is re-armed only when the wanted interval changes
  (`:979`), which is what keeps idle CPU at the measured ~0.15 % (README.md:333-344).
- Visibility is recomputed every tick from track/lyrics/config (`IsOverlayVisible`,
  `AppHost.cs:800-828`; applied at `:902-917`), with `SetVisible` early-returning on no change
  (`OverlayWindow.cs:168-177`).
- **Topmost reassertion is tick-counted, not time-based**: `TopmostReassertTicks = 20`
  (`AppHost.cs:27-28`) and `ReassertTopmost()` is called when `++_topmostTicks >= 20` while visible
  (`:921-925` → `OverlayWindow.cs:181-183`). At the 250 ms path that is ~5 s (matching the comment);
  at the 33 ms karaoke path ~0.66 s; at the 1 s idle path **20 s**. A game that raises its own window
  topmost can therefore displace the overlay for up to 20 s while the overlay is idle-but-visible.
- Display/DPI changes: `WM_DISPLAYCHANGE` and `WM_DPICHANGED` re-resolve DPI, re-apply bounds and
  rebuild the composition host (`OverlayWindow.cs:245-260`); the settings window does the same
  (`src/Overlyric/App/SettingsWindow.cs:499-505`). A resolution-changing FSE switch therefore does
  reach the app, but a same-resolution FSE switch does not, so `WM_DISPLAYCHANGE` is an accelerator
  for detection, never the detector.
- There is **no foreground-window awareness anywhere**. `NativeMethods.cs` (415 hand-picked lines)
  has no `GetForegroundWindow`, `SetWinEventHook`, `EnumWindows`, `GetWindowThreadProcessId`,
  `SHQueryUserNotificationState`, `DwmGetWindowAttribute`, `SendInput`, `CreateFileMapping` or any
  process/IPC/input API (`src/Overlyric/Interop/NativeMethods.cs:1-415`). Every option below that
  needs one of those starts from zero.

### 1.3 Why it cannot cover exclusive fullscreen

Stated plainly in README.md:313-318. The mechanism is not a style or a flag that was forgotten:
exclusive fullscreen bypasses DWM composition and hands the output to the game's own swap chain, so a
`WS_EX_TOPMOST` window whose pixels only exist as a DirectComposition visual
(`CompositionHost.cs:97-102`) has nothing to be composited over. No change to the window styles
(`OverlayWindow.cs:57-60`), the DComp target (`CompositionHost.cs:97-102`) or hit testing
(`:271-296`) reaches past that. The overlay is also a 1200×200 pill rather than a screen-sized
surface, so even a compositor-level fix would be a different feature (see §4.3), not a repair.

### 1.4 Invariants the options are measured against

| Promise | Evidence |
| --- | --- |
| No injection, hooking or DLL loading into other processes | README.md:315-318 |
| Player-agnostic: no per-app integration | README.md:3-6, 44-58 |
| Per-user MSI, no admin/UAC, unsigned | README.md:399-403, 444-454; `installer/Overlyric.wxs:17-23, 32, 50-57`; `src/Overlyric/app.manifest` (no `requestedExecutionLevel`) |
| NativeAOT + full trim, 9.2 MB exe, <100 MB WS | `src/Overlyric/Overlyric.csproj:14-24, 36-44`; README.md:322-359 |
| No UI framework; raw Win32 + D3D/D2D/DW | README.md:299-311; `Overlyric.slnx:1-5` |
| Per-user state only, under `%LOCALAPPDATA%` | `src/Overlyric/App/AppConfig.cs:137-145` |
| Installer contract is machine-verified | `installer/tests/verify-installer.ps1:5-35` (2,429 lines) |

## 2. Per-option integration analysis

### (a) Do nothing + document

Already done and already committed: README.md:313-318 is in HEAD (2e4d939). Incremental work: zero
code, at most a few lines tying it to the detection behaviour in (b). Cost: nothing. Breaks nothing.
Value: honesty only — it is the correct baseline, not an improvement.

### (b) Detect exclusive fullscreen and tell the user

- **New logic**: one file cloned from the house pattern, `Media/FullscreenWatch.cs` modelled on
  `src/Overlyric/Media/PlayerWarningWatch.cs:33-127` — 2 s poll gate (`:35-36`), 10 s sustain
  (`:38-39`), announce once per situation (`:108-113`), `Reset()` (`:116-126`). Detection itself is
  the OS teammate's question; the documented candidate is `SHQueryUserNotificationState`
  (`QUNS_RUNNING_D3D_FULL_SCREEN`=3 / `QUNS_BUSY`=2) plus a foreground-window/monitor-rect compare,
  which needs `GetForegroundWindow`, `GetWindowThreadProcessId`, `QueryFullProcessImageNameW` and a
  shell32 import added to `NativeMethods.cs` (none present, §1.2).
- **Insertion point**: `AppHost.OnTick`, beside `UpdatePlayerWarning` (`AppHost.cs:379`), with a
  field and a "new situation" guard exactly like `AppHost.cs:835-866`. Poll cost is one shell call
  per 2 s on the slow path, so the measured CPU budget is untouched.
- **The UX trap that decides this option**: `OverlayNotice`/`InfoPopup` are drawn *inside the
  overlay* (`src/Overlyric/Overlay/OverlayNotice.cs:13-21`, `AppHost.cs:635-689`,
  `src/Overlyric/Overlay/LyricsRenderer.cs:389`), i.e. invisible in precisely the situation being
  reported. The primitives that can work are `TrayIcon.Alert` (`src/Overlyric/App/TrayIcon.cs:85-106`),
  the tray tooltip (`AppHost.cs:936-961`) and the tray-menu explanation rows
  (`PlayerWarningMenuItems`, `AppHost.cs:879-900`). Caveat worth stating in the code: a balloon may
  itself be suppressed while a game is exclusive-fullscreen (that is what `QUNS_BUSY` reports), so
  tooltip/menu are the durable surfaces and the balloon should be deferred until the game leaves
  fullscreen. Multi-monitor nuance: if the FSE game is on another monitor the overlay *is* visible, so
  the channel can depend on whether the overlay's monitor is the FSE one (`MonitorFromWindow` exists,
  `NativeMethods.cs:352`).
- **Settings surface**: a `Fullscreen notice` toggle is a `Section` addition
  (`SettingsWindow.cs:47-71`), title/hint (`:206-219`), a draw method in the switch (`:1121-1165`) and
  a `Control` id (`:73-115`); ~60-90 LOC. Config: one bool following `WarnWhenPlayerDoesNotPublish`
  (`AppConfig.cs:99-105`).
- **Cost**: ~120-200 LOC + a 60-100 LOC probe; no new artifacts, dependencies, signing or elevation.
  **Residual risk**: detection is necessarily a heuristic (`SetFullscreenState` is in-process, and
  Windows 11 fullscreen optimizations make many "fullscreen" games composited and overlayable), so
  false positives are the failure mode; the mitigation is the same sustain/announce-once discipline
  the player warning already uses.
- **Verdict**: the honest first step. It does not make lyrics appear over FSE; it stops the app from
  looking broken.

### (c) Nudge or auto-configure the game to borderless

- **What exists to build on**: nothing. No process enumeration, no foreign-window manipulation, no
  input synthesis, no config-file integration (§1.2).
- **Levers, and their honesty**: (i) the game's own borderless/windowed-fullscreen setting — guidance,
  zero code; (ii) `Alt+Enter`/`F11` — needs `SendInput`/`keybd_event` interop (~40 LOC) plus per-title
  knowledge, and synthesizes keystrokes into a game, which is the thin end of the injection wedge;
  (iii) per-exe compatibility flags under `HKCU\...\AppCompatFlags\Layers` — the *documented* flag here,
  `DISABLEDXMAXIMIZEDWINDOWEDMODE`, **disables** fullscreen optimizations, i.e. pushes a game toward
  true FSE: the opposite of the goal, and there is no documented flag that forces windowed mode;
  (iv) rewriting game config files — per-title, per-store, per-user paths, fragile, and squarely
  against README.md:3-6/44-58.
- **Cost**: guidance 30-60 LOC (rides on (b)); the `Alt+Enter` nudge ~120-200 LOC plus a maintained
  title list; config rewriting 300+ LOC plus that list and a support burden.
- **Verdict**: guidance yes; automation has no supported mechanism and converts the project into the
  per-app integrator it deliberately is not.

### (d) In-process injection overlay helper

The full inventory, because this is the option that looks cheap until it is costed:

- **New native artifact**: x64 C++ DLL (static CRT `/MT`), hooking `IDXGISwapChain::Present`
  (separate vtable slots for D3D11 vs D3D12 and per-interface versions) and `vkQueuePresentKHR`
  (Vulkan needs a layer or a `vulkan-1.dll` hook). Realistically 2,000-5,000 LOC C++ plus a per-title
  workaround list. Build: new CMake/MSBuild project, a slot in `Overlyric.slnx:1-5`, and a new
  toolchain step (MSVC is already a prerequisite for AOT linking, README.md:365-368, so this is the
  one infrastructure item that is partly there).
- **Injection mechanics**: `CreateRemoteThread` + `LoadLibraryW` (needs `OpenProcess` with
  `PROCESS_VM_OPERATION`, and fails against elevated or anti-cheat-protected games), or a CBT
  `SetWindowsHookEx` (same-bitness only, `DllMain` restrictions), or a launcher shim. All are
  explicitly what the README rules out (README.md:315-318).
- **IPC**: named shared memory + events, or a named pipe (`CreateFileMappingW`/`MapViewOfFile`
  P/Invokes absent today), a producer loop, and a frame contract. Two shapes, both expensive:
  (1) the helper renders lyrics itself — duplicates the 704-line Direct2D/DirectWrite renderer
  (`src/Overlyric/Overlay/LyricsRenderer.cs`) in C++; (2) Overlyric renders to a D3D11 shared texture
  and the helper blits it in `Present` — needs an offscreen device/renderer path that does not exist,
  because `CompositionHost` is welded to an HWND-bound swap chain (`CompositionHost.cs:76-128`), plus
  cross-device synchronisation.
- **Installer**: a new `Component`/`File` for the DLL following `installer/Overlyric.wxs:195-206`, a
  `Level=2` opt-in feature like `DesktopShortcut` (`:168-174`), payload harvesting already general
  (`installer/build-installer.ps1:175-201`); but `verify-installer.ps1`'s assertion 2 ("exactly the
  publish output minus `*.pdb`", `installer/tests/verify-installer.ps1:5-35`) must be rewritten, and
  that script is the project's acceptance contract.
- **Security model**: an unsigned third-party DLL inside a game process is flagged by EAC, BattlEye,
  Ricochet and Vanguard, and by some games outright; signing needs an OV/EV certificate (nothing is
  signed today, README.md:448-449 — the build script's `signtool` path signs only the MSI). Reaching
  elevated games requires running Overlyric elevated, which kills "no admin" (README.md:399-403;
  `Overlyric.wxs:50-57`), or a service, which requires per-machine install (`README.md:450-451`).
- **Maintenance**: a graphics-API × store × title × anti-cheat matrix that never converges; issue
  reports become "the game crashed" and "I was banned".
- **Cost**: 6-12 person-months for a credible v1, with no guarantee for competitive titles.
- **Verdict**: the only mechanism that truly covers FSE, and incompatible with every stated project
  promise. Not viable without re-scoping the product.

### (e) Xbox Game Bar widget (separate packaged app)

- **What it is**: an MSIX-packaged UWP/XAML app with a Game Bar widget extension, hosted in Game Bar's
  privileged overlay. It does show over many fullscreen games, but as a panel (opened/pinned), not a
  click-through lyric line.
- **Relation to this codebase**: no reuse of the window stack — `WindowBase`/`CompositionHost`/
  `LyricsRenderer` are Win32-window-shaped and irrelevant to XAML. The reusable asset is the pure
  logic: `Lyrics\` (~3.0k LOC, of which `LrcParser` 291, `LrclibClient` 680, `LyricsCache` 463,
  `LyricsService` 327, `VersionOverrides` 131, `InstrumentalDetector` 272) and `Media\`
  (`PlaybackClock` 291, `SourceSelector` 297,
  `NowPlayingSnapshot` 62; `MediaSessionWatcher` is already WinRT and survives packaging).
- **Extraction cost**: those types are `internal` in a single WinExe with no library boundary
  (`Overlyric.slnx:1-5`), depend on `AppConfig` and its source-generated JSON
  (`AppConfig.cs:234-236`) and on the Win32 CoreAudio interop (`Media/AudioSessions.cs:1-45`):
  roughly 500-900 LOC of restructuring into a shared library plus project changes.
- **Cost/artifacts**: new packaged project 1,500-3,000 LOC (XAML + C#), MSIX manifest with package
  identity, and a certificate — an unsigned MSIX cannot be installed, so the per-user unsigned MSI
  promise is broken; plus a second renderer to keep visually consistent and a store/sideload decision.
- **Verdict**: the only non-injection path that plausibly draws lyrics over some FSE games, but it is
  a second product with a different delivery model; consider only if packaging and a panel UI are
  acceptable.

### (f) Driver-level overlay

The only way to composite over a true FSE swap chain from outside the game is a kernel-mode
display/indirect-display driver or undocumented `D3DKMT`/`Dxgkrnl` interfaces. That means an EV
certificate, Microsoft attestation/WHQL signing, HVCI-compatible kernel code, a per-machine install
plus service lifecycle — and it is unconditionally treated as a kernel cheat vector by anti-cheat.
It contradicts per-user, unsigned and no-admin simultaneously (README.md:444-454). Out of scope:
no amount of effort makes it an acceptable delivery for this project.

## 3. Decision matrix

| Option | Covers true FSE? | Effort (LOC / artifacts) | Risk to users | Fits project constraints | Verdict |
| --- | --- | --- | --- | --- | --- |
| (a) Document only | No | 0 / none | None | Fits | Baseline; already in README:313-318 — keep |
| (b) Detect + notify | No | ~120-200 + probe 60-100 / none | None; heuristic false positives | Fits (no new deps, no elevation) | **Recommended now** |
| (c) Nudge borderless | No (only if the game complies) | 30-60 guidance; 120-200 with `Alt+Enter`; 300+ per-title | Input synthesis into games; support load | Guidance fits; automation does not | Guidance yes, automation no |
| (d) Injection helper | **Yes** | 2,000-5,000 C++ + 200-400 C# IPC + signing + installer + matrix; 6-12 pm | **High** — anti-cheat bans, crashes | Breaks no-inject, unsigned, no-admin, per-user, <100 MB reasoning | Reject unless re-scoped |
| (e) Game Bar widget | Partly (panel only) | 1,500-3,000 new + 500-900 refactor; MSIX + cert | Low, but signing/packaging burden | Breaks unsigned/per-user MSI; new product | Separate product, not this task |
| (f) Driver overlay | Yes (bypass) | 10k+ kernel LOC, EV + WHQL, reboot/service | Very high | Breaks everything | Out of scope |

## 4. Recommendations

1. **Ship (b), and make it the answer.** ~150-250 LOC total, no new artifacts: a
   `FullscreenWatch` following `PlayerWarningWatch`'s 2 s poll / 10 s sustain / announce-once shape
   (`PlayerWarningWatch.cs:33-127`), announced through the tray balloon plus a durable tooltip/menu
   row (`TrayIcon.cs:85-106`, `AppHost.cs:879-900, 936-961`), never through the overlay notice — that
   notice is invisible in exactly the failure case (`OverlayNotice.cs:13-21`). Say the game's name and
   the one remedy that exists: switch the game to Borderless / Windowed Fullscreen.
2. **Fix the topmost story for the games that are supported**, which is the cheapest real win on this
   list: reassert on foreground change (`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, out-of-context) or
   compare `GetForegroundWindow` on the slow tick and reassert once, instead of relying on the
   tick-counted 20-tick timer (`AppHost.cs:27-28, 921-925`) that can be 20 s apart on the idle path.
   ~40-70 LOC, no new dependencies, helps every borderless-windowed game.
3. **Offer a monitor-sized transparent canvas as an opt-in layout — and say clearly it is not an FSE
   fix.** Window bounds from `rcMonitor` instead of `rcWork` plus an anchor-driven content origin
   (`OverlayWindow.cs:362-394`; the renderer already lays out relative to its viewport,
   `LyricsRenderer.cs:77-115, 200-205`), new config + Settings row: ~150-250 LOC. Document the
   tradeoff: a monitor-covering layered topmost window can defeat MPO/independent flip and hurt frame
   pacing in the borderless games it targets, so it must be opt-in and measured with the probe
   harness, not default.
4. **Keep README.md:313-318 as the contract** and extend it by one line: what the app now detects and
   what the user should change. Do not add per-title automation (c-iii/c-iv); that is the philosophy
   the project is built on (README.md:3-6, 44-58).
5. **Evidence required before any injection path is reconsidered** — all five, not a subset:
   (i) probe data naming which real titles fail and confirming fullscreen optimizations are not
   already making them composited; (ii) a written anti-cheat vendor position (allowlist/review path)
   for an unsigned third-party module — folklore is not evidence; (iii) proof that a **non-elevated**
   helper can inject into the titles that matter, since elevated games are the common case; (iv) a
   signed-artifact budget (OV/EV cert, reputation, SmartScreen) and an installer contract that
   survives `verify-installer.ps1`; (v) demand evidence that users actually run true FSE, with a
   per-title maintenance estimate. Missing any one → ship (1)+(2) and stop there.

## 5. Repository history and dead code

- Git: one branch (`master`), no tags, no stashes, 8 commits from 2e4d939 to 579b1b2. No commit,
  branch or message mentions fullscreen, exclusive, injection, hooking, borderless, Game Bar or
  in-game overlays; the only matches for "overlay" are ordinary feature prose. **This question has not
  been attempted before in this repo** — nothing to revive, no abandoned work.
- The only overlay-adjacent commit is a741d28 ("Add a settings window…"), which changed
  `OverlayWindow.cs` to adopt a theme in place instead of rebuilding the window, and reworked
  `AppHost.cs`.
- Working tree is dirty: the overlay-notice feature (`OverlayNotice.cs`, `InfoPopup.cs`,
  `LyricStatus.cs`, `LyricsAlert.cs`, `probe/InfoTests`, `probe/ToastProbe`), `installer/` and ~142
  added README lines are uncommitted. All references above are to that working tree.
- No `TODO`/`FIXME`/`HACK` anywhere in `src/` (the single grep hit is a false positive in an
  `LrcParser` doc comment). No dead code in `Overlay/`: `RepairLegacyPlacement`
  (`AppHost.cs:1291-1328`) is a deliberate one-time migration, the `SetLayeredWindowAttributes`
  failure path (`OverlayWindow.cs:113-117`) is a real fallback, and `SwpFramechanged`/`SwpNoZOrder`
  are used by the lock toggle (`:152-166`).
- Structural gaps every non-trivial option must fill from zero: no foreground/process introspection,
  no input synthesis, no IPC, no shell-state query, no packaged-app or native-helper build
  infrastructure (§1.2, `Overlyric.slnx:1-5`).
