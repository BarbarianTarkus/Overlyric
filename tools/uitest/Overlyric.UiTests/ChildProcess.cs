using System.Diagnostics;

namespace Overlyric.UiTests;

/// <summary>
/// One process the suite started. It owns the only handle to it and is the only thing allowed to
/// kill it: the suite never enumerates processes by name, so it can never touch the user's copy.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    private nint _handle;
    private bool _exited;

    private ChildProcess(uint pid, nint handle)
    {
        Pid = (int)pid;
        _handle = handle;
    }

    public int Pid { get; }

    /// <summary>The process handle, so a job object can adopt it. Never closed by the caller.</summary>
    public nint NativeHandle => _handle;

    public bool HasExited
    {
        get
        {
            if (_exited)
            {
                return true;
            }

            if (NativeMethods.WaitForSingleObject(_handle, 0) != 0)
            {
                return false;
            }

            _exited = true;
            return true;
        }
    }

    public static ChildProcess Start(
        string exePath,
        IReadOnlyList<string> arguments,
        string desktopName,
        IReadOnlyDictionary<string, string>? environment,
        string? workingDirectory)
    {
        var startup = new NativeMethods.STARTUPINFO
        {
            cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.STARTUPINFO>(),

            // An empty name keeps the process on the desktop it is started from. The parent only
            // passes one when it created a private desktop; --visible deliberately does not.
            lpDesktop = string.IsNullOrEmpty(desktopName) ? null : desktopName
        };

        var commandLine = ProcessLauncher.BuildCommandLine(exePath, arguments);
        var environmentBlock = environment is null ? nint.Zero : ProcessLauncher.BuildEnvironmentBlock(environment);

        try
        {
            var flags = environmentBlock == nint.Zero ? 0u : NativeMethods.CREATE_UNICODE_ENVIRONMENT;

            if (!NativeMethods.CreateProcessW(
                    exePath,
                    commandLine,
                    nint.Zero,
                    nint.Zero,
                    false,
                    flags,
                    environmentBlock,
                    workingDirectory,
                    ref startup,
                    out var info))
            {
                throw new InvalidOperationException(
                    $"CreateProcessW('{exePath}') failed with Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
            }

            NativeMethods.CloseHandle(info.hThread);
            return new ChildProcess(info.dwProcessId, info.hProcess);
        }
        finally
        {
            if (environmentBlock != nint.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    /// <summary>Waits for exit; true when it exited inside the budget.</summary>
    public bool WaitForExit(int timeoutMs) => HasExited || NativeMethods.WaitForSingleObject(_handle, (uint)timeoutMs) == 0;

    public int ExitCode
    {
        get
        {
            if (!NativeMethods.GetExitCodeProcess(_handle, out var code))
            {
                return -1;
            }

            return unchecked((int)code);
        }
    }

    /// <summary>
    /// Terminates this process and every descendant it started. The tree is walked from the PID this
    /// object owns, so the blast radius is exactly the processes the suite launched: a user-run
    /// Overlyric is a different root and is never reached.
    /// </summary>
    public void KillTree()
    {
        if (HasExited)
        {
            return;
        }

        ProcessLauncher.KillTree(Pid);
        WaitForExit(5_000);
    }

    public void Dispose()
    {
        if (_handle != nint.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = nint.Zero;
        }
    }

    /// <summary>Formats a start failure for a result message.</summary>
    public string Describe() => $"pid {Pid}, exited {HasExited}, exit code {(HasExited ? ExitCode : 0)}";
}

internal static class ProcessLauncher
{
    /// <summary>
    /// Quotes one argument for the MS C runtime command line grammar that CreateProcessW accepts.
    /// The application path is quoted unconditionally because it routinely contains spaces.
    /// </summary>
    public static string BuildCommandLine(string exePath, IReadOnlyList<string> arguments)
    {
        var builder = new System.Text.StringBuilder(Quote(exePath));

        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append('"');

        for (var i = 0; i < value.Length; i++)
        {
            var backslashes = 0;

            while (i < value.Length && value[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == value.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (value[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(value[i]);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// An explicit environment for the app: the suite's own environment with the three test seams
    /// replaced. Sending it explicitly rather than mutating this process keeps one scenario's
    /// override from leaking into the next one.
    /// </summary>
    public static nint BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                values[key] = value;
            }
        }

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        var builder = new System.Text.StringBuilder();

        foreach (var (key, value) in values)
        {
            builder.Append(key).Append('=').Append(value).Append('\0');
        }

        builder.Append('\0');

        var bytes = System.Text.Encoding.Unicode.GetBytes(builder.ToString());
        var block = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, block, bytes.Length);
        return block;
    }

    /// <summary>
    /// Kills a process tree rooted at <paramref name="rootPid"/>. The root is signalled first so it
    /// stops spawning, then every descendant found in the current snapshot.
    /// </summary>
    public static void KillTree(int rootPid)
    {
        var children = ReadChildren();

        if (children.Count == 0)
        {
            Terminate(rootPid);
            return;
        }

        Terminate(rootPid);

        var queue = new Queue<int>();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();

            foreach (var child in children.Where(pair => pair.Value == parent).Select(pair => pair.Key))
            {
                Terminate(child);
                queue.Enqueue(child);
            }
        }
    }

    /// <summary>
    /// Kills processes that still name the worker as their parent and are the application under
    /// test. This is the safety net for a worker that died without disposing its scenario: the app
    /// it started would otherwise keep running, invisible, on a desktop that no longer exists.
    /// Matching the executable name as well as the parent keeps the sweep from touching anything
    /// the suite did not start.
    /// </summary>
    public static void KillOrphanedChildren(int workerPid, string expectedExecutableName)
    {
        if (workerPid == 0 || expectedExecutableName.Length == 0)
        {
            return;
        }

        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);

        if (snapshot == nint.Zero || snapshot == -1)
        {
            return;
        }

        var orphans = new List<int>();

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>()
            };

            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return;
            }

            do
            {
                if ((int)entry.th32ParentProcessID == workerPid
                    && string.Equals(entry.szExeFile, expectedExecutableName, StringComparison.OrdinalIgnoreCase))
                {
                    orphans.Add((int)entry.th32ProcessID);
                }
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        foreach (var pid in orphans)
        {
            Terminate(pid);
        }
    }

    private static Dictionary<int, int> ReadChildren()
    {
        var map = new Dictionary<int, int>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);

        if (snapshot == nint.Zero || snapshot == -1)
        {
            return map;
        }

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>()
            };

            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                var pid = (int)entry.th32ProcessID;
                var parent = (int)entry.th32ParentProcessID;

                if (pid > 0)
                {
                    map[pid] = parent;
                }
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return map;
    }

    private static void Terminate(int pid)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_TERMINATE | NativeMethods.PROCESS_SYNCHRONIZE,
            false,
            (uint)pid);

        if (handle == nint.Zero)
        {
            // A process that is already gone is the normal case for the second half of a tree walk.
            return;
        }

        try
        {
            NativeMethods.TerminateProcess(handle, 1);
            NativeMethods.WaitForSingleObject(handle, 3_000);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
