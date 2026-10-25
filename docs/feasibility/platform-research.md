# Platform research: can a lyrics overlay draw over exclusive-fullscreen games?

Research brief for the Overlyric feasibility question (`task-1`): web/API/prior-art research only. Overlyric is a
topmost, click-through, `WS_EX_NOREDIRECTIONBITMAP` + DirectComposition window (`src/Overlyric/Overlay/OverlayWindow.cs`);
its README says exclusive fullscreen (FSE) is out of reach.

**Labels:** **confirmed** = primary source (Microsoft docs/blog, vendor policy page, the project's own repo);
**reported** = credible secondary (practitioner write-up, support forum, reverse-engineering blog); **unknown** = not
established by any source found. Vendor-internal overlay mechanisms (NVIDIA, Valve, RTSS) are undocumented, so they are
flagged rather than asserted.

**One-line verdict:** nothing outside the game's own present path can appear over true FSE — not topmost, layered,
DirectComposition, a higher window band, UIAccess or a Game Bar widget — and the paths that do work (DLL injection,
vendor driver) are disqualified for a lyrics utility. Microsoft's own overlay has the same limit and says so.

---

## 1. The composition model: FSE vs fullscreen optimizations (FSO) vs borderless

| Mode | Who composes the final image | Topmost window visible over it? |
| --- | --- | --- |
| Borderless / windowed | DWM composites every top-level window ([DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model)) | Yes |
| "Fullscreen" with FSO (the usual Win10/11 case) | DWM owns the display, hands near-full control to the game when nothing else is on screen ([Demystifying FSO](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)) | Yes — the overlay makes DWM resume composition |
| True FSE (`SetFullscreenState(TRUE)`) | The game's swapchain; DWM is not in the scanout path | **No** |

**FSO is the pivot point.** FSO "takes full screen exclusive games and runs them instead in a highly optimized
borderless windowed format that takes up the entire screen… with the benefits of running in a windowed mode. These
benefits include faster PC commands such as alt-tab, multiple monitor set ups and overlays" **[confirmed]**
([DirectX blog, 2019-12-17](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)). Two more
sentences from that post carry the argument: "Fullscreen Optimizations is essentially FSE with the flexibility to go
back to DWM composition in a simple manner… other features that require the DWM, such as overlays", and "When an
overlay such as the Game Bar is present, the DWM reassumes control of the display, and a slight performance overhead is
incurred so that the overlay can be composited on top of the game" **[confirmed]**. That answers "what happens to a
topmost window when an FSO game presents?": it *is* composited, at a small latency/power cost plus leaving
independent-flip mode.

**For true FSE the mechanism is a scanout bypass, not a z-order contest.** Microsoft, on why FSE is being retired:
"when you give an application full control of your desktop, you cannot run any other processes in the background", and
overlays "are another of the key limitations of FSE" **[confirmed]** (same post). The Vista-era statement of the same
mechanism: "if some application switches to full-screen display and then DWM turns off composition… your application
should recognize this situation and disable composition-specific features" **[confirmed]** ([MSDN Magazine,
2007](https://learn.microsoft.com/en-us/archive/msdn-magazine/2007/april/aero-glass-create-special-effects-with-the-desktop-window-manager)).
DWM can no longer be switched off wholesale on Windows 10/11, but the bypass is the same idea: during FSE there is no
composed desktop surface, so there is nothing to composite a window into. A Microsoft Q&A moderator says the same for
capture: "true Exclusive Fullscreen can bypass that system… it may present frames directly to the display and bypass
the normal desktop composition flow. In that situation, there may be no composed desktop surface available"
**[reported]** ([MS Q&A, 2026](https://learn.microsoft.com/en-us/answers/questions/5791521/wgc-capturing-exclusive-fullscreen-games-apps)).

`SetFullscreenState` enters FSE and its failure list documents the ownership model: `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE`
when "another application is already in full-screen mode", when the output window "does not have keyboard focus", or
when it is occluded **[confirmed]** ([doc](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-setfullscreenstate)).
**README accuracy:** Overlyric's sentence is substantially correct; sharpen the mechanism to "the game's swapchain is
scanned out without a composition pass, so DWM has nothing to composite a window onto" rather than "Windows bypasses
DWM" as a blanket claim (DWM still runs for every other window and output).

## 2. Independent flip, MPO, and what an overlay costs over an FSO game

- Flip-model back buffers are shared with DWM, so DWM composes straight from them without a copy — that sharing is why
  FSO is cheap **[confirmed]** ([DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model)).
- DirectFlip / Independent Flip: "the DWM can go to sleep… Your application frames are sent directly to the screen,
  independently, with the same efficiency as fullscreen exclusive" **[confirmed]** ([For best performance, use DXGI
  flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model)).
- That page also states what happens on top: "If other desktop contents come on top, the DWM can either seamlessly
  transition back to composed mode, efficiently 'reverse compose' the contents on top of the application before
  flipping it, or leverage MPO to maintain the independent flip mode" **[confirmed]**. So an overlay over an FSO game
  costs a transition to composed presentation, a reverse-compose, or an MPO plane that keeps independent flip; which
  one is GPU/driver dependent **[unknown]** per system.
- MPO planes are reserved by DWM/driver for the *presenting application's* swapchain ("The DWM is able to reserve a
  dedicated hardware scanout plane for your application") — not an API a third party can use to place a window over
  another process's output **[confirmed quote; inference for the conclusion]**.
- Net: over FSO the overlay already works at the documented "slight performance overhead"; over FSE it is not a cost
  question, because there is no composition to join.

## 3. How common is *true* FSE on Windows 11 in 2024/2025?

- Windows 10 introduced FSO so games requesting FSE run as optimised borderless **[confirmed]** ([DirectX blog](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)).
- Windows 11 (22H2+) added **"Optimizations for windowed games"**, which "improves gaming performance for DirectX 10 and
  DirectX 11 games running in windowed and borderless windowed modes… transitions compatible games from the legacy
  blt-model presentation to the newer flip-model… enabling… Auto HDR and variable refresh rate". Turning on Auto HDR
  turns it on automatically **[confirmed]** ([Microsoft Support](https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11)).
- A game can still land in true FSE: the per-exe compatibility flag **"Disable fullscreen optimizations"** is the
  Microsoft-documented way to get FSE back **[confirmed]** (DirectX blog, troubleshooting), and titles presenting
  through Vulkan/OpenGL or wrapper layers may not participate in FSO. Community reports for DXVK are explicit:
  "Windows Fullscreen Optimizations seems to be disabled whenever dxvk is used" — such games go true-exclusive and
  desktop overlays/notifications stop appearing **[reported]** ([dxvk#2177](https://github.com/doitsujin/dxvk/issues/2177)).
- Microsoft's own overlay concedes the same for Vulkan/OpenGL: "In many cases Xbox Game Bar cannot be shown when a game
  uses Vulkan or OpenGL in full-screen mode… a game running in any kind of full-screen mode will potentially be
  rendered without Desktop Window Manager (DWM) involvement; this prevents Game Bar from showing over the game window"
  **[confirmed]** ([Game Bar known issues](https://learn.microsoft.com/en-us/xbox/game-bar/known-issues)).
- **Share of players actually in true FSE: unknown.** No public telemetry found. Defensible statement: FSO covers most
  "fullscreen" games on Windows 11, while true FSE persists in (a) titles whose users disable FSO for latency, (b)
  Vulkan/OpenGL/DXVK/wrapper cases, (c) older or esports titles that call `SetFullscreenState` and opt out. Working
  assumption: "some users, concentrated in exactly the games where an overlay is most wanted".
- Failures that are *not* true FSE and should be measured rather than assumed by this brief: a game re-asserting
  `HWND_TOPMOST`, mode/refresh changes that recreate the swapchain, HDR toggling, per-app optimizations off
  **[unknown]** — hypotheses for the probe, not findings.

## 4. Microsoft-sanctioned overlay paths (Game Bar widgets) and their limits

- A widget is "a Windows UWP XAML app that has a XAML view rendered into Xbox Game Bar's UI"; the app stays a separate
  process and "Game Bar can render your applications views into its own overlay space" **[confirmed]**
  ([widget overview](https://learn.microsoft.com/en-us/xbox/game-bar/overview)).
- Packaging is MSIX/UWP, not Win32: declared in `Package.appxmanifest` via a `uap3:AppExtension` named
  `microsoft.gameBarUIExtension`, with `GameBarWidget` properties (type `Standard`/`Settings`, initial/min/max size,
  `AllowForegroundTransparency`, and `PinningSupported` — which lets a widget stay "pinned on the screen, even when Game
  Bar is dismissed") **[confirmed]** ([Changes to your project](https://learn.microsoft.com/en-us/xbox/game-bar/guide/pkg-manifest)).
- Distribution: "Publishing to the Widget Store is no different from publishing other UWP apps to the Microsoft
  Store"; sideloading works but is recommended "for developing and testing, but not for wide scale distribution"
  **[confirmed]** ([Widget Store](https://learn.microsoft.com/en-us/xbox/game-bar/guide/widget-store)).
- The public `Windows.Gaming.UI.GameBar` surface is notification-only — `Visible`, `IsInputRedirected` and events, for
  games that want to pause when Game Bar is up. No overlay-rendering capability **[confirmed]**
  ([GameBar class](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.ui.gamebar)).
- **Widgets inherit Game Bar's composition dependency.** Game Bar cannot be shown when a game renders without DWM
  involvement **[confirmed]**, and a widget is rendered *into* Game Bar's overlay space, so a widget — pinned or not —
  cannot appear over true FSE either **[confirmed premises; inference for the conclusion — no document says "widgets do
  not work in FSE" in those words]**.
- User reports match: a player in Valorant's fullscreen-exclusive mode is alt-tabbed and told "games settings are not
  compatible with xbox gamebar", and resolves it by using windowed fullscreen **[reported]** ([MS Q&A](https://learn.microsoft.com/en-us/answers/questions/3800640/what-do-i-doo-to-fix-fullscreen-exclusive-not-worki)).
- **Is there any public API for a third-party overlay over FSE? No.** Nothing in the Game Bar SDK, `Windows.Gaming.UI`,
  DXGI or DWM docs exposes it; the only documented way to draw over FSE is interception, which Microsoft frames as a
  problem rather than an API (§5) **[unknown in the strict sense; "no such API found" after searching those surfaces]**.
- Cost for Overlyric: a second UWP/XAML-packaged component, Store-or-sideload distribution, a different UI stack from
  the NativeAOT Direct2D one — and still no FSE. Not worth it.

## 5. Injection overlays: how they work, what they cost, what they break

Microsoft's description of the only mechanism that works over FSE, and of why it is bad **[confirmed]** ([DirectX
blog](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)):

> "In order to create an overlay, the outside application would have to step into and intercept the rendering process.
> So, if you wanted an overlay – the frame would be rendered, then intercepted by the command that generates the
> overlay before the frame was presented, inject the overlay, then finally present the frame to the graphics card.
> This process of intercepting the render and presentation process can cause problems including performance
> regressions, instability and issues with anti-cheat."

- **Mechanism:** load a DLL into the game process and hook presentation — `IDXGISwapChain::Present`/`Present1` for
  D3D11/12, the Vulkan `vkQueuePresentKHR` path, `wglSwapBuffers`/`SwapBuffers` for OpenGL — then draw with the game's
  device (or a second swapchain) just before the real present. Variants: IAT hooking, inline/detour patching, device
  wrapping via a proxy `dxgi.dll`, or rendering from a helper sharing the swapchain. All of them are code in the game's
  address space, which is what anti-cheat treats as a threat surface **[confirmed for the mechanism; API enumeration is
  standard graphics knowledge, reported]**.
- **Evidence shipping overlays do this:** Discord tells users whose overlay fails to "try changing your game display
  settings to either windowed or borderless windowed", and its crash-log path is
  `%appdata%\discord\…\modules\discord_hook\hook` — a hook module **[confirmed]** ([Discord Game Overlay 101](https://support.discord.com/hc/en-us/articles/217659737-Game-Overlay-101)).
- **BattlEye policy (primary):** "Generally we only ever ban for the use of actual cheats/hacks or components of such
  hacks… non-cheat overlays and visual enhancement tools like Reshade or SweetFX are generally supported unless desired
  otherwise by the game developers (the latter two are currently blocked in PUBG, Fortnite and Islands of Nyne). We
  might decide to kick (not ban) you at some point for using a specific program (such as macro tools)". The same FAQ
  tells users to remove `d3d9.dll`, `dxgi.dll` or `dsound.dll` from the game directory — proxy-DLL injection is
  detected and blocked **[confirmed]** ([BattlEye FAQ](https://www.battleye.com/support/faq/)).
- **Easy Anti-Cheat, VAC, Ricochet, Denuvo Anti-Cheat:** no primary statement on overlays was retrieved in this pass; the
  widely repeated secondary claims (external windows tolerated, injected code flagged) could not be verified against a
  vendor page **[unknown]**. Treat all four as "assume injection is disallowed or risky" — and note that a
  *non*-injected overlay is not the thing they object to. That asymmetry is a product asset for Overlyric, not just an
  implementation detail.
- **Elevation/UIPI:** overlaying an elevated game means being elevated; protected-process anti-cheat blocks handle/DLL
  access outright and UIPI blocks cross-integrity messages/hooks **[reported/standard Windows behaviour, no single doc
  fetched]**.
- **Servicing:** a per-API matrix (D3D11/12, Vulkan, OpenGL, 32- and 64-bit), per-game workarounds, HDR/VRR/resolution
  edge cases, anti-cheat whitelisting requests, AV false positives, code signing, permanent maintenance
  **[reported/inference]**.
- **Realistic cost for a small .NET NativeAOT tray app:** D3D11+D3D12 only, with a working click-through lyric overlay
  and no anti-cheat drama, is multi-month work for one experienced engineer; Vulkan and OpenGL add more; maintenance
  never ends **[unknown/estimate]**. The decisive point: the mechanism that makes it possible is the mechanism
  anti-cheat exists to stop. For a *lyrics* feature the risk/benefit is indefensible.

## 6. Kernel / driver overlay path

- **A third party cannot add an overlay plane over another application's output.** MPO planes are reserved by
  DWM/driver for the app that owns the swapchain **[confirmed]** (§2); display miniports are GPU-vendor components; and
  the one Microsoft-documented third-party display model, the **Indirect Display Driver**, creates a *new virtual
  monitor* and receives "the desktop image" to process — it does not composite into an existing output. "The IDD uses a
  user-mode model and doesn't support kernel-mode components… The driver shouldn't call user-mode APIs that aren't
  appropriate for driver use, such as GDI, windowing APIs, OpenGL, or Vulkan" **[confirmed]** ([IDD overview](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview)).
- **Signing:** attestation requires "an extended validation (EV) code signing certificate", Hardware Developer Program
  registration and a Partner Center submission per package; and "attestation signed drivers that target retail
  audiences aren't published on Windows Update" **[confirmed]** ([Attestation sign Windows drivers](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation)).
  EV pricing and WHQL timelines were not verified **[unknown]**.
- **Why NVIDIA/AMD overlays exist and ours cannot:** they are the display driver vendor, so the composition/scanout
  path is theirs; a third party would need a signed display driver and still no documented plane-allocation API
  **[inference, high confidence; no vendor doc states it]**. Not a path for this project at any cost.

## 7. Window bands, UIAccess, Magnification API — what they solve, what they do not

**Window bands** (`CreateWindowInBand`, `ZBID_*`): since Windows 8, bands are separate z-order groups above
`ZBID_DESKTOP` (`ZBID_SYSTEM_TOOLS` for Task Manager's always-on-top, `ZBID_UIACCESS` highest). The best-documented
reverse-engineering write-up reports: `CreateWindowInBand` works **only** for `ZBID_DEFAULT`/`ZBID_DESKTOP`;
`ZBID_UIACCESS` additionally needs a UIAccess token; any other band fails `ERROR_ACCESS_DENIED` unless the binary has a
special `.imrsiv` PE section and a Microsoft "Windows" signature; `SetWindowBand` always fails; reaching other bands
required DLL injection into an immersive process **[reported]** ([ADeltaX, "Window z-order in Windows
10"](https://blog.adeltax.com/window-z-order-in-windows-10/), 2020–21). A public fork advertises "Overlay fullscreen
applications by duplicating winlogon.exe system token to gain uiaccess (ZBID_UIACCESS)" **[reported, unverified
claim]** ([repo](https://github.com/arcanine300/CreateWindowInBand)) — i.e. the trick is SYSTEM token theft, which is
malware behaviour, not a shippable tray-app feature, and its "defeats true FSE" claim is exactly what this brief could
not verify.

**UIAccess** is real, documented, and narrower than folklore: Authenticode-signed, installed in a secure location such
as `Program Files`, built with `uiAccess="true"`; it then "can run as the topmost application in the z-order at any
time". Microsoft also states it "should not be used… by applications that just want to appear above other applications
in the new Windows UI", and that no listed scenario gives access to system-IL UI **[confirmed]** ([UIAccess security
considerations](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview)). Note the packaging
consequence: signed + Program Files + manifest is a real distribution constraint, and it buys z-order over *composited*
windows only.

**Why neither helps:** bands and UIAccess reorder windows *within the composited desktop*; during FSE there is no
composition pass, so there is nothing to be on top of. The proof is Microsoft's own overlay: Game Bar — Microsoft-signed,
privileged, DWM-integrated — documents that it cannot show over fullscreen rendering that bypasses DWM **[confirmed]**
(§4). If a first-party overlay with every privilege cannot, a band or a manifest flag will not.

**Magnification API** is not a back door: the magnifier control "copies an area of the screen… to an off-screen bitmap"
and magnifies it, and `MagSetInputTransform` requires UIAccess **[confirmed]** ([Magnification API
overview](https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/magapi-intro)). It reads the composed desktop;
it cannot read what was never composed **[inference]**.

**Debunking "topmost window over FSE" claims.** Three explanations survive scrutiny: (a) the game was in
FSO/borderless and the reporter believed it was FSE — Microsoft's own FSO test is literally "does the Game Bar overlay
appear?" **[confirmed]**; (b) the game lost focus and minimised, leaving the overlay visible on the desktop; (c) the
report involves token theft/injection, which is neither supported nor shippable **[reported/unknown]**. No primary
source documents a plain window appearing over true FSE.

## 8. "Ejection": can we force the game out of FSE?

| Mechanism | What actually happens | Viable? |
| --- | --- | --- |
| `Win+G` (Game Bar) | Microsoft's own FSO test: "If you are running in Fullscreen Exclusive, then the display brightness may flicker… If you are in Fullscreen Optimizations, the Xbox Game Bar should pop up as an overlay" **[confirmed]** | Diagnostic only; visible disruption |
| Volume OSD / other system UI | Same test: "You can do this with other system UIs such as the volume indicator too" **[confirmed]** | Same |
| Steal focus (Alt+Tab, `SetForegroundWindow`) | FSE depends on owning output/focus; `SetFullscreenState` fails when the window "does not have keyboard focus" **[confirmed]**, so focus loss forces the game out of FSE (it usually minimises) | Reliable, but destroys the play experience |
| `ChangeDisplaySettingsEx` mode change | Changes the mode under the game's exclusive swapchain; the game must re-enter FSE or minimise; `SetFullscreenState` distinguishes `DXGI_STATUS_MODE_CHANGE_IN_PROGRESS` **[confirmed]**, per-game behaviour **[unknown]** | Not viable as a background behaviour |
| Our own FSE swapchain | Cannot take the output: fails `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE` when "another application is already in full-screen mode" **[confirmed]** | Impossible |
| `IDXGIOutput::TakeOwnership` | "Should not be called directly by applications, since results will be unpredictable… should not be used as a substitute for swap-chain methods" **[confirmed]** ([doc](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-takeownership)) | No |
| Secure desktop / UAC, OSK (UIAccess band) | Reported to force FSE games out rather than draw over them **[reported, weak]** (comment thread on [ADeltaX](https://blog.adeltax.com/window-z-order-in-windows-10/)) | Not a product feature |

**Conclusion:** every working mechanism takes focus or the display mode away — i.e. kicks the player out of the game.
Defensible only as an explicit user-initiated action (e.g. a hotkey that ejects and shows lyrics), never as passive
behaviour, and still game-dependent. The honest user-facing framing is "switch the game to borderless".

**Cheap, non-disruptive detection exists:** `SHQueryUserNotificationState` returns `QUNS_RUNNING_D3D_FULL_SCREEN` ("A
full-screen (exclusive mode) Direct3D application is running") **[confirmed]** ([QUERY_USER_NOTIFICATION_STATE](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state)) —
the right signal for "hide the overlay and explain why", plus (for an injected app, which we are not) the game's DXGI
fullscreen state.

## 9. Alternatives worth naming

- **Per-game borderless / "fullscreen windowed"**: the actual fix, and the documented overlay-compatibility advice
  **[confirmed]** ([Optimizations for windowed games](https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11), [Game Bar known issues](https://learn.microsoft.com/en-us/xbox/game-bar/known-issues)).
- **Windows compat flags**: "Disable fullscreen optimizations" *forces* true FSE — a fine diagnostic (if lyrics vanish
  only with it set, the game was using FSO), harmful as advice **[confirmed]** ([DirectX blog](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)).
- **Per-title "known FSE titles" notes** in Overlyric's docs: honest, cheap, useful.
- **An always-visible second surface**: taskbar lyrics, phone/second display, desktop widget. Lyricify ships taskbar
  lyrics and desktop lyrics alongside its in-app fullscreen view **[confirmed]** ([Lyricify README](https://github.com/WXRIW/Lyricify-App)).
- **Streaming/capture re-composite** (lyrics in the OBS overlay, not on the player's screen): works because the
  compositor is the streaming app's **[inference]**.
- **VR**: an in-headset overlay is composited by the VR runtime, not DWM, so the FSE problem does not apply there;
  irrelevant outside VR **[unknown specifics]**.
- **Borderless-forcing tools**: third-party window manipulators help some titles; general reliability and anti-cheat
  tolerance **[unknown]**.

## 10. Prior art: lyric overlays over games

- **Lyricify** (`WXRIW/Lyricify-App`, ~7.3k stars, Microsoft Store, lyrics engine open as
  `Lyricify-Lyrics-Helper`): desktop lyrics, a dynamic-lyrics island, taskbar lyrics, and an in-app **fullscreen
  lyrics** view. Its fullscreen feature is the app's own fullscreen window, not an overlay over another application's
  FSE; no public claim, changelog entry or doc of drawing over exclusive fullscreen was found **[confirmed: feature list
  in the repo README](https://github.com/WXRIW/Lyricify-App); absence of an FSE claim: negative search result,
  unknown]**.
- **BetterLyrics** (`jayfunc/BetterLyrics`, ~2.2k stars, C#, WinUI3/Win2D, GPL-3.0): a lyrics visualiser/music player
  with desktop-lyrics style windows — the same architectural class as Overlyric (a topmost app-drawn window), no
  injection in evidence **[reported — repository metadata](https://github.com/jayfunc/BetterLyrics)**.
- **The pattern:** every desktop-lyrics product lives in the DWM-composited world and inherits the limit; the ones that
  appear over "fullscreen" games do so because those games are in FSO/borderless. Nobody in the category advertises FSE
  support, which is itself evidence the problem is real and unsolved without injection **[reported/unknown]**.

## 11. Bottom line

**Cannot draw over true FSE, by any non-injected means:**

- Any HWND however styled — `WS_EX_TOPMOST`, `WS_EX_LAYERED`, `WS_EX_TRANSPARENT`, `WS_EX_NOREDIRECTIONBITMAP`,
  DirectComposition, Direct2D — because FSE presents without a composition pass (§1, §2).
- Window bands (`ZBID_*`) and UIAccess: they reorder *composited* windows; the highest band still needs a composition
  pass, and the non-desktop bands are gated behind a Microsoft-signed PE section anyway (§7).
- Magnification API overlays: they read the composed desktop (§7).
- Xbox Game Bar widgets, pinned or not: rendered into Game Bar's overlay space, and Game Bar itself documents that it
  cannot show over non-DWM fullscreen (§4).
- A second FSE swapchain or `TakeOwnership`: the output belongs to the game and the API is explicitly unsupported (§8).
- An Indirect Display Driver: it creates a virtual monitor; it does not composite over someone else's output (§6).

**Can draw over everything DWM composites — the common case:** windowed, borderless and FSO "fullscreen" (the default
for FSE-requesting games since Windows 10, further normalised by Windows 11's windowed-game optimizations). Over an FSO
game the overlay makes DWM resume composition at a documented, small cost (§1, §2). This is what Overlyric already does.

**MS-sanctioned paths and their packaging limits:** the only sanctioned third-party overlay is a Game Bar widget —
UWP/XAML inside MSIX, Store or sideload, declared through `microsoft.gameBarUIExtension`. It cannot solve FSE and cannot
be the same NativeAOT Win32 binary. No public API exists for an overlay over FSE (§4).

**Injection reality:** the only user-mode mechanism that works over FSE is hooking the game's present path, which
Microsoft itself describes as causing "performance regressions, instability and issues with anti-cheat". BattlEye bans
only for actual cheats but reserves the right to block or kick and already blocks proxy `dxgi.dll`/`d3d9.dll`; Discord's
overlay ships a hook module and still tells users to switch to borderless when it fails (§5). Multi-month build,
permanent maintenance, and a real chance of getting players kicked or banned — for lyrics.

**Driver path:** vendor-only. A third party cannot allocate an overlay plane, the documented third-party display model
(IDD) is a virtual monitor, and signing needs an EV certificate plus Partner Center attestation (§6).

**Ejection:** possible only by taking focus or the display mode away — effective, but it is "kick the player out of the
game", must be user-initiated, and is game-dependent; `Win+G`/volume-OSD behaviour in FSE is exactly this disruption
(§8).

**Cheapest credible path (recommended):** keep the no-injection design and make the limitation legible instead of
engineering around it.

1. Detect FSE with `SHQueryUserNotificationState` → `QUNS_RUNNING_D3D_FULL_SCREEN` (documented, no privileges, no hooks)
   — a few lines, no new risk.
2. When FSE is detected, hide the overlay (it cannot be seen anyway) and raise the existing tray balloon — Overlyric
   already has `Shell_NotifyIcon`/`NIF_INFO` plumbing — saying "this game is in exclusive fullscreen; switch it to
   borderless for lyrics", ideally per-title.
3. Keep investing in the DWM-composited path (FSO/borderless), where nearly all players are, and correct the README's
   mechanism sentence to "the game presents directly to the display, so DWM has nothing to composite onto".

Hours-to-days, no anti-cheat or security surface, and truthful. The only alternative that changes the answer is an
injection overlay, which this project has correctly ruled out; nothing in the platform, the Microsoft documentation or
the prior art suggests a third option exists.

### Sources (all fetched during this research)

Microsoft: [Demystifying Fullscreen Optimizations](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/) ·
[DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model) ·
[For best performance, use DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model) ·
[SetFullscreenState](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-setfullscreenstate) ·
[IDXGIOutput::TakeOwnership](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-takeownership) ·
[QUERY_USER_NOTIFICATION_STATE](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state) ·
[Optimizations for windowed games](https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11) ·
[Game Bar SDK](https://learn.microsoft.com/en-us/xbox/game-bar/) · [widget overview](https://learn.microsoft.com/en-us/xbox/game-bar/overview) ·
[widget manifest](https://learn.microsoft.com/en-us/xbox/game-bar/guide/pkg-manifest) · [widget store](https://learn.microsoft.com/en-us/xbox/game-bar/guide/widget-store) ·
[Game Bar known issues](https://learn.microsoft.com/en-us/xbox/game-bar/known-issues) · [Windows.Gaming.UI.GameBar](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.ui.gamebar) ·
[UIAccess security considerations](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview) ·
[Magnification API](https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/magapi-intro) ·
[Indirect Display Driver](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview) ·
[Attestation signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation) ·
[Aero Glass / DWM (2007)](https://learn.microsoft.com/en-us/archive/msdn-magazine/2007/april/aero-glass-create-special-effects-with-the-desktop-window-manager)
· MS Q&A: [WGC + exclusive fullscreen](https://learn.microsoft.com/en-us/answers/questions/5791521/wgc-capturing-exclusive-fullscreen-games-apps),
[Game Bar in Valorant FSE](https://learn.microsoft.com/en-us/answers/questions/3800640/what-do-i-doo-to-fix-fullscreen-exclusive-not-worki)

Vendors/practitioners: [BattlEye FAQ](https://www.battleye.com/support/faq/) · [Discord Game Overlay 101](https://support.discord.com/hc/en-us/articles/217659737-Game-Overlay-101) ·
[dxvk#2177](https://github.com/doitsujin/dxvk/issues/2177) · [ADeltaX: window z-order/bands](https://blog.adeltax.com/window-z-order-in-windows-10/) ·
[arcanine300/CreateWindowInBand](https://github.com/arcanine300/CreateWindowInBand) · [Lyricify-App](https://github.com/WXRIW/Lyricify-App) ·
[BetterLyrics](https://github.com/jayfunc/BetterLyrics)

_Not verified in this pass (flagged, not guessed): EAC / VAC / Ricochet / Denuvo written overlay policies; NVIDIA,
Valve and RTSS implementation details; EV certificate pricing and WHQL timelines; the share of players in true FSE on
Windows 11; whether any shipping product draws over FSE without injection._
