using System.Runtime.InteropServices;

namespace Overlyric.UiTests;

/// <summary>
/// A desktop of the suite's own. Windows draws and queues input per desktop, so a window created on
/// this one cannot be covered by the user's windows, cannot take their focus, and cannot be
/// interrupted by their mouse or keyboard. It also isolates the app under test: the production
/// Overlyric instance lives on the default desktop and never sees these windows.
/// </summary>
/// <remarks>
/// The handle belongs to the process that created it. The parent creates the desktop and keeps it
/// open for exactly as long as the worker runs; the worker and the app it starts only know the name,
/// which they pass through <c>STARTUPINFO.lpDesktop</c>. When the parent closes the handle the
/// desktop is destroyed, so a crashed run cannot leave one behind.
/// </remarks>
internal sealed class PrivateDesktop : IDisposable
{
    private nint _handle;

    private PrivateDesktop(string name, nint handle)
    {
        Name = name;
        _handle = handle;
    }

    public string Name { get; }

    public static PrivateDesktop Create(string name)
    {
        // GENERIC_ALL is what the spike used: the desktop must let its processes create windows,
        // post to them and read their pixels. It is a private object owned by this process, not a
        // shared desktop, so the access does not widen anything the user already has.
        var handle = NativeMethods.CreateDesktopW(name, null, nint.Zero, 0, NativeMethods.GENERIC_ALL, nint.Zero);

        if (handle == nint.Zero)
        {
            throw new InvalidOperationException(
                $"CreateDesktopW('{name}') failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return new PrivateDesktop(name, handle);
    }

    public void Dispose()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        if (!NativeMethods.CloseDesktop(_handle))
        {
            // A desktop that still has processes on it refuses to close. The suite kills everything
            // it started before disposing, so this is worth recording but not worth failing over:
            // the handle is closed by the process teardown anyway.
            Console.Error.WriteLine(
                $"warning: CloseDesktop('{Name}') failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        _handle = nint.Zero;
    }
}
