using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Vortice.Mathematics;
using static FullscreenProbe.NativeMethods;

namespace FullscreenProbe;

internal static class Display
{
    public static DEVMODEW Capture()
    {
        var mode = new DEVMODEW
        {
            dmDeviceName = "",
            dmFormName = "",
            dmSize = (ushort)Marshal.SizeOf<DEVMODEW>()
        };

        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref mode))
        {
            Console.WriteLine($"    (EnumDisplaySettingsW failed, error {Marshal.GetLastWin32Error()})");
        }

        return mode;
    }

    /// <summary>Put the desktop back if a fullscreen test changed the mode. Returns what happened.</summary>
    public static string Restore(DEVMODEW original)
    {
        var current = Capture();
        if (current.dmPelsWidth == original.dmPelsWidth &&
            current.dmPelsHeight == original.dmPelsHeight &&
            current.dmDisplayFrequency == original.dmDisplayFrequency)
        {
            return $"mode unchanged ({current.Display})";
        }

        var copy = original;
        var result = ChangeDisplaySettingsExW(null, ref copy, IntPtr.Zero, 0, IntPtr.Zero);
        var after = Capture();
        return $"mode {current.Display} -> restore hr={result} -> {after.Display}";
    }
}

internal static class Program
{
    private static readonly Color4 GameBlue = new(0.0f, 0.0f, 1.0f, 1.0f);
    private static readonly Color4 FseGreen = new(0.0f, 1.0f, 0.0f, 1.0f);
    private static readonly Color4 OverlayMagenta = new(1.0f, 0.0f, 1.0f, 1.0f);

    private const int OverlayWidth = 400;
    private const int OverlayHeight = 160;

    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string FsoDisabledFlag = "~ DISABLEDXMAXIMIZEDWINDOWEDMODE";

    private static int _failures;

    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));

        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        Console.WriteLine($"FullscreenProbe mode={mode} at {DateTime.Now:HH:mm:ss}");
        Console.WriteLine($"  process={Environment.ProcessId} session={Process.GetCurrentProcess().SessionId} " +
                          $"os={Environment.OSVersion.VersionString} exe={Environment.ProcessPath}");

        try
        {
            switch (mode)
            {
                case "env":
                    ShowEnvironment();
                    break;
                case "baseline":
                    ExperimentBaseline();
                    break;
                case "fso":
                    ExperimentFso();
                    break;
                case "fse":
                    ExperimentFse(flipModel: true, width: 0, height: 0, allowModeSwitch: false, label: "FSE requested (flip model, native buffers)");
                    break;
                case "fse-blt":
                    ExperimentFse(flipModel: false, width: 0, height: 0, allowModeSwitch: false, label: "FSE requested (blt model, native buffers)");
                    break;
                case "fse-mode":
                    ExperimentFse(flipModel: false, width: 1280, height: 720, allowModeSwitch: true, label: "FSE requested (blt model, 1280x720 + AllowModeSwitch)");
                    break;
                case "fse-true":
                    ExperimentTrueFse();
                    break;
                case "fse-child":
                    try
                    {
                        ExperimentFse(flipModel: true, width: 0, height: 0, allowModeSwitch: false,
                            label: "TRUE FSE candidate: FSO disabled for this exe, flip model, native buffers");
                    }
                    finally
                    {
                        ClearOwnFsoFlag();
                    }

                    break;
                case "fullscreen-overlay":
                    ExperimentFullscreenOverlay();
                    break;
                case "detect":
                    ShowEnvironment();
                    Detect.Snapshot("current desktop", IntPtr.Zero, null);
                    break;
                case "real":
                    ExperimentReal();
                    break;
                case "all":
                    ShowEnvironment();
                    ExperimentBaseline();
                    ExperimentFso();
                    ExperimentFse(flipModel: true, width: 0, height: 0, allowModeSwitch: false, label: "FSE requested (flip model, native buffers)");
                    ExperimentFse(flipModel: false, width: 0, height: 0, allowModeSwitch: false, label: "FSE requested (blt model, native buffers)");
                    ExperimentTrueFse();
                    ExperimentFullscreenOverlay();
                    ExperimentReal();
                    break;
                default:
                    Console.WriteLine($"Unknown mode '{mode}'. Modes: env baseline fso fse fse-blt fse-mode fse-true fullscreen-overlay detect real all");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CRASH: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            _failures++;
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "PROBE COMPLETED (no harness failures)" : $"{_failures} HARNESS FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    // ----------------------------------------------------------------------------------------
    // Shared helpers
    // ----------------------------------------------------------------------------------------

    private static void Section(string text)
    {
        Console.WriteLine();
        Console.WriteLine($"== {text} ==");
    }

    private static void Assert(string what, bool ok, string detail = "")
    {
        if (ok)
        {
            Console.WriteLine($"PASS | {what}{(detail.Length > 0 ? "   (" + detail + ")" : "")}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL | {what}{(detail.Length > 0 ? "   (got: " + detail + ")" : "")}");
    }

    private static void Observe(string text) => Console.WriteLine($"OBSERVE | {text}");

    private static RECT PrimaryMonitor()
    {
        var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        return info.rcMonitor;
    }

    private static (RECT Monitor, int Width, int Height) Desktop(out DEVMODEW mode)
    {
        mode = Display.Capture();
        var monitor = PrimaryMonitor();
        return (monitor, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }

    private static void ShowEnvironment()
    {
        Section("environment");
        var (monitor, width, height) = Desktop(out var mode);
        Console.WriteLine($"  desktop {width}x{height} @ {mode.Display}, monitor {monitor}");
        Console.WriteLine($"  DWM composition: hr=0x{DwmIsCompositionEnabled(out var dwm):X8} enabled={dwm}");

        var notifyHr = SHQueryUserNotificationState(out var notify);
        Console.WriteLine($"  notification state: hr=0x{notifyHr:X8} value={notify} ({Detect.NotificationName(notify)})");

        try
        {
            using var capture = DesktopCapture.Create();
            Console.WriteLine($"  adapter='{capture.AdapterName}' output='{capture.OutputName}'");
            Console.WriteLine($"  duplication: {capture.DuplicationDescription}");
            var error = "";
            var frame = Capture(capture, out error);
            if (frame is null)
            {
                Observe($"duplication created but first capture failed: {error}");
            }
            else
            {
                Console.WriteLine($"  first frame {frame.Width}x{frame.Height} format={frame.Format} accumulated={frame.AccumulatedFrames}");
                Console.WriteLine($"    tones(step=8): {frame.Describe()}");
            }
        }
        catch (Exception ex)
        {
            Observe($"duplication unavailable: {Detect.Describe(ex)}");
        }
    }

    private static CapturedFrame? Capture(DesktopCapture capture, out string error)
    {
        error = "";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var log = new Dictionary<string, string>();
            var frame = capture.TryCapture(1200, log);
            if (frame is not null)
            {
                return frame;
            }

            error = log.GetValueOrDefault("capture-error", "unknown");
            Thread.Sleep(250);
        }

        return null;
    }

    private static CapturedFrame? CaptureFresh(out string error)
    {
        error = "";
        try
        {
            using var capture = DesktopCapture.Create();
            return Capture(capture, out error);
        }
        catch (Exception ex)
        {
            error = Detect.Describe(ex);
            return null;
        }
    }

    private static void PrintFrame(string label, CapturedFrame frame)
    {
        Console.WriteLine($"  {label}: {frame.Width}x{frame.Height} format={frame.Format} accumulatedFrames={frame.AccumulatedFrames}");
        Console.WriteLine($"    tones(step=8): {frame.Describe()}");

        var (gameB, gameG, gameR, gameA) = frame.Pixel(Math.Min(frame.Width - 1, 200), Math.Min(frame.Height - 1, 200));
        Console.WriteLine($"    pixel(200,200) BGRA=({gameB},{gameG},{gameR},{gameA}) tone={frame.ToneAt(200, 200)}");
    }

    private static void ReportOverlayRegion(CapturedFrame frame, int x, int y, int w, int h, string label, bool expectVisible)
    {
        var samples = frame.SamplesInRect(x, y, x + w, y + h, 4);
        var magenta = frame.CountInRect(x, y, x + w, y + h, Tone.Magenta, 4);
        var ratio = samples == 0 ? 0 : (double)magenta / samples;
        var centerX = Math.Min(frame.Width - 1, x + (w / 2));
        var centerY = Math.Min(frame.Height - 1, y + (h / 2));
        var (b, g, r, a) = frame.Pixel(centerX, centerY);

        Console.WriteLine($"  {label}: overlay-region magenta={magenta}/{samples} ({ratio:P1}), " +
                          $"pixel({centerX},{centerY}) BGRA=({b},{g},{r},{a}) tone={frame.ToneAt(centerX, centerY)}");
        Assert(
            expectVisible
                ? $"{label}: overlay is present in the composed desktop"
                : $"{label}: overlay is absent from the composed desktop",
            expectVisible ? ratio > 0.9 : ratio < 0.02,
            $"overlay ratio={ratio:P1}");
    }

    // ----------------------------------------------------------------------------------------
    // exp1 - baseline: borderless (windowed) flip-model game + small topmost DComp overlay
    // ----------------------------------------------------------------------------------------

    private static void ExperimentBaseline()
    {
        Section("exp1 baseline: borderless flip-model game (windowed) + small topmost DComp overlay");
        var (monitor, width, height) = Desktop(out var mode);
        Console.WriteLine($"  desktop {width}x{height} {mode.Display}, monitor {monitor}");

        var overlayX = monitor.Left + ((monitor.Width - OverlayWidth) / 2);
        var overlayY = monitor.Bottom - OverlayHeight - 120;

        try
        {
            using var game = GameWindow.Create("GameBorderless", width, height, flipModel: true, topmost: false);
            ProbeWindow.PumpWithFrames(400, () => game.Render(GameBlue));

            using var overlay = OverlaySurface.Create(
                "OverlaySmall",
                overlayX, overlayY, OverlayWidth, OverlayHeight,
                overlayX, overlayY, OverlayWidth, OverlayHeight,
                clickThrough: true);

            overlay.Render(OverlayMagenta);
            ProbeWindow.PumpWithFrames(600, () => game.Render(GameBlue));

            using var capture = DesktopCapture.Create();
            Console.WriteLine($"  duplication: {capture.DuplicationDescription}");

            var error = "";
            var frame = Capture(capture, out error);
            if (frame is null)
            {
                Assert("exp1: desktop duplication captures the composed desktop", false, error);
                return;
            }

            PrintFrame("frame", frame);

            var gameSamples = frame.SamplesInRect(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom, 32);
            var blue = frame.CountInRect(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom, Tone.Blue, 32);
            Console.WriteLine($"  game-region blue={blue}/{gameSamples} ({(gameSamples == 0 ? 0 : (double)blue / gameSamples):P1})");

            ReportOverlayRegion(frame, overlayX, overlayY, OverlayWidth, OverlayHeight, "borderless flip", expectVisible: true);
            Detect.Snapshot("borderless flip-model window, overlay topmost", overlay.Hwnd, capture);
        }
        finally
        {
            Console.WriteLine($"  restore: {Display.Restore(mode)}");
        }
    }

    // ----------------------------------------------------------------------------------------
    // exp2 - FSO-style: fullscreen window that is also HWND_TOPMOST, overlay re-asserts topmost
    // ----------------------------------------------------------------------------------------

    private static void ExperimentFso()
    {
        Section("exp2 FSO-style: topmost borderless flip-model fullscreen window + topmost overlay");
        var (monitor, width, height) = Desktop(out var mode);
        Console.WriteLine($"  desktop {width}x{height} {mode.Display}, monitor {monitor}");

        var overlayX = monitor.Left + ((monitor.Width - OverlayWidth) / 2);
        var overlayY = monitor.Bottom - OverlayHeight - 120;

        try
        {
            using var game = GameWindow.Create("GameFso", width, height, flipModel: true, topmost: true);
            ProbeWindow.PumpWithFrames(400, () => game.Render(GameBlue));

            using var overlay = OverlaySurface.Create(
                "OverlaySmall",
                overlayX, overlayY, OverlayWidth, OverlayHeight,
                overlayX, overlayY, OverlayWidth, OverlayHeight,
                clickThrough: true);

            overlay.Render(OverlayMagenta);
            ProbeWindow.PumpWithFrames(600, () => game.Render(GameBlue));

            using var capture = DesktopCapture.Create();
            var error = "";
            var frame = Capture(capture, out error);
            if (frame is null)
            {
                Assert("exp2: desktop duplication captures the composed desktop", false, error);
                return;
            }

            PrintFrame("frame", frame);
            ReportOverlayRegion(frame, overlayX, overlayY, OverlayWidth, OverlayHeight, "FSO-style", expectVisible: true);
            Detect.Snapshot("topmost fullscreen window (FSO-style), overlay topmost", overlay.Hwnd, capture);

            // Topmost-vs-topmost race: the game and the overlay both claim HWND_TOPMOST.
            Section("exp2b topmost-vs-topmost z-order race");
            var rounds = 6;
            var overlayWins = 0;
            for (var round = 1; round <= rounds; round++)
            {
                // The game re-asserts topmost first, then the overlay does, then we look.
                SetWindowPos(game.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                overlay.ReassertTopmost();
                ProbeWindow.PumpWithFrames(200, () => game.Render(GameBlue));

                var raceFrame = Capture(capture, out var raceError);
                if (raceFrame is null)
                {
                    Console.WriteLine($"  round {round}: capture failed {raceError}");
                    continue;
                }

                var samples = raceFrame.SamplesInRect(overlayX, overlayY, overlayX + OverlayWidth, overlayY + OverlayHeight, 4);
                var magenta = raceFrame.CountInRect(overlayX, overlayY, overlayX + OverlayWidth, overlayY + OverlayHeight, Tone.Magenta, 4);
                var ratio = samples == 0 ? 0 : (double)magenta / samples;
                if (ratio > 0.9)
                {
                    overlayWins++;
                }

                Console.WriteLine($"  round {round}: game re-asserted topmost then overlay re-asserted -> overlay visible ratio={ratio:P1}");
                Thread.Sleep(120);
            }

            Assert("exp2b: overlay re-assert after the game wins the topmost race", overlayWins == rounds,
                $"overlay won {overlayWins}/{rounds} rounds");
        }
        finally
        {
            Console.WriteLine($"  restore: {Display.Restore(mode)}");
        }
    }

    // ----------------------------------------------------------------------------------------
    // exp3 - legacy FSE via SetFullscreenState(TRUE), with explicit state classification
    // ----------------------------------------------------------------------------------------

    private static void ExperimentFse(bool flipModel, int width, int height, bool allowModeSwitch, string label)
    {
        var model = flipModel ? "flip-model" : "blt-model";
        Section($"exp3 {label} [{model}]");

        var (monitor, desktopWidth, desktopHeight) = Desktop(out var originalMode);
        var swapWidth = width == 0 ? desktopWidth : width;
        var swapHeight = height == 0 ? desktopHeight : height;
        Console.WriteLine($"  desktop {desktopWidth}x{desktopHeight} {originalMode.Display}, buffer {swapWidth}x{swapHeight}");
        Console.WriteLine($"  pre-FSE notification state: {QueryNotification()}");

        var overlayX = monitor.Left + ((monitor.Width - OverlayWidth) / 2);
        var overlayY = monitor.Bottom - OverlayHeight - 120;

        GameWindow? game = null;
        OverlaySurface? overlay = null;
        DesktopCapture? capture = null;
        try
        {
            game = GameWindow.Create("GameFse", swapWidth, swapHeight, flipModel, topmost: true, allowModeSwitch);
            ProbeWindow.PumpWithFrames(400, () => game.Render(GameBlue));

            overlay = OverlaySurface.Create(
                "OverlaySmall",
                overlayX, overlayY, OverlayWidth, OverlayHeight,
                overlayX, overlayY, OverlayWidth, OverlayHeight,
                clickThrough: true);
            overlay.Render(OverlayMagenta);
            ProbeWindow.PumpWithFrames(500, () => game.Render(GameBlue));

            capture = DesktopCapture.Create();
            var preFrame = Capture(capture, out var preError);
            if (preFrame is null)
            {
                Assert("exp3: windowed capture before FSE", false, preError);
                return;
            }

            PrintFrame("windowed frame", preFrame);
            ReportOverlayRegion(preFrame, overlayX, overlayY, OverlayWidth, OverlayHeight, "windowed", expectVisible: true);
            Detect.Snapshot("before SetFullscreenState(TRUE)", overlay.Hwnd, capture);

            // ---- enter legacy fullscreen ----
            var enter = game.EnterFullscreen();
            Console.WriteLine($"  SetFullscreenState(TRUE) hr=0x{enter.Code:X8} success={enter.Success}");
            Console.WriteLine($"  GetFullscreenState: {game.FullscreenState()}");
            var modeDuringFse = Display.Capture();
            Console.WriteLine($"  display mode now: {modeDuringFse.Display} (was {originalMode.Display})");

            ProbeWindow.PumpWithFrames(700, () => game.Render(FseGreen));

            // Repaint the overlay too: if composition is alive the new pixels must reach the screen.
            overlay.Render(OverlayMagenta);
            ProbeWindow.PumpWithFrames(500, () => game.Render(FseGreen));

            Console.WriteLine($"  during-FSE notification state: {QueryNotification()}");

            // (a) the duplication created before FSE: does it survive the composition change?
            var preExistingError = "";
            var preExistingFrame = Capture(capture, out preExistingError);
            if (preExistingFrame is null)
            {
                Observe($"pre-existing duplication during FSE: FAILED {preExistingError}");
            }
            else
            {
                PrintFrame("pre-existing duplication frame", preExistingFrame);
            }

            capture.Dispose();
            capture = null;

            // (b) a brand-new duplication created while FSE is active
            var freshError = "";
            var freshFrame = CaptureFresh(out freshError);
            var compositionState = "unknown";
            if (freshFrame is null)
            {
                Observe($"fresh duplication during FSE: FAILED {freshError}");
                compositionState = "Duplication refused while FSE is active => DWM composition/scanout bypassed";
            }
            else
            {
                PrintFrame("fresh duplication frame", freshFrame);

                var samples = freshFrame.SamplesInRect(0, 0, freshFrame.Width, freshFrame.Height, 16);
                var green = freshFrame.CountInRect(0, 0, freshFrame.Width, freshFrame.Height, Tone.Green, 16);
                var blue = freshFrame.CountInRect(0, 0, freshFrame.Width, freshFrame.Height, Tone.Blue, 16);
                var overlaySamples = freshFrame.SamplesInRect(overlayX, overlayY, overlayX + OverlayWidth, overlayY + OverlayHeight, 4);
                var magenta = freshFrame.CountInRect(overlayX, overlayY, overlayX + OverlayWidth, overlayY + OverlayHeight, Tone.Magenta, 4);

                Console.WriteLine($"  fresh-frame tones: green={green} blue={blue} of {samples} whole-frame samples; " +
                                  $"overlay magenta={magenta}/{overlaySamples}");

                if (green > samples / 2)
                {
                    compositionState = "Capture shows the live FSE swap-chain content (green) => DWM composition STILL ACTIVE (FSO-emulated FSE)";
                }
                else if (blue > samples / 2)
                {
                    compositionState = "Capture shows the pre-FSE desktop (blue), not the live green FSE frame => STALE desktop, composition bypassed";
                }
                else
                {
                    compositionState = $"Capture shows neither live FSE content nor the pre-FSE desktop (green={green} blue={blue} of {samples}) => composition bypassed/broken";
                }

                Observe(compositionState);
                ReportOverlayRegion(freshFrame, overlayX, overlayY, OverlayWidth, OverlayHeight, "during FSE", expectVisible: true);
            }

            // Snapshot the detection APIs while still fullscreen (fresh duplication attempt included).
            Detect.Snapshot("during SetFullscreenState(TRUE)", overlay.Hwnd, null);

            Console.WriteLine($"  STATE CLASSIFICATION: {ClassifyFse(enter.Success, freshFrame, compositionState)}");
        }
        finally
        {
            capture?.Dispose();
            capture = null;

            // Restore in this order: leave fullscreen, then make sure the desktop mode is native.
            if (game is not null)
            {
                var exit = game.ExitFullscreen();
                Console.WriteLine($"  SetFullscreenState(FALSE) hr=0x{exit.Code:X8} success={exit.Success}");
                Console.WriteLine($"  GetFullscreenState after exit: {game.FullscreenState()}");
                game.Dispose();
                game = null;
            }

            Console.WriteLine($"  restore: {Display.Restore(originalMode)}");
            ProbeWindow.Pump(400);

            if (Display.Capture().Display != originalMode.Display)
            {
                Console.WriteLine($"  restore retry: {Display.Restore(originalMode)}");
            }

            var restoredError = "";
            var restored = CaptureFresh(out restoredError);
            if (restored is null)
            {
                Assert("exp3: duplication works again after FSE exit", false, restoredError);
            }
            else
            {
                PrintFrame("post-FSE frame", restored);
                Assert("exp3: duplication works again after FSE exit", true);
                ReportOverlayRegion(restored, overlayX, overlayY, OverlayWidth, OverlayHeight, "after FSE exit", expectVisible: true);
            }

            overlay?.Dispose();
            overlay = null;
        }
    }

    private static string ClassifyFse(bool enterSucceeded, CapturedFrame? freshFrame, string compositionState)
    {
        if (!enterSucceeded)
        {
            return "FSE REFUSED by DXGI (see hr above) - state stayed borderless/windowed";
        }

        if (freshFrame is null)
        {
            return "TRUE FSE (composition bypassed): duplication cannot be created/acquired while fullscreen";
        }

        return compositionState.Contains("STILL ACTIVE", StringComparison.Ordinal)
            ? "FSE requested, FSO ACTIVE: display pipeline still composes the game and the overlay"
            : "TRUE FSE (composition bypassed): desktop capture is stale/black while fullscreen";
    }

    private static string QueryNotification()
    {
        var hr = SHQueryUserNotificationState(out var state);
        return $"hr=0x{hr:X8} value={state} ({Detect.NotificationName(state)})";
    }

    // ----------------------------------------------------------------------------------------
    // exp3c - true FSE: disable Fullscreen Optimizations for this exe only, relaunch, re-measure
    // ----------------------------------------------------------------------------------------

    private static void ExperimentTrueFse()
    {
        Section("exp3c true FSE: relaunch the probe with FSO disabled for its own exe (compat flag)");
        var exe = Environment.ProcessPath;
        if (exe is null || string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            Assert("exp3c: probe runs from its own apphost so a per-exe compat flag applies", false, $"exe={exe}");
            return;
        }

        var hadPrevious = false;
        string? previous = null;
        try
        {
            previous = SetFsoDisabled(exe, out hadPrevious);
            Console.WriteLine($"  compat flag set: HKCU\\{LayersKey} ['{exe}'] = '{FsoDisabledFlag}' (previous={(hadPrevious ? previous : "<none>")})");

            using var child = Process.Start(new ProcessStartInfo(exe, "fse-child")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (child is null)
            {
                Assert("exp3c: relaunch probe child", false, "Process.Start returned null");
                return;
            }

            Console.WriteLine($"  child pid={child.Id}");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();

            if (!child.WaitForExit(40000))
            {
                child.Kill(entireProcessTree: true);
                Console.WriteLine("  child timed out and was killed");
            }

            Console.WriteLine(stdout.GetAwaiter().GetResult());
            var errorText = stderr.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(errorText))
            {
                Console.WriteLine("  child stderr:");
                Console.WriteLine(errorText);
            }

            Console.WriteLine($"  child exit code={child.ExitCode}");
        }
        finally
        {
            RestoreFso(exe, hadPrevious, previous);
        }
    }

    private static string? SetFsoDisabled(string exePath, out bool hadPrevious)
    {
        using var key = Registry.CurrentUser.CreateSubKey(LayersKey, writable: true)
                        ?? throw new InvalidOperationException("cannot open the AppCompatFlags Layers key");
        var previous = key.GetValue(exePath) as string;
        hadPrevious = previous is not null;
        key.SetValue(exePath, FsoDisabledFlag, RegistryValueKind.String);
        return previous;
    }

    private static void RestoreFso(string exePath, bool hadPrevious, string? previous)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LayersKey, writable: true);
            if (key is null)
            {
                Console.WriteLine("  compat flag cleanup: Layers key disappeared");
                return;
            }

            if (hadPrevious && previous is not null)
            {
                key.SetValue(exePath, previous, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(exePath, throwOnMissingValue: false);
            }

            var remaining = key.GetValue(exePath);
            Console.WriteLine($"  compat flag cleanup: remaining={(remaining is null ? "<none>" : remaining.ToString())}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  compat flag cleanup FAILED: {ex.Message}");
        }
    }

    /// <summary>Belt and braces: the child also removes the flag it was launched with.</summary>
    private static void ClearOwnFsoFlag()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LayersKey, writable: true);
            if (key?.GetValue(exe) is string value &&
                value.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(exe, throwOnMissingValue: false);
                Console.WriteLine($"  child removed its own compat flag (remaining={key.GetValue(exe) ?? "<none>"})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  child could not remove the compat flag: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------------------------------
    // exp5a - monitor-sized transparent overlay window over an FSO-style game
    // ----------------------------------------------------------------------------------------

    private static void ExperimentFullscreenOverlay()
    {
        Section("exp5a monitor-sized (transparent) DComp overlay window over an FSO-style game");
        var (monitor, width, height) = Desktop(out var mode);

        var contentX = monitor.Left + ((monitor.Width - OverlayWidth) / 2);
        var contentY = monitor.Bottom - OverlayHeight - 120;

        try
        {
            using var game = GameWindow.Create("GameFso2", width, height, flipModel: true, topmost: true);
            ProbeWindow.PumpWithFrames(300, () => game.Render(GameBlue));

            // Window covers the whole monitor; only the small visual is painted.
            using var overlay = OverlaySurface.Create(
                "OverlayFullscreen",
                monitor.Left, monitor.Top, monitor.Width, monitor.Height,
                contentX, contentY, OverlayWidth, OverlayHeight,
                clickThrough: true);

            overlay.Render(OverlayMagenta);
            ProbeWindow.PumpWithFrames(600, () => game.Render(GameBlue));

            using var capture = DesktopCapture.Create();
            var error = "";
            var frame = Capture(capture, out error);
            if (frame is null)
            {
                Assert("exp5a: capture", false, error);
                return;
            }

            PrintFrame("frame", frame);
            var samples = frame.SamplesInRect(contentX, contentY, contentX + OverlayWidth, contentY + OverlayHeight, 4);
            var magenta = frame.CountInRect(contentX, contentY, contentX + OverlayWidth, contentY + OverlayHeight, Tone.Magenta, 4);
            var ratio = samples == 0 ? 0 : (double)magenta / samples;
            Console.WriteLine($"  monitor-sized overlay type: content({contentX},{contentY},{OverlayWidth}x{OverlayHeight}) " +
                              $"in window({monitor.Left},{monitor.Top},{monitor.Width}x{monitor.Height})");
            Assert("exp5a: monitor-sized transparent overlay still composites over the FSO game", ratio > 0.9, $"ratio={ratio:P1}");
        }
        finally
        {
            Console.WriteLine($"  restore: {Display.Restore(mode)}");
        }
    }

    // ----------------------------------------------------------------------------------------
    // exp6 - end-to-end against the real Overlyric.exe --demo
    // ----------------------------------------------------------------------------------------

    private static void ExperimentReal()
    {
        Section("exp6 end-to-end: real Overlyric.exe --demo over the desktop and over the synthetic FSO game");
        var (monitor, width, height) = Desktop(out var mode);

        var exe = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Overlyric", "bin", "Release", "net10.0-windows10.0.26100.0", "win-x64", "publish", "Overlyric.exe"));

        Console.WriteLine($"  exe={exe} exists={File.Exists(exe)}");

        var existing = Process.GetProcessesByName("Overlyric");
        Console.WriteLine($"  Overlyric processes already running: {existing.Length}");

        Process? spawned = null;
        var spawnedByUs = false;

        try
        {
            if (existing.Length > 0)
            {
                Observe("an Overlyric instance is already running; the probe will not start or stop it");
            }
            else
            {
                if (!File.Exists(exe))
                {
                    Assert("exp6: published Overlyric.exe present", false, exe);
                    return;
                }

                spawned = Process.Start(new ProcessStartInfo(exe, "--demo") { UseShellExecute = false });
                spawnedByUs = spawned is not null;
                Console.WriteLine($"  started Overlyric --demo pid={spawned?.Id} (spawnedByUs={spawnedByUs})");
            }

            // Wait for the overlay window class to appear.
            var overlayHwnd = IntPtr.Zero;
            for (var i = 0; i < 80 && overlayHwnd == IntPtr.Zero; i++)
            {
                overlayHwnd = FindWindowW("OverlyricOverlayWindow", null);
                if (overlayHwnd == IntPtr.Zero)
                {
                    ProbeWindow.Pump(100);
                }
            }

            Console.WriteLine($"  OverlyricOverlayWindow hwnd=0x{overlayHwnd:X}");
            ProbeWindow.Pump(1800);

            using var capture = DesktopCapture.Create();
            var error = "";
            var frame = Capture(capture, out error);
            if (frame is null)
            {
                Assert("exp6: capture of the real overlay", false, error);
                return;
            }

            PrintFrame("frame", frame);
            GetWindowRect(overlayHwnd, out var overlayRect);
            Console.WriteLine($"  overlay window rect={overlayRect}");

            if (overlayHwnd != IntPtr.Zero)
            {
                var white = frame.CountNearWhiteInRect(overlayRect.Left, overlayRect.Top, overlayRect.Right, overlayRect.Bottom, 170);
                var clippedSamples = frame.SamplesInRect(overlayRect.Left, overlayRect.Top, overlayRect.Right, overlayRect.Bottom, 2);
                Console.WriteLine($"  near-white pixels inside the real overlay rect: {white}/{clippedSamples} (sampled every 2px)");
                Assert("exp6: real Overlyric demo lyrics are composited on the desktop", white > 40, $"white={white}");
            }

            Detect.Snapshot("Overlyric --demo over the desktop", overlayHwnd, capture);

            // ---- now put the synthetic topmost fullscreen game above it and re-assert ----
            Section("exp6b real overlay vs a topmost fullscreen game");
            using var game = GameWindow.Create("GameRealFso", width, height, flipModel: true, topmost: true);
            ProbeWindow.PumpWithFrames(500, () => game.Render(GameBlue));

            var afterGame = Capture(capture, out var afterGameError);
            if (afterGame is null)
            {
                Assert("exp6b: capture with the game on top", false, afterGameError);
                return;
            }

            var whiteAfterGame = afterGame.CountNearWhiteInRect(
                overlayRect.Left, overlayRect.Top, overlayRect.Right, overlayRect.Bottom, 170);
            Console.WriteLine($"  after the topmost game window appeared: near-white in overlay rect = {whiteAfterGame}");
            Observe(whiteAfterGame > 40
                ? "the real overlay is still above the newly created topmost game window"
                : "the newly created topmost game window covers the real overlay (game created later wins the topmost band)");

            // Mimic the product's ReassertTopmost from outside: bump the overlay window to the top.
            SetWindowPos(overlayHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            ProbeWindow.PumpWithFrames(400, () => game.Render(GameBlue));

            var afterReassert = Capture(capture, out var afterReassertError);
            if (afterReassert is null)
            {
                Assert("exp6b: capture after overlay topmost re-assert", false, afterReassertError);
                return;
            }

            var whiteAfterReassert = afterReassert.CountNearWhiteInRect(
                overlayRect.Left, overlayRect.Top, overlayRect.Right, overlayRect.Bottom, 170);
            Console.WriteLine($"  after SetWindowPos(overlay, HWND_TOPMOST): near-white in overlay rect = {whiteAfterReassert}");
            Assert("exp6b: externally re-asserting topmost puts the real overlay above the game", whiteAfterReassert > 40,
                $"white={whiteAfterReassert}");
        }
        finally
        {
            if (spawnedByUs && spawned is not null)
            {
                try
                {
                    if (!spawned.HasExited)
                    {
                        spawned.Kill(entireProcessTree: true);
                        spawned.WaitForExit(3000);
                    }

                    Console.WriteLine($"  stopped the Overlyric instance this probe started (pid={spawned.Id}, exited={spawned.HasExited})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  could not stop spawned Overlyric: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("  left the user's Overlyric instance running");
            }

            Console.WriteLine($"  restore: {Display.Restore(mode)}");
        }
    }
}
