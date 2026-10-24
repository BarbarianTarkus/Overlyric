using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;

namespace VorticeAot;

/// <summary>
/// Risk probe: does the Vortice COM interop layer survive NativeAOT?
/// Creates the device/factory objects the overlay renderer needs and exercises
/// the DirectWrite text pipeline.
/// </summary>
internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("VorticeAot starting");

        try
        {
            var hr = D3D11.D3D11CreateDevice(
                null!,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                null!,
                out ID3D11Device? device,
                out ID3D11DeviceContext? context);

            Console.WriteLine($"  D3D11CreateDevice hr={hr} device={device is not null} context={context is not null}");
            if (device is null)
            {
                Console.WriteLine("FAIL: no D3D11 device");
                return;
            }

            using var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            Console.WriteLine($"  D2D1 factory = {d2dFactory is not null}");

            using var dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
            Console.WriteLine($"  DWrite factory = {dwriteFactory is not null}");

            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            Console.WriteLine($"  DXGI device = {dxgiDevice is not null}");

            using var dcompDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            Console.WriteLine($"  DComp device = {dcompDevice is not null}");

            using var textFormat = dwriteFactory.CreateTextFormat(
                "Segoe UI Variable Display",
                null!,
                FontWeight.SemiBold,
                FontStyle.Normal,
                FontStretch.Normal,
                28.0f);
            Console.WriteLine($"  TextFormat = {textFormat is not null}");

            using var layout = dwriteFactory.CreateTextLayout("Overlyric AOT probe", textFormat, 600.0f, 80.0f);
            var metrics = layout.Metrics;
            Console.WriteLine($"  TextLayout metrics width={metrics.Width} height={metrics.Height}");

            // Settle whether TextMetrics.Width is the natural text width or the layout constraint:
            // the overlay sizes its backdrop pill from this value.
            using var probeFormat = dwriteFactory.CreateTextFormat(
                "Segoe UI Variable Display",
                null!,
                FontWeight.SemiBold,
                FontStyle.Normal,
                FontStretch.Normal,
                30.0f);

            using var probeLayout = dwriteFactory.CreateTextLayout("No plugin for any player", probeFormat, 1184.0f, 200.0f);

            probeLayout.TextAlignment = TextAlignment.Center;
            var centered = probeLayout.Metrics;
            Console.WriteLine(
                $"  ALIGN=Center  Width={centered.Width:F2} LayoutWidth={centered.LayoutWidth:F2} Height={centered.Height:F2}");

            probeLayout.TextAlignment = TextAlignment.Leading;
            var leading = probeLayout.Metrics;
            Console.WriteLine(
                $"  ALIGN=Leading Width={leading.Width:F2} LayoutWidth={leading.LayoutWidth:F2} Height={leading.Height:F2}");

            Console.WriteLine("VORTICE_AOT_OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}
