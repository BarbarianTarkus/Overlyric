using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using static FullscreenProbe.NativeMethods;

namespace FullscreenProbe;

/// <summary>Window class plumbing: one class per probe window, default window proc, message pump.</summary>
internal static class ProbeWindow
{
    private static readonly HashSet<string> Registered = [];
    private static readonly Lock Gate = new();

    public static IntPtr Create(string name, int x, int y, int width, int height, int extraStyle)
    {
        var className = "FullscreenProbe." + name;

        lock (Gate)
        {
            if (Registered.Add(className))
            {
                unsafe
                {
                    var windowClass = new WNDCLASSEXW
                    {
                        cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                        style = 0,
                        lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&StaticWindowProc,
                        cbClsExtra = 0,
                        cbWndExtra = 0,
                        hInstance = GetModuleHandleW(null),
                        hIcon = IntPtr.Zero,
                        hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
                        hbrBackground = IntPtr.Zero,
                        lpszMenuName = null,
                        lpszClassName = className,
                        hIconSm = IntPtr.Zero
                    };

                    if (RegisterClassExW(ref windowClass) == 0)
                    {
                        Registered.Remove(className);
                        throw new InvalidOperationException(
                            $"RegisterClassExW failed for '{className}' (error {Marshal.GetLastWin32Error()}).");
                    }
                }
            }
        }

        var hwnd = CreateWindowExW(
            extraStyle,
            className,
            name,
            WS_POPUP | WS_VISIBLE,
            x,
            y,
            width,
            height,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW failed for '{className}' (error {Marshal.GetLastWin32Error()}).");
        }

        return hwnd;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr StaticWindowProc(nint hWnd, uint msg, nint wParam, nint lParam) =>
        DefWindowProcW(hWnd, msg, wParam, lParam);

    /// <summary>Drain the message queue for a while so windows stay responsive.</summary>
    public static void Pump(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }

            Thread.Sleep(2);
        }
    }

    public static void PumpWithFrames(int milliseconds, Action? perFrame = null)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            perFrame?.Invoke();

            while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }

            Thread.Sleep(8);
        }
    }
}

/// <summary>
/// A synthetic "game": a D3D11 swap chain on a borderless fullscreen HWND. Flip model is the
/// borderless-windowed case; the blt model is the legacy exclusive-fullscreen case.
/// </summary>
internal sealed class GameWindow : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly IDXGIFactory2 _factory;
    private readonly IDXGIAdapter _adapter;
    private readonly IDXGISwapChain1 _swapChain;
    private bool _disposed;

    private GameWindow(
        IntPtr hwnd,
        int width,
        int height,
        bool flipModel,
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIDevice dxgiDevice,
        IDXGIFactory2 factory,
        IDXGIAdapter adapter,
        IDXGIOutput output,
        IDXGISwapChain1 swapChain)
    {
        Hwnd = hwnd;
        Width = width;
        Height = height;
        FlipModel = flipModel;
        _device = device;
        _context = context;
        _dxgiDevice = dxgiDevice;
        _factory = factory;
        _adapter = adapter;
        Output = output;
        _swapChain = swapChain;
    }

    public IntPtr Hwnd { get; }

    public int Width { get; }

    public int Height { get; }

    public bool FlipModel { get; }

    public IDXGIOutput Output { get; }

    public static GameWindow Create(string name, int width, int height, bool flipModel, bool topmost, bool allowModeSwitch = false)
    {
        var hwnd = ProbeWindow.Create(name, 0, 0, width, height, topmost ? WS_EX_TOPMOST : 0);

        var hr = D3D11.D3D11CreateDevice(
            null!,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context);

        if (device is null || context is null)
        {
            throw new InvalidOperationException($"D3D11CreateDevice failed hr=0x{hr.Code:X8}.");
        }

        var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        var adapter = dxgiDevice.GetAdapter();
        var factory = adapter.GetParent<IDXGIFactory2>();

        var enumResult = adapter.EnumOutputs(0, out var output);
        if (output is null)
        {
            throw new InvalidOperationException($"EnumOutputs(0) failed hr=0x{enumResult.Code:X8}.");
        }

        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = flipModel ? SwapEffect.FlipDiscard : SwapEffect.Discard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = allowModeSwitch ? SwapChainFlags.AllowModeSwitch : SwapChainFlags.None
        };

        var swapChain = factory.CreateSwapChainForHwnd(dxgiDevice, hwnd, description, null, null);

        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, width, height,
            SWP_SHOWWINDOW | SWP_NOACTIVATE);

        return new GameWindow(
            hwnd, width, height, flipModel, device, context, dxgiDevice, factory, adapter, output, swapChain);
    }

    /// <summary>
    /// Paint the current back buffer and present. Buffer 0 only: a flip-model D3D11 swap chain
    /// exposes just the current back buffer and rejects GetBuffer(1) with DXGI_ERROR_INVALID_CALL.
    /// </summary>
    public void Render(Color4 color)
    {
        using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var view = _device.CreateRenderTargetView(buffer);
        _context.ClearRenderTargetView(view, color);
        _swapChain.Present(1, PresentFlags.None);
    }

    public Result EnterFullscreen() => _swapChain.SetFullscreenState(true, Output);

    public Result ExitFullscreen() => _swapChain.SetFullscreenState(false, null!);

    public string FullscreenState()
    {
        var result = _swapChain.GetFullscreenState(out RawBool state, out var output);
        output?.Dispose();
        return result.Success ? $"fullscreen={(bool)state}" : $"GetFullscreenState hr=0x{result.Code:X8}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _swapChain.Dispose();
        _factory.Dispose();
        _dxgiDevice.Dispose();
        _adapter.Dispose();
        Output.Dispose();
        _context.Dispose();
        _device.Dispose();
        DestroyWindow(Hwnd);
    }
}

/// <summary>
/// The overlay: a topmost, non-activating window with no redirection bitmap, whose pixels come
/// from a DirectComposition visual hosting a premultiplied composition swap chain. The visual can
/// be smaller than the window, which is how the monitor-sized variant is built.
/// </summary>
internal sealed class OverlaySurface : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly IDXGIFactory2 _factory;
    private readonly IDCompositionDevice _compositionDevice;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;
    private readonly IDXGISwapChain1 _swapChain;
    private bool _disposed;

    private OverlaySurface(
        IntPtr hwnd,
        int windowX,
        int windowY,
        int windowWidth,
        int windowHeight,
        int contentX,
        int contentY,
        int contentWidth,
        int contentHeight,
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIDevice dxgiDevice,
        IDXGIFactory2 factory,
        IDCompositionDevice compositionDevice,
        IDCompositionTarget target,
        IDCompositionVisual visual,
        IDXGISwapChain1 swapChain)
    {
        Hwnd = hwnd;
        WindowX = windowX;
        WindowY = windowY;
        WindowWidth = windowWidth;
        WindowHeight = windowHeight;
        ContentX = contentX;
        ContentY = contentY;
        ContentWidth = contentWidth;
        ContentHeight = contentHeight;
        _device = device;
        _context = context;
        _dxgiDevice = dxgiDevice;
        _factory = factory;
        _compositionDevice = compositionDevice;
        _target = target;
        _visual = visual;
        _swapChain = swapChain;
    }

    public IntPtr Hwnd { get; }

    public int WindowX { get; }

    public int WindowY { get; }

    public int WindowWidth { get; }

    public int WindowHeight { get; }

    public int ContentX { get; }

    public int ContentY { get; }

    public int ContentWidth { get; }

    public int ContentHeight { get; }

    public static OverlaySurface Create(
        string name,
        int windowX,
        int windowY,
        int windowWidth,
        int windowHeight,
        int contentX,
        int contentY,
        int contentWidth,
        int contentHeight,
        bool clickThrough)
    {
        var extraStyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP;
        if (clickThrough)
        {
            extraStyle |= WS_EX_TRANSPARENT;
        }

        var hwnd = ProbeWindow.Create(name, windowX, windowY, windowWidth, windowHeight, extraStyle);

        var hr = D3D11.D3D11CreateDevice(
            null!,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context);

        if (device is null || context is null)
        {
            throw new InvalidOperationException($"D3D11CreateDevice failed hr=0x{hr.Code:X8}.");
        }

        var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        var factory = dxgiDevice.GetAdapter().GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = (uint)contentWidth,
            Height = (uint)contentHeight,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None
        };

        var swapChain = factory.CreateSwapChainForComposition(dxgiDevice, description);

        var compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        compositionDevice.CreateTargetForHwnd(hwnd, true, out var target);
        compositionDevice.CreateVisual(out var visual);

        // The visual is placed inside the window; for the small overlay they coincide.
        if (contentX != windowX || contentY != windowY)
        {
            visual.SetOffsetX(contentX - windowX);
            visual.SetOffsetY(contentY - windowY);
        }

        visual.SetContent(swapChain);
        target.SetRoot(visual);
        compositionDevice.Commit();

        SetWindowPos(hwnd, HWND_TOPMOST, windowX, windowY, windowWidth, windowHeight,
            SWP_SHOWWINDOW | SWP_NOACTIVATE);

        return new OverlaySurface(
            hwnd, windowX, windowY, windowWidth, windowHeight, contentX, contentY, contentWidth, contentHeight,
            device, context, dxgiDevice, factory, compositionDevice, target, visual, swapChain);
    }

    /// <summary>Paint the composition surface and hand it to DirectComposition.</summary>
    public void Render(Color4 color)
    {
        using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var view = _device.CreateRenderTargetView(buffer);
        _context.ClearRenderTargetView(view, color);
        _swapChain.Present(1, PresentFlags.None);
        _compositionDevice.Commit();
    }

    /// <summary>Re-assert topmost without touching size or position (the product's ReassertTopmost).</summary>
    public void ReassertTopmost() =>
        SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public void MoveTo(int x, int y, int width, int height) =>
        SetWindowPos(Hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _target.Dispose();
        _visual.Dispose();
        _compositionDevice.Dispose();
        _swapChain.Dispose();
        _factory.Dispose();
        _dxgiDevice.Dispose();
        _context.Dispose();
        _device.Dispose();
        DestroyWindow(Hwnd);
    }
}

internal enum Tone
{
    Black,
    Blue,
    Green,
    Magenta,
    White,
    Other
}

/// <summary>One captured composed-desktop frame, with cheap pixel classification.</summary>
internal sealed class CapturedFrame
{
    public required byte[] Pixels { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Stride { get; init; }

    public required string Format { get; init; }

    public uint AccumulatedFrames { get; init; }

    public bool IsBgra => Format == "B8G8R8A8_UNorm";

    public (byte B, byte G, byte R, byte A) Pixel(int x, int y)
    {
        var offset = (y * Stride) + (x * 4);
        return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]);
    }

    public static Tone Classify(byte b, byte g, byte r)
    {
        if (r <= 48 && g <= 48 && b <= 48)
        {
            return Tone.Black;
        }

        if (b >= 100 && b > r * 2 && b > g * 2)
        {
            return Tone.Blue;
        }

        if (g >= 100 && g > r * 2 && g > b * 2)
        {
            return Tone.Green;
        }

        if (r >= 100 && b >= 100 && g * 2 < r && g * 2 < b)
        {
            return Tone.Magenta;
        }

        if (r >= 180 && g >= 180 && b >= 180)
        {
            return Tone.White;
        }

        return Tone.Other;
    }

    public Tone ToneAt(int x, int y)
    {
        var (b, g, r, _) = Pixel(x, y);
        return Classify(b, g, r);
    }

    public Dictionary<Tone, int> CountTones(int step)
    {
        var counts = new Dictionary<Tone, int>();
        if (!IsBgra)
        {
            return counts;
        }

        for (var y = 0; y < Height; y += step)
        {
            for (var x = 0; x < Width; x += step)
            {
                var tone = ToneAt(x, y);
                counts[tone] = counts.GetValueOrDefault(tone) + 1;
            }
        }

        return counts;
    }

    /// <summary>Count sampled pixels of one tone inside a rectangle (clipped to the frame).</summary>
    public int CountInRect(int left, int top, int right, int bottom, Tone tone, int step)
    {
        if (!IsBgra)
        {
            return 0;
        }

        var x0 = Math.Max(0, left);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(Width, right);
        var y1 = Math.Min(Height, bottom);

        var count = 0;
        for (var y = y0; y < y1; y += step)
        {
            for (var x = x0; x < x1; x += step)
            {
                if (ToneAt(x, y) == tone)
                {
                    count++;
                }
            }
        }

        return count;
    }

    public int SamplesInRect(int left, int top, int right, int bottom, int step)
    {
        var x0 = Math.Max(0, left);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(Width, right);
        var y1 = Math.Min(Height, bottom);
        if (x1 <= x0 || y1 <= y0)
        {
            return 0;
        }

        return ((x1 - x0 + step - 1) / step) * ((y1 - y0 + step - 1) / step);
    }

    public int CountNearWhiteInRect(int left, int top, int right, int bottom, int threshold)
    {
        if (!IsBgra)
        {
            return 0;
        }

        var x0 = Math.Max(0, left);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(Width, right);
        var y1 = Math.Min(Height, bottom);

        var count = 0;
        for (var y = y0; y < y1; y += 2)
        {
            for (var x = x0; x < x1; x += 2)
            {
                var (b, g, r, _) = Pixel(x, y);
                if (r >= threshold && g >= threshold && b >= threshold)
                {
                    count++;
                }
            }
        }

        return count;
    }

    public string Describe()
    {
        var counts = CountTones(8);
        return string.Join(
            " ",
            Enum.GetValues<Tone>().Select(tone => $"{tone}={counts.GetValueOrDefault(tone)}"));
    }
}

/// <summary>
/// Desktop Duplication capture. This is the decisive probe: it reads back what the display
/// output is actually showing (i.e. what DWM composed), so overlay pixels appearing here means
/// the overlay is really on screen, and a failure or stale/black frame means composition is gone.
/// </summary>
internal sealed class DesktopCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly IDXGIAdapter _adapter;
    private readonly IDXGIOutput _output;
    private readonly IDXGIOutput1 _output1;
    private readonly IDXGIOutputDuplication _duplication;
    private ID3D11Texture2D? _staging;
    private bool _disposed;

    private DesktopCapture(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIDevice dxgiDevice,
        IDXGIAdapter adapter,
        IDXGIOutput output,
        IDXGIOutput1 output1,
        IDXGIOutputDuplication duplication,
        string adapterName,
        string outputName,
        string duplicateDescription)
    {
        _device = device;
        _context = context;
        _dxgiDevice = dxgiDevice;
        _adapter = adapter;
        _output = output;
        _output1 = output1;
        _duplication = duplication;
        AdapterName = adapterName;
        OutputName = outputName;
        DuplicationDescription = duplicateDescription;
    }

    public string AdapterName { get; }

    public string OutputName { get; }

    public string DuplicationDescription { get; }

    public static DesktopCapture Create()
    {
        var hr = D3D11.D3D11CreateDevice(
            null!,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context);

        if (device is null || context is null)
        {
            throw new InvalidOperationException($"D3D11CreateDevice failed hr=0x{hr.Code:X8}.");
        }

        var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        var adapter = dxgiDevice.GetAdapter();
        var enumResult = adapter.EnumOutputs(0, out var output);
        if (output is null)
        {
            throw new InvalidOperationException($"EnumOutputs(0) failed hr=0x{enumResult.Code:X8}.");
        }

        var adapterDescription = adapter.Description;
        var outputDescription = output.Description;
        var output1 = output.QueryInterface<IDXGIOutput1>();
        var duplication = output1.DuplicateOutput(device);

        var description = duplication.Description;
        var duplicateText = $"Mode={description.ModeDescription.Width}x{description.ModeDescription.Height} " +
                            $"Format={description.ModeDescription.Format} Rotation={description.Rotation}";

        return new DesktopCapture(
            device,
            context,
            dxgiDevice,
            adapter,
            output,
            output1,
            duplication,
            adapterDescription.Description.Trim(),
            outputDescription.DeviceName.Trim(),
            duplicateText);
    }

    /// <summary>Try to capture; returns null and an HRESULT text when duplication refuses.</summary>
    public CapturedFrame? TryCapture(uint timeoutMilliseconds, Dictionary<string, string> log)
    {
        try
        {
            var acquire = _duplication.AcquireNextFrame(timeoutMilliseconds, out var frameInfo, out var resource);
            if (acquire.Failure)
            {
                log["capture-error"] = $"0x{acquire.Code:X8} (AcquireNextFrame)";
                return null;
            }

            try
            {
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                var description = texture.Description;

                if (_staging is null || _staging.Description.Width != description.Width ||
                    _staging.Description.Height != description.Height ||
                    _staging.Description.Format != description.Format)
                {
                    _staging?.Dispose();
                    _staging = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = description.Width,
                        Height = description.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = description.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    });
                }

                _context.CopyResource(_staging, texture);

                var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var rowPitch = (int)mapped.RowPitch;
                    var pixels = new byte[rowPitch * description.Height];
                    for (var y = 0; y < description.Height; y++)
                    {
                        Marshal.Copy(mapped.DataPointer + (y * rowPitch), pixels, y * rowPitch, rowPitch);
                    }

                    var frame = new CapturedFrame
                    {
                        Pixels = pixels,
                        Width = (int)description.Width,
                        Height = (int)description.Height,
                        Stride = rowPitch,
                        Format = description.Format.ToString(),
                        AccumulatedFrames = frameInfo.AccumulatedFrames
                    };

                    return frame;
                }
                finally
                {
                    _context.Unmap(_staging, 0);
                }
            }
            finally
            {
                _duplication.ReleaseFrame();
            }
        }
        catch (SharpGenException ex)
        {
            log["capture-error"] = $"0x{ex.HResult:X8} {ex.Message}";
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _staging?.Dispose();
        _duplication.Dispose();
        _output1.Dispose();
        _output.Dispose();
        _adapter.Dispose();
        _dxgiDevice.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
