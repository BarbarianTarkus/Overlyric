using System.Runtime.InteropServices;

namespace Overlyric.Interop;

/// <summary>
/// Raw COM vtable access for the few CoreAudio interfaces the app reads. NativeAOT carries no
/// built-in COM support unless it is switched on, and switching it on to read a device name would
/// cost more than these few lines do.
/// </summary>
/// <remarks>
/// Every interface begins with <c>IUnknown</c>: QueryInterface 0, AddRef 1, Release 2, and the
/// interface's own methods from slot 3. A callable delegate per slot keeps the calls in safe code.
/// </remarks>
internal static class ComVtable
{
    private const int ReleaseSlot = 2;

    /// <summary>Reads one slot out of an interface's vtable as a callable delegate.</summary>
    public static T Slot<T>(nint instance, int slot)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    /// <summary>Releases one interface pointer; a null pointer is ignored.</summary>
    public static void Release(nint unknown)
    {
        if (unknown != IntPtr.Zero)
        {
            Slot<ReleaseDelegate>(unknown, ReleaseSlot)(unknown);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(nint self);
}
