using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>Thin owning wrapper around a WinDivert handle.</summary>
public sealed class WinDivertHandle : IDisposable
{
    private nint _handle;

    private WinDivertHandle(nint handle) => _handle = handle;

    public bool IsOpen => _handle != nint.Zero && _handle != WinDivertNative.InvalidHandle;

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
        if (!WinDivertNative.SetParam(_handle, param, value))
        {
            throw new WinDivertException(Marshal.GetLastWin32Error(), $"WinDivertSetParam({param}) failed");
        }
    }

    /// <summary>Blocking receive. Returns false once the handle has been shut down.</summary>
    public bool Receive(Span<byte> buffer, out int length, ref WinDivertAddress addr)
    {
        length = 0;
        if (!IsOpen)
        {
            return false;
        }

        if (!WinDivertNative.Recv(_handle, ref MemoryMarshal.GetReference(buffer), (uint)buffer.Length, out var received, ref addr))
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
    {
        if (!IsOpen)
        {
            return false;
        }

        var buffer = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(packet), packet.Length);
        return WinDivertNative.Send(_handle, ref MemoryMarshal.GetReference(buffer), (uint)packet.Length, out _, ref addr);
    }

    public static void CalculateChecksums(Span<byte> packet, ref WinDivertAddress addr)
        => WinDivertNative.CalcChecksums(ref MemoryMarshal.GetReference(packet), (uint)packet.Length, ref addr, 0);

    public void Shutdown()
    {
        if (IsOpen)
        {
            WinDivertNative.Shutdown(_handle, WinDivertShutdown.Both);
        }
    }

    public void Dispose()
    {
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
