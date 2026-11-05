using System.Runtime.InteropServices;
using Overlyric.Interop;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Media;

/// <summary>
/// Answers the one question GSMTC cannot: is any application producing sound right now?
/// <para>
/// This is diagnosis, not playback. When nothing publishes to Windows media controls there are two
/// very different situations - nothing is playing, or something is playing through a player that
/// never registers - and only the audio engine can tell them apart. Without that, Overlyric can only
/// sit silent and leave the user guessing why its overlay never appears.
/// </para>
/// </summary>
/// <remarks>
/// CoreAudio's session interfaces, driven through raw vtable calls rather than <c>[ComImport]</c>:
/// NativeAOT carries no built-in COM support unless it is switched on, and switching it on to read
/// one peak value would cost more than this costs. Every vtable slot is reached through a blittable
/// delegate, so the calls themselves stay in safe code. The shape is the same as the rest of
/// <c>Interop\NativeMethods.cs</c> - a handful of entry points, nothing else.
/// </remarks>
internal static class AudioSessions
{
    /// <summary>
    /// Above this peak a session counts as audible. Digital silence reads exactly zero, so the bar
    /// only has to clear rounding noise; anything louder would start missing quiet playback.
    /// </summary>
    private const float AudiblePeak = 0.0005f;

    // COM vtable slots. Every interface begins with IUnknown: QueryInterface 0, AddRef 1, Release 2.
    private const int QueryInterfaceSlot = 0;
    private const int GetPeakValueSlot = 3;           // IAudioMeterInformation
    private const int ActivateSlot = 3;               // IMMDevice
    private const int GetSessionCountSlot = 3;        // IAudioSessionEnumerator
    private const int GetSessionSlot = 4;             // IAudioSessionEnumerator
    private const int GetDefaultEndpointSlot = 4;     // IMMDeviceEnumerator
    private const int GetSessionEnumeratorSlot = 5;   // IAudioSessionManager2
    private const int IsSystemSoundsSessionSlot = 15; // IAudioSessionControl2

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidIAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IidIAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid IidIAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    /// <summary>
    /// True when at least one application is audibly playing to the default output device. Fails
    /// soft: any COM failure answers false, because a missing warning is better than a wrong one.
    /// </summary>
    /// <summary>The default render endpoint, eRender/eMultimedia.</summary>
    private const int ERender = 0;
    private const int EMultimedia = 1;

    /// <summary>The default capture endpoint used for calls, eCapture/eCommunications.</summary>
    private const int ECapture = 1;
    private const int ECommunications = 2;

    public static bool IsAnythingAudible() => AnyActive(ERender, EMultimedia);

    /// <summary>
    /// True when an application is actually capturing audio from the default communications
    /// microphone: an active non-system session with a non-zero capture peak. A call that is joined
    /// and muted holds a session but captures nothing, so the peak is what separates the two.
    /// </summary>
    public static bool IsMicrophoneCapturing() => AnyActive(ECapture, ECommunications);

    private static bool AnyActive(int dataFlow, int role)
    {
        if (CoCreateInstance(ClsidMMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, IidIMMDeviceEnumerator, out var enumerator) < 0)
        {
            return false;
        }

        try
        {
            return AnySessionAudible(enumerator, dataFlow, role);
        }
        catch (Exception)
        {
            // A device can disappear mid-enumeration; that is not worth reporting.
            return false;
        }
        finally
        {
            ComVtable.Release(enumerator);
        }
    }

    private static bool AnySessionAudible(nint enumerator, int dataFlow, int role)
    {
        var getDefaultEndpoint = ComVtable.Slot<GetDefaultEndpointDelegate>(enumerator, GetDefaultEndpointSlot);

        if (getDefaultEndpoint(enumerator, dataFlow, role, out var device) < 0)
        {
            return false;
        }

        try
        {
            return AnySessionAudibleOn(device);
        }
        finally
        {
            ComVtable.Release(device);
        }
    }

    private static bool AnySessionAudibleOn(nint device)
    {
        var activate = ComVtable.Slot<ActivateDelegate>(device, ActivateSlot);

        if (activate(device, IidIAudioSessionManager2, CLSCTX_ALL, IntPtr.Zero, out var manager) < 0)
        {
            return false;
        }

        try
        {
            return AnySessionAudibleUnder(manager);
        }
        finally
        {
            ComVtable.Release(manager);
        }
    }

    private static bool AnySessionAudibleUnder(nint manager)
    {
        var getSessionEnumerator = ComVtable.Slot<GetSessionEnumeratorDelegate>(manager, GetSessionEnumeratorSlot);

        if (getSessionEnumerator(manager, out var sessions) < 0)
        {
            return false;
        }

        try
        {
            var getCount = ComVtable.Slot<GetSessionCountDelegate>(sessions, GetSessionCountSlot);
            var getSession = ComVtable.Slot<GetSessionDelegate>(sessions, GetSessionSlot);

            if (getCount(sessions, out var count) < 0)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                if (getSession(sessions, i, out var control) < 0)
                {
                    continue;
                }

                try
                {
                    if (IsAudible(control))
                    {
                        return true;
                    }
                }
                finally
                {
                    ComVtable.Release(control);
                }
            }

            return false;
        }
        finally
        {
            ComVtable.Release(sessions);
        }
    }

    private static bool IsAudible(nint control)
    {
        var queryInterface = ComVtable.Slot<QueryInterfaceDelegate>(control, QueryInterfaceSlot);

        // Windows' own beeps and chimes are not a player, and would otherwise always count.
        if (IsSystemSounds(control, queryInterface))
        {
            return false;
        }

        // The meter hangs off the session control, and the peak is what separates "making noise"
        // from "holding a stream open" - a game sitting in a menu keeps a session alive for hours.
        if (queryInterface(control, IidIAudioMeterInformation, out var meter) < 0)
        {
            return false;
        }

        try
        {
            var getPeakValue = ComVtable.Slot<GetPeakValueDelegate>(meter, GetPeakValueSlot);

            return getPeakValue(meter, out var peak) >= 0 && peak > AudiblePeak;
        }
        finally
        {
            ComVtable.Release(meter);
        }
    }

    private static bool IsSystemSounds(nint control, QueryInterfaceDelegate queryInterface)
    {
        if (queryInterface(control, IidIAudioSessionControl2, out var control2) < 0)
        {
            return false;
        }

        try
        {
            var isSystemSoundsSession = ComVtable.Slot<IsSystemSoundsDelegate>(control2, IsSystemSoundsSessionSlot);

            // S_OK means "yes, this is the system sounds session".
            return isSystemSoundsSession(control2) == 0;
        }
        finally
        {
            ComVtable.Release(control2);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(nint self, in Guid iid, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDefaultEndpointDelegate(nint self, int dataFlow, int role, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateDelegate(nint self, in Guid iid, uint classContext, nint outer, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionEnumeratorDelegate(nint self, out nint enumerator);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionCountDelegate(nint self, out int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionDelegate(nint self, int index, out nint control);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPeakValueDelegate(nint self, out float peak);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsSystemSoundsDelegate(nint self);
}
