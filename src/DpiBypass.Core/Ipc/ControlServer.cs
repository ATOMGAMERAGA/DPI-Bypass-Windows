using System.IO.Pipes;
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

    public void Start() => _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token));

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
            catch (OperationCanceledException)
            {
                return;
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
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
                : await _handler(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = ControlResponse.Failure(ex.Message);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, ControlProtocol.Json)).ConfigureAwait(false);

        // Let the client read the answer before the pipe goes away.
        try
        {
            pipe.WaitForPipeDrain();
        }
        catch (Exception)
        {
            // The client may already have gone.
        }
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
    /// <summary>Sends one command. Returns null when no instance is running.</summary>
    public static async Task<ControlResponse?> SendAsync(
        ControlRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", ControlProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds, cancellationToken).ConfigureAwait(false);

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ControlProtocol.Json)).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
    }
}
