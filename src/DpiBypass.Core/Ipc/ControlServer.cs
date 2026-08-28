using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DpiBypass.Core.Ipc;

/// <summary>
/// Lets a command line invocation drive the instance that owns the engine.
/// </summary>
/// <remarks>
/// The engine is a system-wide packet filter living in one elevated process, so
/// "dpibypass --status" cannot answer for itself: a second process knows the
/// settings on disk but not what the running one has measured or chosen. This is
/// the same split the Linux build solves with a unix socket, done here with a named
/// pipe. One client at a time is plenty for a command line.
/// </remarks>
public sealed class ControlServer : IAsyncDisposable
{
    private readonly Func<ControlRequest, Task<ControlResponse>> _handler;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public ControlServer(Func<ControlRequest, Task<ControlResponse>> handler, Action<string>? log = null)
    {
        _handler = handler;
        _log = log;
    }

    public void Start()
    {
        if (_loop is { IsCompleted: false })
        {
            return;
        }

        _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    ControlProtocol.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                _log?.Invoke("Control channel request timed out.");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Control channel error: {ex.Message}");

                // Never spin: a pipe that cannot be created would otherwise burn a core.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ControlProtocol.RequestTimeout);
        var token = timeout.Token;

        var line = await ReadLineBoundedAsync(pipe, ControlProtocol.MaxRequestBytes, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        ControlResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<ControlRequest>(line, ControlProtocol.Json);
            response = request is null
                ? ControlResponse.Failure("boş istek")
                : await _handler(request).WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = ControlResponse.Failure(ex.Message);
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, ControlProtocol.Json) + "\n");
        await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        using var memory = new MemoryStream();

        while (memory.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;
            memory.Write(buffer, 0, count);
            if (newline >= 0)
            {
                break;
            }
        }

        if (memory.Length == 0 || memory.Length >= maxBytes)
        {
            return null;
        }

        return Encoding.UTF8.GetString(memory.ToArray()).TrimEnd('\r');
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        // Unblock WaitForConnectionAsync, which does not always observe cancellation
        // until something touches the pipe.
        try
        {
            await using var nudge = new NamedPipeClientStream(".", ControlProtocol.PipeName, PipeDirection.InOut);
            await nudge.ConnectAsync(200).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing listening; that is the outcome we wanted anyway.
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        _stopping.Dispose();
    }
}

/// <summary>The command line side of <see cref="ControlServer"/>.</summary>
public static class ControlClient
{
    /// <summary>
    /// The longest this waits for the pipe itself, however long the command is
    /// allowed to take.
    /// </summary>
    /// <remarks>
    /// Connecting is never the slow part: a running instance keeps a server instance
    /// waiting, so it accepts in milliseconds. The command timeout is about how long
    /// the answer may take - a re-tune runs real handshakes - and spending it on the
    /// connection as well means every command sent when nothing is listening blocks
    /// for the whole of it. That is not a hypothetical: the uninstaller restores the
    /// NIC settings through this channel after the app has already been stopped, so
    /// each removal, and therefore each update, sat for the best part of a minute
    /// waiting for a process that had been asked to exit. Bounding the connection on
    /// its own keeps "nobody is home" quick without shortening any command.
    /// </remarks>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Sends one command. Returns null when no instance is running.</summary>
    public static async Task<ControlResponse?> SendAsync(
        ControlRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var requestTimeout = timeout ?? TimeSpan.FromSeconds(3);
            var connectTimeout = requestTimeout < ConnectTimeout ? requestTimeout : ConnectTimeout;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(requestTimeout);
            var token = deadline.Token;

            await using var pipe = new NamedPipeClientStream(".", ControlProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, token).ConfigureAwait(false);

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ControlProtocol.Json).AsMemory(), token).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            return line is null ? null : JsonSerializer.Deserialize<ControlResponse>(line, ControlProtocol.Json);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
