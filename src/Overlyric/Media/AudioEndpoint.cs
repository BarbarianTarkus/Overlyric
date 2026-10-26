using System.Runtime.InteropServices;
using Overlyric.Interop;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Media;

/// <summary>
/// Reads the current default render endpoint from CoreAudio: its stable id, its friendly name, and
/// whether Windows calls it a Bluetooth endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="AudioSessions"/>: raw vtable calls through Standard-call delegates and
/// no <c>[ComImport]</c>, because NativeAOT carries no built-in COM support. Every path fails soft:
/// a missing endpoint returns null and the caller keeps the last known device. A timing feature must
/// never take the overlay down because a USB headset was unplugged.
/// </para>
/// <para>
/// The id is the key and the name is only a label. Two outputs can share a friendly name, and the
/// name is user-editable; the id is what survives a rename. The Bluetooth flag comes from the
/// endpoint's enumerator name, not from the codec, so it is used to explain a value, never to invent
/// one.
/// </para>
/// </remarks>
internal static class AudioEndpoint
{
    private const int GetDefaultEndpointSlot = 4;   // IMMDeviceEnumerator
    private const int OpenPropertyStoreSlot = 4;    // IMMDevice
    private const int GetIdSlot = 5;                // IMMDevice
    private const int GetValueSlot = 5;             // IPropertyStore

    private const int ERender = 0;
    private const int EConsole = 0;

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    private static readonly PropertyKey DeviceFriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    private static readonly PropertyKey DeviceEnumeratorName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        24);

    /// <summary>The current default output, or null when CoreAudio cannot answer.</summary>
    public static AudioOutput? ReadDefault()
    {
        if (CoCreateInstance(
                ClsidMMDeviceEnumerator,
                IntPtr.Zero,
                CLSCTX_ALL,
                IidIMMDeviceEnumerator,
                out var enumerator) < 0)
        {
            return null;
        }

        try
        {
            return ReadFrom(enumerator);
        }
        catch (Exception)
        {
            // A device can disappear mid-read; the caller already treats null as "keep the old one".
            return null;
        }
        finally
        {
            ComVtable.Release(enumerator);
        }
    }

    private static AudioOutput? ReadFrom(nint enumerator)
    {
        var getDefaultEndpoint = ComVtable.Slot<GetDefaultEndpointDelegate>(enumerator, GetDefaultEndpointSlot);

        // eRender, eConsole: the device the Windows sound panel calls the default. It is the one a
        // listener means by "my output", and the one Windows labels with a name people recognise.
        if (getDefaultEndpoint(enumerator, ERender, EConsole, out var device) < 0)
        {
            return null;
        }

        try
        {
            var id = ReadId(device) ?? string.Empty;
            var name = ReadProperty(device, DeviceFriendlyName);
            var enumeratorName = ReadProperty(device, DeviceEnumeratorName) ?? string.Empty;

            return new AudioOutput(
                id,
                string.IsNullOrWhiteSpace(name) ? "Output device" : name,
                enumeratorName.StartsWith("BTH", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ComVtable.Release(device);
        }
    }

    private static string? ReadId(nint device)
    {
        var getId = ComVtable.Slot<GetIdDelegate>(device, GetIdSlot);

        if (getId(device, out var id) < 0 || id == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(id);
        }
        finally
        {
            CoTaskMemFree(id);
        }
    }

    private static string? ReadProperty(nint device, in PropertyKey key)
    {
        var open = ComVtable.Slot<OpenPropertyStoreDelegate>(device, OpenPropertyStoreSlot);

        if (open(device, STGM_READ, out var store) < 0 || store == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var getValue = ComVtable.Slot<GetValueDelegate>(store, GetValueSlot);
            var value = default(PropVariant);

            if (getValue(store, key, out value) < 0)
            {
                return null;
            }

            try
            {
                return value.Vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            ComVtable.Release(store);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDefaultEndpointDelegate(nint self, int dataFlow, int role, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIdDelegate(nint self, out nint id);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenPropertyStoreDelegate(nint self, uint access, out nint store);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetValueDelegate(nint self, in PropertyKey key, out PropVariant value);
}
