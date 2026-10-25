# Fullscreen gaming overlay for lyrics — feasibility

*Investigation only: no product code, README text or installer was changed. Evidence: a code-level review
([codebase-impact.md](feasibility/codebase-impact.md)), platform research with primary sources
([platform-research.md](feasibility/platform-research.md)), and a measurement probe
([probe/FullscreenProbe](../probe/FullscreenProbe), [probe-results.md](feasibility/probe-results.md)).*

## The short answer

Two different questions hide under "support a fullscreen gaming overlay":

1. **Can Overlyric draw lyrics over a game that says it is in fullscreen?** For the mode Windows 10/11
   actually gives most games — Fullscreen Optimizations (FSO), which runs a fullscreen request as an
   optimized borderless window — **yes, and it already does.** DWM is still the compositor there, and the
   documented behaviour is that an overlay makes DWM resume composition over the game. Measured on this
   machine in §2.
2. **Can it draw over *true* exclusive fullscreen (FSE), where the game's own swap chain is scanned out with
   no composition pass?** **No** — not with a window however styled, not with DirectComposition, a higher
   window band, UIAccess, the Magnification API, a Game Bar widget, or a second fullscreen swap chain. The
   only mechanisms that reach that scan-out path are code injected into the game (hooking its present calls)
   or a display driver. Microsoft says both in its own FSO post and in its own Game Bar documentation, and
   Microsoft's own privileged overlay is limited the same way (§3).

So "support a fullscreen gaming overlay" is not one feature with one switch. It is a supported case that can
be made solid, and an unsupported case that can only be made *legible* — or entered by becoming a different
product. The recommendation in §5 is the former.

## 1. Three different things are called "fullscreen"

| Mode | Who puts the pixels on screen | Can a normal topmost window appear over it? |
| --- | --- | --- |
| Borderless / windowed | DWM composites every top-level window | **Yes** |
| "Fullscreen" under FSO (the usual Win10/11 case) | DWM owns the display; it hands near-full control to the game while nothing else is on screen, and re-composes when an overlay appears | **Yes** |
| True exclusive fullscreen (`SetFullscreenState(TRUE)` with FSO disabled, legacy display-mode changes, some Vulkan/OpenGL paths) | The game's swap chain, direct to the display; no composition pass | **No** |

**FSO is the pivot, and Microsoft describes it in the terms this project needs.** FSO "takes full screen
exclusive games and runs them instead in a highly optimized borderless windowed format that takes up the
entire screen … with the benefits of running in a windowed mode. These benefits include faster PC commands
such as alt-tab, multiple monitor set ups and overlays"
([DirectX blog, 2019](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)).
The same post says what happens when an overlay appears: "When an overlay such as the Game Bar is present,
the DWM reassumes control of the display, and a slight performance overhead is incurred so that the overlay
can be composited on top of the game in a safe and stable way."

**True FSE is a scan-out bypass, not a z-order contest.** The same post describes why FSE is being retired:
"when you give an application full control of your desktop, you cannot run any other processes in the
background", and "Overlays … are another of the key limitations of FSE"; the historical mechanism is stated
plainly in [MSDN Magazine, 2007](https://learn.microsoft.com/en-us/archive/msdn-magazine/2007/april/aero-glass-create-special-effects-with-the-desktop-window-manager):
"if some application switches to full-screen display and then DWM turns off composition … your application
should recognize this situation and disable composition-specific features." During FSE there is no composed
desktop surface to composite a window into.

One nuance matters for expectations: even an ordinary *windowed* flip-model game can bypass DWM through
DirectFlip when nothing else is on screen — "it is possible to bypass desktop composition entirely and
directly send application frames to the screen, in the same way that exclusive fullscreen does"
([flip-model guidance](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model)).
The difference is what happens next: in the flip-model windowed case, "if other desktop contents come on
top, the DWM can either seamlessly transition back to composed mode, efficiently 'reverse compose' the
contents on top of the application, or leverage MPO to maintain the independent flip mode" — so the overlay
appears. In true FSE there is no such transition to make.

**How common is true FSE?** Windows 10 introduced FSO for exactly this; Windows 11 22H2 added
["Optimizations for windowed games"](https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11)
(flip-model for DX10/11 borderless). FSO is the default for most titles. It is not universal: the per-exe
"Disable fullscreen optimizations" compatibility setting is Microsoft's documented way to get FSE back, and
some presentation paths (Vulkan/OpenGL and wrapper layers) do not participate — e.g. DXVK users report that
FSO stops applying and "drawing overlays as well as toast notifications are hindered"
([dxvk#2177](https://github.com/doitsujin/dxvk/issues/2177)). No public telemetry on the share of players in
true FSE was found; treat it as "a minority, concentrated in the games where an overlay is most wanted".

## 2. Measured here

<!-- PROBE-SECTION -->

## 3. Why the existing overlay cannot cover true FSE

The overlay's pixels exist only as a DirectComposition visual on a window with no redirection bitmap:
`CompositionHost` creates a composition swap chain and binds it to the HWND's DComp target
(`src/Overlyric/Overlay/CompositionHost.cs:97-102`), and `OverlayWindow` is a popup tool window with
`WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TRANSPARENT |
WS_EX_LAYERED` (`src/Overlyric/Overlay/OverlayWindow.cs:57-60`). That is the leanest way to show
per-pixel-transparent text over the composed desktop, and it is exactly what does not exist during FSE:
there is no desktop composition pass in the scan-out path. No style, DComp target, hit-test rule
(`OverlayWindow.cs:271-296`) or placement choice changes that.

This is not a hole in Overlyric's implementation; it is the platform's boundary, and Microsoft's own
privileged overlay documents the same limit:

- **Game Bar:** "In many cases Xbox Game Bar cannot be shown when a game uses Vulkan or OpenGL in
  full-screen mode. … Depending on video card driver implementation a game running in any kind of
  full-screen mode will potentially be rendered without Desktop Window Manager (DWM) involvement; this
  prevents Game Bar from showing over the game window"
  ([Game Bar known issues](https://learn.microsoft.com/en-us/xbox/game-bar/known-issues)).
- **Xbox Support:** "For PC games in full-screen (or full-screen exclusive) mode, you won't see Game Bar"
  ([troubleshooting](https://support.xbox.com/en-US/help/games-apps/game-setup-and-play/troubleshoot-game-bar-windows)).
- **Game Bar widgets inherit it:** a widget is "a Windows UWP XAML app that has a XAML view rendered into Xbox
  Game Bar's UI" ([overview](https://learn.microsoft.com/en-us/xbox/game-bar/overview)), so it is composited
  by Game Bar, which is composited by DWM. Widget packaging is MSIX-only (`microsoft.gameBarUIExtension` in
  `Package.appxmanifest`, [manifest guide](https://learn.microsoft.com/en-us/xbox/game-bar/guide/pkg-manifest)),
  with Store or sideload distribution — a second product with a different delivery model, and still not an
  FSE solution.
- **Fresh evidence that even capture is bound to composition:** Microsoft's answer on Windows Graphics
  Capture says it "operates within the Desktop Window Manager (DWM) composition pipeline" and that during
  exclusive fullscreen "there may be no composed desktop surface available"
  ([MS Q&A](https://learn.microsoft.com/en-us/answers/questions/5791521/wgc-capturing-exclusive-fullscreen-games-apps)).

The tempting escape hatches do not reach past it either:

- **Window bands** (undocumented `CreateWindowInBand`/`ZBID_*`) and **UIAccess** reorder *composited* windows.
  UIAccess additionally requires an Authenticode signature, installation in a secure location such as
  `Program Files`, and `uiAccess="true"` in the manifest; Microsoft says it "should not be used … by
  applications that just want to appear above other applications", and even then it gives no access to
  system-IL UI ([security considerations](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview)).
  That contradicts the project's unsigned, per-user, no-admin install for a benefit that stops at the
  composition boundary.
- **The Magnification API** reads the composed desktop and magnifies it
  ([overview](https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/magapi-intro)); it cannot read
  what was never composed.
- **A second fullscreen swap chain or `IDXGIOutput::TakeOwnership`** cannot take the output from the game:
  `SetFullscreenState` returns `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE` when "another application is already in
  full-screen mode", and `TakeOwnership` "should not be called directly by applications, since results will
  be unpredictable"
  ([SetFullscreenState](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-setfullscreenstate),
  [TakeOwnership](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-takeownership)).

The README's existing sentence (`README.md:313-318`) is therefore correct in substance. It could be
sharpened from "Windows bypasses the desktop compositor there" to the more precise "the game presents
straight to the display, so there is no composition pass for a window to be drawn into" — DWM is still
running, it just is not in that output's path. That is a documentation nicety, not a behaviour change.

## 4. The options, honestly costed

| Option | Covers true FSE? | Effort | Risk to users | Fits this project | Verdict |
| --- | --- | --- | --- | --- | --- |
| (a) Keep the limitation, sharpen the wording | No | minutes, docs only | none | Yes | Baseline; do it with (b) |
| (b) Detect FSE and tell the user | No | ~150–250 LOC + a small probe | none; heuristic false positives | Yes: no new deps, no elevation | **Recommended now** |
| (c) Make the supported case solid: foreground-triggered topmost re-assert; optional monitor-sized canvas | No (FSO only) | ~40–70 LOC for the re-assert; ~150–250 LOC for the canvas | Low; a screen-sized topmost window can cost frame pacing | Yes, opt-in where it costs | **Recommended** |
| (d) Xbox Game Bar widget | No | 1,500–3,000 LOC new + 500–900 LOC refactor; MSIX + certificate | Low, but packaging/trust burden | No: breaks unsigned per-user MSI; second product | Separate product, not this task |
| (e) In-process injection (hook `Present`/`vkQueuePresentKHR`) | **Yes** | 2,000–5,000 C++ + 200–400 C# IPC + signing + installer + per-title matrix; 6–12 person-months | **High**: anti-cheat kicks/bans, crashes | No: breaks "no injection", unsigned, no-admin, per-user, <100 MB | Reject unless the product is re-scoped |
| (f) Display/kernel driver overlay | Yes (in principle) | 10k+ kernel LOC, EV certificate, attestation/WHQL, per-machine install + service | Very high; anti-cheat treats drivers as cheat vectors | No: breaks everything the installer promises | Out of scope |
| (g) Force the game out of FSE | It becomes FSO, not a true overlay | guidance only; automation is focus/mode theft | Disrupts play; game-dependent | Guidance fits, automation does not | Guidance only |

### (a) Document the limitation

Already in `README.md:313-318` and in HEAD. The only change worth making is the mechanism sentence in §3
plus one line pointing at the new detection behaviour. Zero code, zero risk.

### (b) Detect true FSE and say so

`SHQueryUserNotificationState` returns `QUNS_RUNNING_D3D_FULL_SCREEN` (`3`) — "A full-screen (exclusive
mode) Direct3D application is running"
([enum](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state)).
It needs no privileges and no hooks, and `shell32` is one `[LibraryImport]` away. The probe measured what it
actually returns in each state (§2); if it also fires for FSO games, the foreground-window/monitor-rect check
has to qualify it.

The house pattern to copy is `Media/PlayerWarningWatch.cs` (2 s poll gate, 10 s sustain, announce once per
situation, `Reset`); the insertion point is `AppHost.OnTick` beside `UpdatePlayerWarning`
(`src/Overlyric/App/AppHost.cs:379`). The trap is the channel: `OverlayNotice`/`InfoPopup` are drawn *inside
the overlay* (`src/Overlyric/Overlay/OverlayNotice.cs:13-21`), i.e. invisible in exactly the case being
reported. Use the tray instead — `TrayIcon.Alert` (`src/Overlyric/App/TrayIcon.cs:85-106`), the tooltip
(`AppHost.cs:936-961`) and a durable row in the tray menu's warning block (`AppHost.cs:879-900`). A balloon
can itself be suppressed while a fullscreen app runs (`QUNS_BUSY`), so the tooltip/menu are the durable
surfaces and the balloon should be deferred until the game leaves fullscreen. Multi-monitor nuance: if the
FSE game is on a different monitor, the overlay *is* visible, so the notice should be per-monitor
(`MonitorFromWindow` already exists, `src/Overlyric/Interop/NativeMethods.cs:352`).

Cost: ~150–250 LOC, a `Fullscreen notice` toggle in Settings following `WarnWhenPlayerDoesNotPublish`, and a
60–100 LOC probe pinning the state machine. No new artifacts, no signing, no elevation.

### (c) Make the supported case solid

Two cheap wins on the side of the boundary Overlyric can actually occupy:

- **Topmost re-assert is tick-counted, not time-based.** `TopmostReassertTicks = 20`
  (`AppHost.cs:27-28`) is checked on each visible tick (`AppHost.cs:919-925`), so it fires every ~0.66 s on
  the karaoke path, ~5 s while playing, and **every 20 s on the 1 s idle path**. A game that raises itself
  topmost can therefore cover the overlay for up to 20 s while it is idle-but-visible — a real bug in the
  supported case. Re-assert on foreground change (`EVENT_SYSTEM_FOREGROUND`) or by comparing
  `GetForegroundWindow` on the slow tick: ~40–70 LOC, no dependencies, helps every borderless/FSO game.
- **A monitor-sized transparent canvas as an opt-in layout.** Today the overlay is a fixed pill placed in
  the monitor's work area (`OverlayWindow.ApplyBounds`, `OverlayWindow.cs:362-394`). A full-monitor window
  with the same content anchored inside would make the overlay independent of a game's idea of its own
  bounds. The trade-off must be measured, not assumed: a screen-covering layered topmost window can defeat
  MPO/independent flip and cost frame pacing in the games it targets, so it belongs behind a setting and a
  probe, never as the default.

Neither closes FSE; both make the part that does work trustworthy.

### (d) Xbox Game Bar widget

Technically the only non-injection overlay Microsoft sanctions: a UWP/XAML app packaged as MSIX, declared
with `microsoft.gameBarUIExtension` and `PinningSupported` (which lets a widget stay on screen while the
game runs). But it is a panel in Game Bar's overlay space, not a click-through lyric line; it inherits Game
Bar's DWM dependency and therefore cannot appear over true FSE (§3); and packaging, a certificate and a
second UI stack break the per-user unsigned MSI promise. The lyrical engine could be extracted (the
`Lyrics\` and most of `Media\` code is portable), but the result is a second product.

### (e) In-process injection — the only real FSE fix

Mechanically it is what every overlay that survives true FSE does: load a DLL into the game and hook the
presentation path — `IDXGISwapChain::Present`/`Present1` for D3D11/12, `vkQueuePresentKHR` for Vulkan,
`wglSwapBuffers`/`SwapBuffers` for OpenGL — then draw with the game's device immediately before the real
present. Microsoft describes the category and its cost in one paragraph: "the outside application would have
to step into and intercept the rendering process … This process of intercepting the render and presentation
process can cause problems including performance regressions, instability and issues with anti-cheat"
([FSO post](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)).

For this codebase that means, at minimum: a new x64 C++ DLL (D3D11 and D3D12 vtable hooks, Vulkan layer or
`vulkan-1.dll` hook, OpenGL), a frame/IPC contract to the existing renderer or a second renderer in C++,
injection mechanics (`CreateRemoteThread`+`LoadLibrary`, CBT hook, or a launcher shim), elevation handling
for elevated games, code signing (nothing is signed today), an installer overhaul (the payload assertion in
`installer/tests/verify-installer.ps1` is a 2,429-line contract), and a per-title × per-API × per-anti-cheat
maintenance matrix that never converges. Realistically 6–12 person-months before it is credible, with no
guarantee for the competitive titles.

The user-facing risk is the reason to refuse it, not the engineering: BattlEye's policy says non-cheat
overlays are "generally supported unless desired otherwise by the game developers" but that it may block or
kick, and it instructs users to remove `dxgi.dll`/`d3d9.dll` from game directories — proxy-DLL injection is
exactly what its detection is built to catch ([FAQ](https://www.battleye.com/support/faq/)). The overlays
users know work over FSE are either vendor-whitelisted (Steam) or driver-level (NVIDIA/AMD); an unsigned
third-party module inside a protected game is the shape anti-cheat exists to stop. For lyrics, that is not
a defensible trade.

### (f) Driver overlay

A third party cannot allocate a scan-out plane over another application's output; MPO planes are DWM/driver
owned. The one documented third-party display model, the Indirect Display Driver, creates a *new virtual
monitor* and never composites into an existing output
([IDD overview](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview)).
Signing needs an EV certificate and Partner Center attestation
([attestation](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation)),
plus per-machine install and a service. NVIDIA/AMD can do what they do because they ship the display driver.
Not a path for this project at any cost.

### (g) "Ejection" — make the game leave FSE

Everything that reliably works here works by taking focus or the display mode away, i.e. by kicking the
player out of the game: Alt+Tab/focus theft, `ChangeDisplaySettingsEx`, or the OS doing it for a system UI.
Microsoft's own FSO diagnostic is exactly this: pressing Win+G in true FSE makes the display "flicker" rather
than showing Game Bar, while in FSO "the Xbox Game Bar should pop up as an overlay". It is defensible only
as an explicit user action (e.g. a hotkey that ejects and shows lyrics), never as the behaviour of a passive
overlay, and it is game-dependent.

## 5. Recommendation

Do not build an FSE overlay. Build the two things that are honest and cheap, and keep the limitation:

1. **Detect true FSE and say so (option b).** A `Media/FullscreenWatch` modelled on `PlayerWarningWatch`,
   announced through the tray (balloon deferred while fullscreen, durable tooltip/menu row), with the game's
   name and the one remedy that exists: switch the game to Borderless / Windowed Fullscreen. Add the
   Settings toggle. ~150–250 LOC, no new artifacts, no new risk.
2. **Fix the supported case (option c, first half).** Trigger the topmost re-assert on foreground change
   instead of a tick counter, so a borderless/FSO game that raises itself cannot hide the overlay for up to
   20 s. ~40–70 LOC, and it benefits every game already covered.
3. **Sharpen the README sentence** to the mechanism in §3, and point at the new detection behaviour. One
   paragraph.
4. **Leave the monitor-sized canvas (option c, second half) as an opt-in experiment** measured with the
   probe harness, because its frame-pacing cost is real and per-system.

Do not add injection, do not add a Game Bar widget, do not add a driver. None of them is justified by
"lyrics over a game", and each breaks at least one promise the project currently keeps.

## 6. What would change the verdict

An injection path should be reconsidered only if all of the following exist, not a subset:

1. Probe data naming real, currently-played titles that land in true FSE (not merely "some games"), and
   confirming FSO is not already making them composited.
2. A written anti-cheat vendor allowlist or review path for an unsigned third-party module — vendor
   statements, not forum folklore.
3. Proof that a **non-elevated** helper can inject into the titles that matter, since elevated games are
   common and "run Overlyric as administrator" breaks the install model.
4. A signed-artifact budget (OV/EV certificate, SmartScreen reputation) and an installer contract that still
   passes `verify-installer.ps1`.
5. Evidence of demand for true FSE specifically, with a per-title maintenance estimate attached.

Missing any one of those, the answer stays: not possible within this product's design.

## Sources

Primary: [Demystifying Fullscreen Optimizations](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/) ·
[DXGI flip model guidance](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model) ·
[SetFullscreenState](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-setfullscreenstate) ·
[IDXGIOutput::TakeOwnership](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-takeownership) ·
[QUERY_USER_NOTIFICATION_STATE](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state) ·
[Optimizations for windowed games](https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11) ·
[Game Bar known issues](https://learn.microsoft.com/en-us/xbox/game-bar/known-issues) ·
[Game Bar widget overview](https://learn.microsoft.com/en-us/xbox/game-bar/overview) ·
[widget manifest](https://learn.microsoft.com/en-us/xbox/game-bar/guide/pkg-manifest) ·
[UIAccess security considerations](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview) ·
[Magnification API](https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/magapi-intro) ·
[Indirect Display Driver](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview) ·
[Attestation signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation) ·
[MSDN Magazine 2007: DWM composition and full screen](https://learn.microsoft.com/en-us/archive/msdn-magazine/2007/april/aero-glass-create-special-effects-with-the-desktop-window-manager) ·
[WGC and exclusive fullscreen (Q&A)](https://learn.microsoft.com/en-us/answers/questions/5791521/wgc-capturing-exclusive-fullscreen-games-apps)

Vendor/practitioner: [BattlEye FAQ](https://www.battleye.com/support/faq/) ·
[dxvk#2177 (FSO disabled under DXVK)](https://github.com/doitsujin/dxvk/issues/2177) ·
[Xbox Support: Game Bar in fullscreen](https://support.xbox.com/en-US/help/games-apps/game-setup-and-play/troubleshoot-game-bar-windows) ·
[ADeltaX: window z-order in Windows 10](https://blog.adeltax.com/window-z-order-in-windows-10/) ·
[Discord: Game Overlay 101](https://support.discord.com/hc/en-us/articles/217659737-Game-Overlay-101) ·
[Lyricify](https://github.com/WXRIW/Lyricify-App) · [BetterLyrics](https://github.com/jayfunc/BetterLyrics)
