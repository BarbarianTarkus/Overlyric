using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Overlyric.Overlay;

/// <summary>
/// GPU composition stack behind the overlay: a Direct3D 11 device feeding an alpha-blended
/// composition swap chain, presented through DirectComposition onto a window that has no
/// redirection bitmap.
/// <para>
/// This is the most efficient transparent overlay Windows offers. There is no per-frame CPU
/// pixel copy (unlike <c>UpdateLayeredWindow</c> with a DIB), no layered-window redirection
/// surface, and the desktop compositor blends the result on the GPU. Because Overlyric only
/// redraws when the lyric line changes, steady-state CPU cost is effectively zero.
/// </para>
/// </summary>
internal sealed class CompositionHost : IDisposable
{
    private const int BufferCount = 2;

    private readonly ID3D11Device _d3dDevice;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly IDXGIFactory2 _dxgiFactory;
    private readonly IDCompositionDevice _compositionDevice;
    private readonly IDCompositionTarget _compositionTarget;
    private readonly IDCompositionVisual _compositionVisual;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _d2dContext;

    private IDXGISwapChain1 _swapChain;
    private ID2D1Bitmap1? _backBufferBitmap;
    private int _pixelWidth;
    private int _pixelHeight;
    private float _dpi;
    private float _appliedDpi = -1f;
    private bool _disposed;

    private CompositionHost(
        ID3D11Device d3dDevice,
        IDXGIDevice dxgiDevice,
        IDXGIFactory2 dxgiFactory,
        IDCompositionDevice compositionDevice,
        IDCompositionTarget compositionTarget,
        IDCompositionVisual compositionVisual,
        ID2D1Factory1 d2dFactory,
        ID2D1Device d2dDevice,
        ID2D1DeviceContext d2dContext,
        IDXGISwapChain1 swapChain)
    {
        _d3dDevice = d3dDevice;
        _dxgiDevice = dxgiDevice;
        _dxgiFactory = dxgiFactory;
        _compositionDevice = compositionDevice;
        _compositionTarget = compositionTarget;
        _compositionVisual = compositionVisual;
        _d2dFactory = d2dFactory;
        _d2dDevice = d2dDevice;
        _d2dContext = d2dContext;
        _swapChain = swapChain;
    }

    /// <summary>The DirectWrite/Direct2D context the renderer draws into.</summary>
    public ID2D1DeviceContext DeviceContext => _d2dContext;

    public IDWriteFactory? WriteFactory { get; private init; }

    /// <summary>Logical size of the overlay in device-independent pixels.</summary>
    public Size DipSize => new(_pixelWidth / (_dpi / 96f), _pixelHeight / (_dpi / 96f));

    public static CompositionHost Create(IntPtr hwnd, int pixelWidth, int pixelHeight, float dpi)
    {
        var hr = D3D11.D3D11CreateDevice(
            null!,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out ID3D11Device? d3dDevice,
            out ID3D11DeviceContext? _);

        if (d3dDevice is null)
        {
            throw new InvalidOperationException($"D3D11CreateDevice failed (hr={hr}).");
        }

        var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        var dxgiFactory = adapter.GetParent<IDXGIFactory2>();

        var swapChain = CreateSwapChain(dxgiFactory, dxgiDevice, pixelWidth, pixelHeight);

        var compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        compositionDevice.CreateTargetForHwnd(hwnd, true, out var compositionTarget);
        compositionDevice.CreateVisual(out var compositionVisual);
        compositionVisual.SetContent(swapChain);
        compositionTarget.SetRoot(compositionVisual);
        compositionDevice.Commit();

        var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        var d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
        var d2dContext = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        var host = new CompositionHost(
            d3dDevice,
            dxgiDevice,
            dxgiFactory,
            compositionDevice,
            compositionTarget,
            compositionVisual,
            d2dFactory,
            d2dDevice,
            d2dContext,
            swapChain)
        {
            _pixelWidth = pixelWidth,
            _pixelHeight = pixelHeight,
            _dpi = dpi,
            WriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>()
        };

        host.BindBackBuffer();
        return host;
    }

    private static IDXGISwapChain1 CreateSwapChain(
        IDXGIFactory2 factory, IDXGIDevice device, int width, int height)
    {
        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            // Premultiplied alpha is what lets the compositor blend the overlay over the desktop.
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None
        };

        return factory.CreateSwapChainForComposition(device, description);
    }

    private void BindBackBuffer()
    {
        _backBufferBitmap?.Dispose();

        using var surface = _swapChain.GetBuffer<IDXGISurface>(0);

        var properties = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            _dpi,
            _dpi,
            BitmapOptions.Target | BitmapOptions.CannotDraw);

        _backBufferBitmap = _d2dContext.CreateBitmapFromDxgiSurface(surface, properties);
        _d2dContext.Target = _backBufferBitmap;
    }

    /// <summary>Resize the composition surface, for example after a DPI or mode change.</summary>
    public void Resize(int pixelWidth, int pixelHeight, float dpi)
    {
        if (pixelWidth == _pixelWidth && pixelHeight == _pixelHeight && Math.Abs(dpi - _dpi) < 0.01f)
        {
            return;
        }

        _d2dContext.Target = null;
        _backBufferBitmap?.Dispose();
        _backBufferBitmap = null;

        _swapChain.ResizeBuffers(
            BufferCount,
            (uint)pixelWidth,
            (uint)pixelHeight,
            Format.B8G8R8A8_UNorm,
            SwapChainFlags.None);

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _dpi = dpi;

        // The new target needs its DPI applied on the next frame.
        _appliedDpi = -1f;

        BindBackBuffer();
    }

    /// <summary>Clear to fully transparent and hand back the context, set up for DIP coordinates.</summary>
    public ID2D1DeviceContext BeginDraw()
    {
        _d2dContext.BeginDraw();

        // Setting the same DPI again would invalidate Direct2D's device-dependent resources and
        // make it rebuild them, so only touch it when the value actually changed.
        if (_appliedDpi != _dpi)
        {
            _d2dContext.SetDpi(_dpi, _dpi);
            _appliedDpi = _dpi;
        }

        _d2dContext.Clear(new Color4(0f, 0f, 0f, 0f));
        return _d2dContext;
    }

    /// <summary>Finish the frame and hand it to the compositor.</summary>
    /// <returns>False when the device was lost and the host must be rebuilt.</returns>
    public bool EndDraw()
    {
        _d2dContext.EndDraw();

        var present = _swapChain.Present(1, PresentFlags.None);
        return present.Success;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _d2dContext.Target = null;
        _backBufferBitmap?.Dispose();
        _compositionVisual.Dispose();
        _compositionTarget.Dispose();
        _compositionDevice.Dispose();
        _d2dContext.Dispose();
        _d2dDevice.Dispose();
        _d2dFactory.Dispose();
        _swapChain.Dispose();
        _dxgiFactory.Dispose();
        _dxgiDevice.Dispose();
        _d3dDevice.Dispose();
        WriteFactory?.Dispose();
    }
}
