using System.Buffers.Binary;
using System.Net.Sockets;

namespace DpiBypass.Core.Dns;

/// <summary>
/// A place to write bytes that may accept fewer than it was given.
/// </summary>
/// <remarks>
/// One method wide, and it exists because that is exactly the behaviour worth testing.
/// <c>Socket.SendAsync</c> returns the number of bytes it actually sent, which for a
/// stream socket can be less than the buffer whenever the send window is small - and the
/// proxy used to ignore that return value entirely, so a DNS answer that did not fit in
/// one go reached the client as a message shorter than its own length prefix promised.
/// A resolver reading that waits for bytes nobody is going to send.
/// </remarks>
public interface IByteSink
{
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>DNS over a stream: the two byte length prefix, and sending all of it.</summary>
public static class DnsStreamTransport
{
    /// <summary>Wraps a wire format message in the length prefix RFC 1035 §4.2.2 requires.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> message)
    {
        var framed = new byte[message.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)message.Length);
        message.CopyTo(framed.AsSpan(2));
        return framed;
    }

    /// <summary>
    /// Sends every byte, or reports that it could not.
    /// </summary>
    /// <remarks>
    /// Three ways out, and none of them spins: everything sent, the peer stopped taking
    /// bytes (a send that reports zero progress), or the caller's token fired. The zero
    /// case is the important one - a stream socket reports zero when the other end has
    /// gone, and looping on it would be a busy wait for a client that is never coming
    /// back, holding a request slot the whole time.
    /// </remarks>
    /// <returns>True when the whole payload was handed to the socket.</returns>
    public static async ValueTask<bool> SendAllAsync(
        IByteSink sink,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var sent = 0;

        while (sent < payload.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var written = await sink.SendAsync(payload[sent..], cancellationToken).ConfigureAwait(false);
            if (written <= 0)
            {
                return false;
            }

            sent += written;
        }

        return true;
    }

    /// <summary>Adapts a connected socket to <see cref="IByteSink"/>.</summary>
    public sealed class SocketSink : IByteSink
    {
        private readonly Socket _socket;

        public SocketSink(Socket socket) => _socket = socket;

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            => _socket.SendAsync(buffer, SocketFlags.None, cancellationToken);
    }
}
