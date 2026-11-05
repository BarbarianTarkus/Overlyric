using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Overlyric.UiTests;

/// <summary>
/// One application instance started for one scenario: the isolated test namespace, the fixture
/// config written before launch, the process on the private desktop, and the window it opened.
/// </summary>
/// <remarks>
/// Every environment value the app reads here is a test seam it already honours: the instance name
/// moves the mutex, the command pipe and the data folder into a namespace of their own, the data
/// root keeps that folder inside the repository instead of the user's profile, and the DPI override
/// makes the layout land at 96 DPI. Nothing in this class ever writes to the production namespace.
/// </remarks>
internal sealed class AppSession : IDisposable
{
    private readonly ChildProcess _process;

    private AppSession(
        ChildProcess process,
        string instanceName,
        string dataRoot,
        string dataDirectory,
        string pipeName)
    {
        _process = process;
        InstanceName = instanceName;
        DataRoot = dataRoot;
        DataDirectory = dataDirectory;
        PipeName = pipeName;
    }

    public string InstanceName { get; }

    /// <summary>The <c>OVERLYRIC_TEST_DATA_ROOT</c> handed to the app.</summary>
    public string DataRoot { get; }

    /// <summary>Where the app actually keeps its files; it appends its own folder below the root.</summary>
    public string DataDirectory { get; }

    public string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public string PipeName { get; }

    public nint Window { get; private set; }

    public string WindowClass { get; private set; } = string.Empty;

    /// <summary>What the OS says the monitor DPI is; informational only, never asserted on.</summary>
    public uint OsWindowDpi { get; private set; }

    public int Pid => _process.Pid;

    public static AppSession Launch(
        Options options,
        string instanceName,
        string dataRoot,
        bool demo,
        string windowClass,
        int windowTimeoutMs = 30_000)
    {
        // InstanceScope.TryResolve builds <data root>\Overlyric-Test\<name>; mirroring it here is
        // what lets the suite write the fixture config where the app will read it.
        var dataDirectory = Path.Combine(dataRoot, "Overlyric-Test", instanceName);
        var pipeName = "Overlyric.Commands.v1." + instanceName;

        FixtureFile.WriteConfig(options, Path.Combine(dataDirectory, "config.json"));

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OVERLYRIC_TEST_INSTANCE"] = instanceName,
            ["OVERLYRIC_TEST_DATA_ROOT"] = dataRoot,
            ["OVERLYRIC_TEST_DPI"] = "96"
        };

        var arguments = demo ? new[] { "--demo" } : new[] { "--settings" };
        var process = ChildProcess.Start(
            options.ExePath,
            arguments,
            options.DesktopName,
            environment,
            Path.GetDirectoryName(options.ExePath));

        var session = new AppSession(process, instanceName, dataRoot, dataDirectory, pipeName);

        try
        {
            session.Window = session.WaitForWindow(windowClass, windowTimeoutMs);
            session.WindowClass = windowClass;
            session.OsWindowDpi = NativeMethods.GetDpiForWindow(session.Window);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public (int Width, int Height) ClientSize()
    {
        if (!NativeMethods.GetClientRect(Window, out var rect))
        {
            throw new ScenarioFailureException("GetClientRect failed: the window handle is no longer valid.");
        }

        return (rect.Width, rect.Height);
    }

    public (int Width, int Height) WindowSize()
    {
        if (!NativeMethods.GetWindowRect(Window, out var rect))
        {
            throw new ScenarioFailureException("GetWindowRect failed: the window handle is no longer valid.");
        }

        return (rect.Width, rect.Height);
    }

    public bool IsVisible() => Window != nint.Zero && NativeMethods.IsWindowVisible(Window);

    public bool IsAlive() => Window != nint.Zero && NativeMethods.IsWindow(Window);

    public long ExtendedStyle() => NativeMethods.GetWindowLongPtrW(Window, NativeMethods.GWL_EXSTYLE).ToInt64();

    /// <summary>
    /// Posts a left click at a client coordinate. The coordinate is DIP, which is also the client
    /// pixel under the 96 DPI seam. PostMessage is the whole point: it feeds the window's own message
    /// queue without moving the cursor, changing the focus, or asking the input system for anything.
    /// </summary>
    public void Click(int dipX, int dipY)
    {
        PostMouse(NativeMethods.WM_MOUSEMOVE, 0, dipX, dipY);
        Thread.Sleep(20);
        PostMouse(NativeMethods.WM_LBUTTONDOWN, NativeMethods.MK_LBUTTON, dipX, dipY);
        Thread.Sleep(40);
        PostMouse(NativeMethods.WM_LBUTTONUP, 0, dipX, dipY);
    }

    /// <summary>Posts one key press and release to this window's queue.</summary>
    public void Key(uint virtualKey)
    {
        if (!NativeMethods.PostMessageW(Window, NativeMethods.WM_KEYDOWN, new nint(virtualKey), new nint(0x00010001)))
        {
            throw new ScenarioFailureException($"PostMessage(WM_KEYDOWN, 0x{virtualKey:X2}) failed.");
        }

        Thread.Sleep(30);
        NativeMethods.PostMessageW(Window, NativeMethods.WM_KEYUP, new nint(virtualKey), unchecked((nint)0xC0010001L));
    }

    /// <summary>
    /// Sends one command line to the instance's own command pipe. Returns null on success, or a
    /// description of why it could not be sent: the caller decides whether that is a failure or an
    /// environment that blocks named pipes, and the two must not be conflated.
    /// </summary>
    public string? TrySendCommand(string line)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(2_000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(line);
            return null;
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    /// <summary>
    /// Posts a command id to the application's message-only window, the same route a registered
    /// global hotkey takes. It is the fallback for environments that do not let one process connect
    /// to another's named pipe: the layout switch is still exercised through the application's own
    /// command dispatch instead of through its transport.
    /// </summary>
    public void PostCommand(int command)
    {
        var window = FindWindow(_process.Pid, "OverlyricMessageWindow", requireVisible: false);

        if (window == nint.Zero)
        {
            throw new ScenarioFailureException(
                "Could not find the application's OverlyricMessageWindow to post the command to.");
        }

        if (!NativeMethods.PostMessageW(window, NativeMethods.WM_HOTKEY, new nint(command), nint.Zero))
        {
            throw new ScenarioFailureException(
                $"PostMessage(WM_HOTKEY, {command}) to the message window failed.");
        }
    }

    private void PostMouse(uint message, uint wParam, int x, int y)
    {
        var lParam = new nint((y << 16) | (x & 0xFFFF));

        if (!NativeMethods.PostMessageW(Window, message, new nint(wParam), lParam))
        {
            throw new ScenarioFailureException(
                $"PostMessage(0x{message:X4}, {x},{y}) failed: the window no longer accepts messages.");
        }
    }

    private nint WaitForWindow(string className, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (_process.HasExited)
            {
                throw new ScenarioFailureException(
                    $"The application exited during startup with code {_process.ExitCode}.{Environment.NewLine}{DiagnosticsTail()}");
            }

            var window = FindWindow(_process.Pid, className, requireVisible: true);

            if (window != nint.Zero)
            {
                return window;
            }

            Thread.Sleep(100);
        }

        throw new ScenarioFailureException(
            $"No visible window of class '{className}' appeared within {timeoutMs / 1000}s.{Environment.NewLine}{DiagnosticsTail()}");
    }

    private static nint FindWindow(int pid, string className, bool requireVisible)
    {
        var found = nint.Zero;

        NativeMethods.EnumWindows(
            (window, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(window, out var owner);

                if (owner != (uint)pid)
                {
                    return true;
                }

                var buffer = new StringBuilder(256);
                NativeMethods.GetClassNameW(window, buffer, buffer.Capacity);

                if (buffer.ToString() != className)
                {
                    return true;
                }

                if (requireVisible && !NativeMethods.IsWindowVisible(window))
                {
                    return true;
                }

                found = window;
                return false;
            },
            nint.Zero);

        return found;
    }

    /// <summary>The last lines of the instance's own diagnostic log, for a failed launch message.</summary>
    private string DiagnosticsTail()
    {
        var logPath = Path.Combine(DataDirectory, "diagnostics.log");

        try
        {
            if (!File.Exists(logPath))
            {
                return $"No diagnostics log was written to '{DataDirectory}'.";
            }

            var lines = File.ReadAllLines(logPath);
            var tail = lines.Length <= 12 ? lines : lines[^12..];
            return "diagnostics.log:" + Environment.NewLine + string.Join(Environment.NewLine, tail);
        }
        catch (Exception exception)
        {
            return $"Could not read the diagnostics log: {exception.Message}";
        }
    }

    public void Dispose()
    {
        _process.KillTree();
        _process.Dispose();
        Window = nint.Zero;
    }

    /// <summary>What the OS reports as DPI, which is deliberately not what the app draws at.</summary>
    public string DescribeDpi() =>
        $"OS DPI {OsWindowDpi}; client {ClientSize().Width}x{ClientSize().Height} px (the seam keeps the layout at 96 DPI)";
}
