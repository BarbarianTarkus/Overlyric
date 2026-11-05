using System.Runtime.InteropServices;

namespace Overlyric.UiTests;

/// <summary>
/// A kill-on-close job object around the worker and everything it starts.
/// </summary>
/// <remarks>
/// The suite already kills its own process tree after every scenario, but that code runs in the
/// parent: if the parent is itself killed (a stopped script, a closed terminal, a CI cancellation),
/// nothing is left to clean up, and the invisible test copy of the application would keep running
/// on a desktop that no longer exists. A job object with KILL_ON_JOB_CLOSE makes the kernel do the
/// cleanup when the parent's handle disappears, so the user's machine cannot accumulate leftover
/// test instances. It only ever contains processes this run started.
/// </remarks>
internal sealed class ProcessJob : IDisposable
{
    private nint _handle;

    private ProcessJob(nint handle)
    {
        _handle = handle;
    }

    public bool IsAvailable => _handle != nint.Zero;

    /// <summary>Creates the job, or null when the host does not allow one (a nested-job sandbox).</summary>
    public static ProcessJob? TryCreate()
    {
        var handle = NativeMethods.CreateJobObjectW(nint.Zero, null);

        if (handle == nint.Zero)
        {
            return null;
        }

        var information = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();

        if (!NativeMethods.SetInformationJobObject(
                handle,
                NativeMethods.JobObjectExtendedLimitInformation,
                ref information,
                size))
        {
            NativeMethods.CloseHandle(handle);
            return null;
        }

        return new ProcessJob(handle);
    }

    /// <summary>Puts a process in the job; false when the host's job object refuses the nesting.</summary>
    public bool TryAdd(ChildProcess process) => NativeMethods.AssignProcessToJobObject(_handle, process.NativeHandle);

    public void Dispose()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        // Closing the last handle triggers the kernel kill, which is the point of the job.
        NativeMethods.CloseHandle(_handle);
        _handle = nint.Zero;
    }
}
