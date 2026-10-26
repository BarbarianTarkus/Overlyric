using System.Runtime.InteropServices;
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
    private const int ReleaseSlot = 2;
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
    public static bool IsAnythingAudible()
    {
        if (CoCreateInstance(ClsidMMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, IidIMMDeviceEnumerator, out var enumerator) < 0)
        {
            return false;
        }

        try
        {
            return AnySessionAudible(enumerator);
        }
        catch (Exception)
        {
            // A device can disappear mid-enumeration; that is not worth reporting.
            return false;
        }
        finally
        {
            Release(enumerator);
        }
    }

    private static bool AnySessionAudible(nint enumerator)
    {
        var getDefaultEndpoint = Slot<GetDefaultEndpointDelegate>(enumerator, GetDefaultEndpointSlot);

        // eRender, eMultimedia: the device Windows itself treats as "what you hear".
        if (getDefaultEndpoint(enumerator, 0, 1, out var device) < 0)
        {
            return false;
        }

        try
        {
            return AnySessionAudibleOn(device);
        }
        finally
        {
            Release(device);
        }
    }

    private static bool AnySessionAudibleOn(nint device)
    {
        var activate = Slot<ActivateDelegate>(device, ActivateSlot);

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
            Release(manager);
        }
    }

    private static bool AnySessionAudibleUnder(nint manager)
    {
        var getSessionEnumerator = Slot<GetSessionEnumeratorDelegate>(manager, GetSessionEnumeratorSlot);

        if (getSessionEnumerator(manager, out var sessions) < 0)
        {
            return false;
        }

        try
        {
            var getCount = Slot<GetSessionCountDelegate>(sessions, GetSessionCountSlot);
            var getSession = Slot<GetSessionDelegate>(sessions, GetSessionSlot);

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
                    Release(control);
                }
            }

            return false;
        }
        finally
        {
            Release(sessions);
        }
    }

    private static bool IsAudible(nint control)
    {
        var queryInterface = Slot<QueryInterfaceDelegate>(control, QueryInterfaceSlot);

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
            var getPeakValue = Slot<GetPeakValueDelegate>(meter, GetPeakValueSlot);

            return getPeakValue(meter, out var peak) >= 0 && peak > AudiblePeak;
        }
        finally
        {
            Release(meter);
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
            var isSystemSoundsSession = Slot<IsSystemSoundsDelegate>(control2, IsSystemSoundsSessionSlot);

            // S_OK means "yes, this is the system sounds session".
            return isSystemSoundsSession(control2) == 0;
        }
        finally
        {
            Release(control2);
        }
    }

    private static void Release(nint unknown)
    {
        if (unknown != IntPtr.Zero)
        {
            Slot<ReleaseDelegate>(unknown, ReleaseSlot)(unknown);
        }
    }

    /// <summary>Reads one slot out of an interface's vtable as a callable delegate.</summary>
    private static T Slot<T>(nint instance, int slot)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(nint self, in Guid iid, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(nint self);

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
