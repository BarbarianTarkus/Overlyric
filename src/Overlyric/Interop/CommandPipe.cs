using System.IO.Pipes;

namespace Overlyric.Interop;

/// <summary>
/// The named pipe a second instance uses to hand a command to the one that owns playback.
/// </summary>
/// <remarks>
/// One short line per connection, one connection at a time. The server loop is restarted after every
/// command and after any failure, because the pipe existing is the only signal a second instance has
/// that the first one is alive; a send that fails simply means "not running", and the caller then
/// starts normally.
/// </remarks>
internal static class CommandPipe
{
    public const string Name = "Overlyric.Commands.v1";

    /// <summary>Sends one line to a running instance; false when nothing is listening in time.</summary>
    public static bool TrySend(string line, int timeoutMs = 1000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.None);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(line);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Listens in the background until cancelled; each line is handed to the callback.</summary>
    public static void Start(Action<string> onLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        _ = Task.Run(
            async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await using var server = new NamedPipeServerStream(
                            Name,
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                        using var reader = new StreamReader(server);
                        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            onLine(line);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // A client that connected and vanished must not kill the listener.
                        try
                        {
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            },
            cancellationToken);
    }
}
