using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using DpiBypass.Core.Ipc;
using DpiBypass.Core.Diagnostics;
using Xunit;

namespace DpiBypass.Tests;

public sealed class ProtectionTransportTests
{
    [Fact]
    public void NativePipeKeepsMediumIntegrityAndUserOnlyAccess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pipe = ProtectionPipeSecurity.Create(PipeName(), 16);
        var result = GetSecurityInfo(pipe.SafePipeHandle, 6, 0x14, out _, out _, out _, out _, out var descriptor);
        Assert.Equal(0u, result);
        nint text = 0;
        try
        {
            Assert.True(ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, 0x14, out text, out _));
            var sddl = Marshal.PtrToStringUni(text)!;
            Assert.True(sddl.Contains("(ML;;NW;;;ME)", StringComparison.Ordinal), sddl);
            Assert.Contains(";;;" + WindowsIdentity.GetCurrent().User!.Value + ")", sddl);
            Assert.DoesNotContain(";;;WD)", sddl);
            Assert.DoesNotContain(";;;BU)", sddl);
        }
        finally { if (text != 0) LocalFree(text); if (descriptor != 0) LocalFree(descriptor); }
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task FrameLengthIsRejectedBeforeReadingOrAllocatingTheBody(int length)
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProtectionFrames.ReadAsync<ProtectionRequest>(stream, ProtectionProtocol.MaxRequestBytes, default));
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task OversizedOutboundFrameIsRejectedBeforeWriting()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProtectionFrames.WriteAsync(stream, new string('x', 50), 10, default));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task FramingRejectsUnknownSchemaFields()
    {
        using var stream = new MemoryStream();
        await ProtectionFrames.WriteAsync(stream, new { Id = "fixture", Method = "__snapshot", Unexpected = true }, 1024, default);
        stream.Position = 0;
        await Assert.ThrowsAsync<JsonException>(() => ProtectionFrames.ReadAsync<ProtectionRequest>(stream, 1024, default));
    }

    [Fact]
    public async Task ClientRejectsServerWithUnpinnedProcessId()
    {
        if (!OperatingSystem.IsWindows()) return;
        var peer = CurrentPeer();
        var name = PipeName();
        var calls = 0;
        await using var server = new ProtectionPipeServer(name, peer, (_, _) =>
        {
            Interlocked.Increment(ref calls); return Task.FromResult(new ProtectionResponse { Ok = true });
        });
        server.Start();
        var client = new ProtectionRpcClient(name, peer with { ProcessId = int.MaxValue }, false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(Request()));
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ClientRejectsServerWithUnpinnedExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var peer = CurrentPeer();
        var name = PipeName();
        await using var server = new ProtectionPipeServer(name, peer, (_, _) => Task.FromResult(new ProtectionResponse { Ok = true }));
        server.Start();
        var client = new ProtectionRpcClient(name, peer with { Executable = Path.Combine(Path.GetTempPath(), "not-the-helper.exe") }, false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(Request()));
    }

    [Fact]
    public async Task ServerRejectsCallerWithUnpinnedProcessId()
    {
        if (!OperatingSystem.IsWindows()) return;
        var peer = CurrentPeer();
        var name = PipeName();
        var calls = 0;
        await using var server = new ProtectionPipeServer(name, peer with { ProcessId = int.MaxValue }, (_, _) =>
        {
            Interlocked.Increment(ref calls); return Task.FromResult(new ProtectionResponse { Ok = true });
        });
        server.Start();
        var client = new ProtectionRpcClient(name, peer, false);
        await Assert.ThrowsAnyAsync<IOException>(() => client.SendAsync(Request()));
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task RealPipeStillDeliversCancellationWithEightLongOperations()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (controller, fake) = ProtectionBrokerDispatcherTests.ControllerProxy.Create();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        fake.Handlers[nameof(controller.ProbeAsync)] = arguments => BlockAsync((CancellationToken)arguments[1]!);
        async Task<ProbeResult> BlockAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref started) == 8) allStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }
        using var dispatcher = new ProtectionBrokerDispatcher(controller);
        var name = PipeName();
        var peer = CurrentPeer();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        await using var server = new ProtectionPipeServer(name, peer, dispatcher.HandleAsync, diagnostics.Enqueue);
        server.Start();
        var client = new ProtectionRpcClient(name, peer, false);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var requests = Enumerable.Range(0, 8).Select(_ => new ProtectionRequest
        {
            Id = Guid.NewGuid().ToString("N"), Method = nameof(controller.ProbeAsync),
            Arguments = [JsonSerializer.SerializeToElement("fixture.example")],
        }).ToArray();
        var pending = requests.Select(request => client.SendAsync(request, budget.Token)).ToArray();
        var earlyCompletion = Task.WhenAny(pending);
        if (await Task.WhenAny(allStarted.Task, earlyCompletion).WaitAsync(budget.Token) == earlyCompletion)
        {
            ProtectionResponse early;
            try { early = await await earlyCompletion; }
            catch (Exception exception) { throw new AggregateException("Transport failed after " + Volatile.Read(ref started) + " operations started.", diagnostics.Take(2).Prepend(exception)); }
            Assert.Fail("A long operation returned before cancellation: " + early.Error);
        }
        Assert.True((await client.SendAsync(Request(), budget.Token)).Ok);
        await Task.WhenAll(requests.Select(request => client.SendAsync(new ProtectionRequest
        {
            Id = Guid.NewGuid().ToString("N"), Method = "__cancel",
            Arguments = [JsonSerializer.SerializeToElement(request.Id)],
        }, budget.Token)));
        var responses = await Task.WhenAll(pending);
        Assert.All(responses, response => Assert.Equal("REQUEST_CANCELLED", response.Error));
    }

    private static ProtectionPeer CurrentPeer() => new(Environment.ProcessId,
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName);
    private static string PipeName() => "DpiBypass.Engine." + Guid.NewGuid().ToString("N");
    private static ProtectionRequest Request() => new() { Id = Guid.NewGuid().ToString("N"), Method = "__snapshot" };

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint information,
        out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(nint descriptor, uint revision,
        uint information, out nint text, out uint length);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
