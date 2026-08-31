using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DpiBypass.Core.Network;

/// <summary>
/// Measures a Minecraft Java server's real application round trip.
/// </summary>
/// <remarks>
/// <para>
/// The Java edition's Server List Ping exchange ends with a Ping packet (0x01) carrying
/// an arbitrary payload, which the server echoes back unchanged as Pong. That is a real
/// application-level round trip through the server's own network thread - the same number
/// the game's own multiplayer list shows - rather than a TCP handshake time or an ICMP
/// echo to the same address.
/// </para>
/// <para>
/// Deliberately not generalised. Every game has a different handshake, most are
/// undocumented, and inventing one would produce a number that looks like a ping and is
/// not one. This is implemented because the exchange is public, stable and cheap; other
/// protocols stay honest route references until someone can do the same for them.
/// </para>
/// <para>
/// One connection carries the whole series: the handshake is paid once and each sample is
/// a single Ping/Pong pair, so a server sees one status connection rather than a stream of
/// them. That is also what keeps this from looking like abuse.
/// </para>
/// </remarks>
public static class MinecraftStatusProbe
{
    /// <summary>The Java edition's default port.</summary>
    public const int DefaultPort = 25565;

    /// <summary>Protocol version sent in the handshake: -1 means "just asking".</summary>
    private const int UnknownProtocolVersion = -1;

    private const int StatusState = 1;
    private const int MaximumPacketBytes = 64 * 1024;

    /// <summary>The round trips a series measured, and how many were attempted.</summary>
    public sealed record Series(IReadOnlyList<double> Samples, int Attempts, string? Failure);

    /// <summary>
    /// Opens one status connection and times <paramref name="count"/> Ping/Pong pairs.
    /// </summary>
    public static async Task<Series> MeasureAsync(
        IPAddress address,
        int port,
        string host,
        int count,
        int warmup,
        TimeSpan pacing,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        var attempts = Math.Max(1, count);
        var samples = new List<double>(attempts);

        try
        {
            using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));

            await client.ConnectAsync(address, port, connectTimeout.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();

            await SendAsync(stream, BuildHandshake(host, port), cancellationToken).ConfigureAwait(false);
            await SendAsync(stream, BuildPacket(0x00, []), cancellationToken).ConfigureAwait(false);

            // The status response is read and discarded: its content is the server's MOTD
            // and player list, none of which this measurement is about.
            if (await ReadPacketAsync(stream, timeoutMilliseconds, cancellationToken).ConfigureAwait(false) is null)
            {
                return new Series([], attempts, "Sunucu durum yanıtı göndermedi.");
            }

            for (var index = 0; index < attempts + Math.Max(0, warmup); index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(payload, DateTime.UtcNow.Ticks + index);

                var started = Stopwatch.GetTimestamp();
                await SendAsync(stream, BuildPacket(0x01, payload), cancellationToken).ConfigureAwait(false);
                var pong = await ReadPacketAsync(stream, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                // A server that answered something other than our own payload has not
                // completed this round trip, so the sample is dropped rather than kept.
                if (pong is { Length: >= 9 } && pong[0] == 0x01 && pong.AsSpan(1, 8).SequenceEqual(payload))
                {
                    if (index >= warmup)
                    {
                        samples.Add(elapsed);
                    }
                }
                else if (pong is null)
                {
                    break;
                }

                if (index + 1 < attempts + warmup && pacing > TimeSpan.Zero)
                {
                    await Task.Delay(pacing, cancellationToken).ConfigureAwait(false);
                }
            }

            return new Series(samples, attempts, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new Series(samples, attempts, "Sunucuya bağlanılamadı (zaman aşımı).");
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException)
        {
            return new Series(samples, attempts, ex.Message);
        }
    }

    /// <summary>Handshake packet: protocol version, host, port, next state.</summary>
    internal static byte[] BuildHandshake(string host, int port)
    {
        var body = new List<byte>(64);
        WriteVarInt(body, UnknownProtocolVersion);
        WriteString(body, host);
        body.Add((byte)(port >> 8));
        body.Add((byte)(port & 0xFF));
        WriteVarInt(body, StatusState);

        return BuildPacket(0x00, [.. body]);
    }

    /// <summary>Length-prefixed packet: VarInt length, VarInt id, then the body.</summary>
    internal static byte[] BuildPacket(int packetId, ReadOnlySpan<byte> body)
    {
        var inner = new List<byte>(body.Length + 8);
        WriteVarInt(inner, packetId);
        inner.AddRange(body);

        var packet = new List<byte>(inner.Count + 5);
        WriteVarInt(packet, inner.Count);
        packet.AddRange(inner);

        return [.. packet];
    }

    internal static void WriteVarInt(List<byte> target, int value)
    {
        var unsigned = unchecked((uint)value);

        while (true)
        {
            if ((unsigned & ~0x7Fu) == 0)
            {
                target.Add((byte)unsigned);
                return;
            }

            target.Add((byte)((unsigned & 0x7F) | 0x80));
            unsigned >>= 7;
        }
    }

    private static void WriteString(List<byte> target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(target, bytes.Length);
        target.AddRange(bytes);
    }

    private static async Task SendAsync(Stream stream, byte[] packet, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one length-prefixed packet, or null when the deadline passes.</summary>
    private static async Task<byte[]?> ReadPacketAsync(
        Stream stream,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));

        try
        {
            var length = await ReadVarIntAsync(stream, deadline.Token).ConfigureAwait(false);
            if (length is <= 0 or > MaximumPacketBytes)
            {
                return null;
            }

            var buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, deadline.Token).ConfigureAwait(false);
            return buffer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException)
        {
            return null;
        }
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = 0;
        var buffer = new byte[1];

        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            result |= (buffer[0] & 0x7F) << shift;

            if ((buffer[0] & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("VarInt beş bayttan uzun olamaz.");
    }
}
