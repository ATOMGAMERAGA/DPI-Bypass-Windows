using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>Thin owning wrapper around a WinDivert handle.</summary>
public sealed class WinDivertHandle : IDisposable
{
    private nint _handle;
    private volatile bool _shutdown;

    private WinDivertHandle(nint handle) => _handle = handle;

    public bool IsOpen => Current != nint.Zero;

    /// <summary>
    /// True once the handle has been told to stop, whether or not it has been closed.
    /// </summary>
    /// <remarks>
    /// <see cref="IsOpen"/> cannot answer this: shutting a handle down leaves the value
    /// valid until it is disposed, so a reader that has just come out of
    /// <see cref="Receive"/> cannot tell "the driver ended this" from "I fell over" by
    /// looking at the handle alone. That distinction is what decides whether a worker
    /// that returned is replaced or left to go.
    /// </remarks>
    public bool IsShutdown => _shutdown;

    /// <summary>
    /// The handle value, read once.
    /// </summary>
    /// <remarks>
    /// Dispose clears the field from another thread, so testing it and then passing it
    /// to the driver are two different answers. Every call reads it into a local first,
    /// which is what keeps a receive that raced a shutdown from handing the kernel a
    /// value that has changed underneath it.
    /// </remarks>
    private nint Current
    {
        get
        {
            var handle = Volatile.Read(ref _handle);
            return handle == WinDivertNative.InvalidHandle ? nint.Zero : handle;
        }
    }

    public static WinDivertHandle Open(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags)
    {
        var handle = WinDivertNative.Open(filter, layer, priority, flags);
        if (handle == WinDivertNative.InvalidHandle || handle == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new WinDivertException(error, DescribeOpenFailure(error));
        }

        return new WinDivertHandle(handle);
    }

    /// <summary>Validates a filter string without touching the driver.</summary>
    public static bool TryCompileFilter(string filter, WinDivertLayer layer, out string? error)
    {
        var buffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            if (WinDivertNative.CompileFilter(filter, layer, buffer, 64 * 1024, out var errorStr, out var errorPos))
            {
                error = null;
                return true;
            }

            var message = errorStr == nint.Zero ? "invalid filter" : Marshal.PtrToStringUTF8(errorStr) ?? "invalid filter";
            error = $"{message} (position {errorPos})";
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void SetParam(WinDivertParam param, ulong value)
    {
        if (!WinDivertNative.SetParam(Current, param, value))
        {
            throw new WinDivertException(Marshal.GetLastWin32Error(), $"WinDivertSetParam({param}) failed");
        }
    }

    /// <summary>Blocking receive. Returns false once the handle has been shut down.</summary>
    public bool Receive(Span<byte> buffer, out int length, ref WinDivertAddress addr)
    {
        length = 0;
        var handle = Current;
        if (handle == nint.Zero)
        {
            return false;
        }

        if (!WinDivertNative.Recv(handle, ref MemoryMarshal.GetReference(buffer), (uint)buffer.Length, out var received, ref addr))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_NO_DATA / ERROR_OPERATION_ABORTED / ERROR_INVALID_HANDLE all mean "we are done".
            if (error is 232 or 995 or 6 or 1168)
            {
                return false;
            }

            // ERROR_INSUFFICIENT_BUFFER: packet was truncated, skip it rather than dying.
            if (error == 122)
            {
                length = 0;
                return true;
            }

            throw new WinDivertException(error, "WinDivertRecv failed");
        }

        length = (int)received;
        return true;
    }

    public bool Send(ReadOnlySpan<byte> packet, ref WinDivertAddress addr)
        => Send(packet, ref addr, out _);

    /// <summary>
    /// Re-injects a packet, reporting why the driver refused it.
    /// </summary>
    /// <param name="error">
    /// The Win32 error the driver reported, or zero when the call succeeded. The caller
    /// needs it because the difference between "this one packet had nowhere to go" and
    /// "the handle is finished" is the difference between dropping a segment the sender
    /// will retransmit and taking protection off the machine for the rest of the session.
    /// </param>
    public bool Send(ReadOnlySpan<byte> packet, ref WinDivertAddress addr, out int error)
    {
        error = 0;
        var handle = Current;
        if (handle == nint.Zero)
        {
            error = InvalidHandleError;
            return false;
        }

        var buffer = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(packet), packet.Length);
        if (!WinDivertNative.Send(handle, ref MemoryMarshal.GetReference(buffer), (uint)packet.Length, out var sent, ref addr))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        if (sent == packet.Length)
        {
            return true;
        }

        // A short write is not an error the driver reports; it still means the packet
        // did not leave, and the caller has to treat it as a refusal.
        error = ShortWriteError;
        return false;
    }

    /// <summary>ERROR_INVALID_HANDLE, reported when the handle went away underneath us.</summary>
    public const int InvalidHandleError = 6;

    /// <summary>ERROR_PARTIAL_COPY, used here for a send the driver only half took.</summary>
    public const int ShortWriteError = 299;

    /// <summary>
    /// Errors that describe one packet rather than the handle.
    /// </summary>
    /// <remarks>
    /// All of these are ordinary on a machine whose network is moving: a route that has
    /// just gone (1231/1232), a driver briefly out of non-paged pool under a burst
    /// (1450/8), a send that raced the adapter coming back (21/1231). The packet is lost,
    /// the sender retransmits it, and the connection carries on. Releasing the filter
    /// over one of them is the outage - not the packet.
    /// </remarks>
    public static bool IsTransientSendError(int error) => error is 8 or 21 or 87 or 299 or 1231 or 1232 or 1450 or 1453;

    public static bool CalculateChecksums(Span<byte> packet, ref WinDivertAddress addr)
        => WinDivertNative.CalcChecksums(ref MemoryMarshal.GetReference(packet), (uint)packet.Length, ref addr, 0);

    public void Shutdown()
    {
        _shutdown = true;

        var handle = Current;
        if (handle != nint.Zero)
        {
            WinDivertNative.Shutdown(handle, WinDivertShutdown.Both);
        }
    }

    public void Dispose()
    {
        _shutdown = true;

        var handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero && handle != WinDivertNative.InvalidHandle)
        {
            WinDivertNative.Shutdown(handle, WinDivertShutdown.Both);
            WinDivertNative.Close(handle);
        }
    }

    private static string DescribeOpenFailure(int error) => error switch
    {
        5 => "Access denied - DPI Bypass must run as administrator.",
        2 => "WinDivert driver files are missing from the install folder.",
        87 => "The packet filter was rejected by the driver.",
        577 => "The WinDivert driver is not digitally signed for this system.",
        1275 => "The WinDivert driver was blocked by a software restriction policy.",
        1753 => "The base filtering engine service is not running.",
        _ => $"WinDivertOpen failed with Win32 error {error}.",
    };
}

public sealed class WinDivertException : Win32Exception
{
    public WinDivertException(int error, string message) : base(error, message)
    {
    }
}
