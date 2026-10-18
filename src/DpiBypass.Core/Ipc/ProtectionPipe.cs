using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DpiBypass.Core.Ipc;

public sealed record ProtectionPeer(int ProcessId, string Executable);

internal static class ProtectionFrames
{
    public static async Task<T> ReadAsync<T>(Stream stream, int limit, CancellationToken token)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 2 || length > limit) throw new InvalidDataException("FRAME_SIZE");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, ProtectionWireJson.Options) ?? throw new InvalidDataException("FRAME_SCHEMA");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, int limit, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, ProtectionWireJson.Options);
        if (body.Length > limit) throw new InvalidDataException("FRAME_SIZE");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}

/// <summary>A local peer is pinned to its PID, image and Windows user, not a claimed JSON identity.</summary>
internal static class ProtectionPeerChecks
{
    public static bool Matches(SafePipeHandle pipe, ProtectionPeer expected, bool server, bool requireElevated)
    {
        if (!OperatingSystem.IsWindows()) return false;
        uint pid;
        if (!(server ? GetNamedPipeServerProcessId(pipe, out pid) : GetNamedPipeClientProcessId(pipe, out pid)) || pid != expected.ProcessId) return false;
        return MatchesProcess(expected, requireElevated);
    }
    public static bool MatchesProcess(ProtectionPeer expected, bool requireElevated)
    {
        using var process = OpenProcess(0x1000, false, (uint)expected.ProcessId);
        if (process.IsInvalid) return false;
        var path = new StringBuilder(32768); var length = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length)
            || !string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(expected.Executable), StringComparison.OrdinalIgnoreCase)
            || !OpenProcessToken(process, 8, out var token)) return false;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        {
            if (identity.User != current.User) return false;
            return !requireElevated || GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) && elevated != 0;
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int size, out int returned);
}

internal static class ProtectionPipeSecurity
{
    public static NamedPipeServerStream Create(string name, int instances)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException();
        nint descriptor = 0;
        try
        {
            // Supplying the mandatory label at native creation avoids managed ACL
            // code requesting audit-SACL privileges. WRITE_OWNER also overlaps the
            // FIRST_PIPE_INSTANCE flag, so it cannot be added to every listener.
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{sid.Value})S:(ML;;NW;;;ME)", 1, out descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 3 | 0x40000000, 8, (uint)instances, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { if (descriptor != 0) LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out nint descriptor, out uint size);
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances,
        uint outputBuffer, uint inputBuffer, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

public sealed class ProtectionRpcClient
{
    private readonly string _pipeName;
    private readonly ProtectionPeer _helper;
    private readonly bool _requireElevation;
    public ProtectionRpcClient(string pipeName, ProtectionPeer helper) : this(pipeName, helper, true) { }
    internal ProtectionRpcClient(string pipeName, ProtectionPeer helper, bool requireElevation)
    {
        if (!ProtectionProtocol.ValidPipeName(pipeName)) throw new ArgumentException("INVALID_PIPE");
        _pipeName = pipeName; _helper = helper; _requireElevation = requireElevation;
    }
    public async Task<ProtectionResponse> SendAsync(ProtectionRequest request, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProtectionProtocol.RequestDeadline);
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, deadline.Token).ConfigureAwait(false);
        if (!ProtectionPeerChecks.Matches(pipe.SafePipeHandle, _helper, true, _requireElevation))
            throw new UnauthorizedAccessException("HELPER_IDENTITY_MISMATCH");
        await ProtectionFrames.WriteAsync(pipe, request, ProtectionProtocol.MaxRequestBytes, deadline.Token).ConfigureAwait(false);
        return await ProtectionFrames.ReadAsync<ProtectionResponse>(pipe, ProtectionProtocol.MaxResponseBytes, deadline.Token).ConfigureAwait(false);
    }
}

/// <summary>Concurrent bounded requests let cancellation bypass a long driver/latency operation.</summary>
public sealed class ProtectionPipeServer : IAsyncDisposable
{
    public static bool ValidateOwner(ProtectionPeer owner) => ProtectionPeerChecks.MatchesProcess(owner, false);
    private readonly string _pipeName;
    private readonly ProtectionPeer _owner;
    private readonly Func<ProtectionRequest, CancellationToken, Task<ProtectionResponse>> _handler;
    private readonly Action<Exception>? _diagnostic;
    private readonly CancellationTokenSource _stopping = new();
    private Task[] _listeners = [];
    public ProtectionPipeServer(string pipeName, ProtectionPeer owner, Func<ProtectionRequest, CancellationToken, Task<ProtectionResponse>> handler)
        : this(pipeName, owner, handler, null) { }
    internal ProtectionPipeServer(string pipeName, ProtectionPeer owner,
        Func<ProtectionRequest, CancellationToken, Task<ProtectionResponse>> handler, Action<Exception>? diagnostic)
    {
        if (!ProtectionProtocol.ValidPipeName(pipeName)) throw new ArgumentException("INVALID_PIPE");
        _pipeName = pipeName; _owner = owner; _handler = handler; _diagnostic = diagnostic;
    }
    public void Start()
    {
        if (_listeners.Length != 0) return;
        _listeners = Enumerable.Range(0, 16).Select(_ => Task.Run(ListenAsync)).ToArray();
    }
    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await using var pipe = ProtectionPipeSecurity.Create(_pipeName, 16);
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
                if (!ProtectionPeerChecks.Matches(pipe.SafePipeHandle, _owner, false, false)) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                deadline.CancelAfter(ProtectionProtocol.RequestDeadline);
                // Input has its own short bound so a partial frame cannot occupy a slot for minutes.
                using var input = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                input.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await ProtectionFrames.ReadAsync<ProtectionRequest>(pipe, ProtectionProtocol.MaxRequestBytes, input.Token).ConfigureAwait(false);
                var response = await _handler(request, deadline.Token).ConfigureAwait(false);
                await ProtectionFrames.WriteAsync(pipe, response, ProtectionProtocol.MaxResponseBytes, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                _diagnostic?.Invoke(exception);
                // Malformed/disconnected callers cannot kill the engine or create a busy loop.
                try { await Task.Delay(25, _stopping.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_listeners).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        _stopping.Dispose();
    }
}
