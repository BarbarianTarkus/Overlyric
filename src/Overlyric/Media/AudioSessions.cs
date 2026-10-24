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
/// one peak value would cost more than this costs. The call is deliberately shaped like the rest of
/// <c>Interop\NativeMethods.cs</c> - a handful of entry points, nothing else.
/// </remarks>
internal static unsafe class AudioSessions
{
    /// <summary>
    /// Above this peak a session counts as audible. Digital silence reads exactly zero, so the bar
    /// only has to clear rounding noise; anything louder would start missing quiet playback.
    /// </summary>
    private const float AudiblePeak = 0.0005f;

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
        var enumeratorVtbl = *(nint**)enumerator;
        var getDefaultEndpoint = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)enumeratorVtbl[4];

        nint device;
        // eRender, eMultimedia: the device Windows itself treats as "what you hear".
        if (getDefaultEndpoint(enumerator, 0, 1, &device) < 0)
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
        var deviceVtbl = *(nint**)device;
        var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)deviceVtbl[3];

        var managerIid = IidIAudioSessionManager2;
        nint manager;
        if (activate(device, &managerIid, CLSCTX_ALL, IntPtr.Zero, &manager) < 0)
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
        var managerVtbl = *(nint**)manager;
        var getSessionEnumerator = (delegate* unmanaged[Stdcall]<nint, nint*, int>)managerVtbl[5];

        nint sessions;
        if (getSessionEnumerator(manager, &sessions) < 0)
        {
            return false;
        }

        try
        {
            var sessionsVtbl = *(nint**)sessions;
            var getCount = (delegate* unmanaged[Stdcall]<nint, int*, int>)sessionsVtbl[3];
            var getSession = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)sessionsVtbl[4];

            int count;
            if (getCount(sessions, &count) < 0)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                nint control;
                if (getSession(sessions, i, &control) < 0)
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
        var vtbl = *(nint**)control;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtbl[0];

        // Windows' own beeps and chimes are not a player, and would otherwise always count.
        if (IsSystemSounds(control, queryInterface))
        {
            return false;
        }

        // The meter hangs off the session control, and the peak is what separates "making noise"
        // from "holding a stream open" - a game sitting in a menu keeps a session alive for hours.
        var meterIid = IidIAudioMeterInformation;
        nint meter;
        if (queryInterface(control, &meterIid, &meter) < 0)
        {
            return false;
        }

        try
        {
            var meterVtbl = *(nint**)meter;
            var getPeakValue = (delegate* unmanaged[Stdcall]<nint, float*, int>)meterVtbl[3];

            float peak;
            return getPeakValue(meter, &peak) >= 0 && peak > AudiblePeak;
        }
        finally
        {
            Release(meter);
        }
    }

    private static bool IsSystemSounds(
        nint control,
        delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int> queryInterface)
    {
        var control2Iid = IidIAudioSessionControl2;
        nint control2;
        if (queryInterface(control, &control2Iid, &control2) < 0)
        {
            return false;
        }

        try
        {
            var vtbl = *(nint**)control2;
            var isSystemSoundsSession = (delegate* unmanaged[Stdcall]<nint, int>)vtbl[15];

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
            var vtbl = *(nint**)unknown;
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(unknown);
        }
    }
}
