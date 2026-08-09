using System.Buffers.Binary;
using System.Text;

namespace DpiBypass.Core.Net;

/// <summary>What we managed to learn from a TLS ClientHello.</summary>
public readonly record struct TlsClientHelloInfo(
    string? ServerName,
    int ServerNameOffset,
    int ServerNameLength,
    int RecordLength)
{
    public bool HasServerName => !string.IsNullOrEmpty(ServerName);

    /// <summary>Byte index just past the SNI hostname - the classic desync point.</summary>
    public int ServerNameEnd => ServerNameOffset + ServerNameLength;
}

/// <summary>
/// Minimal ClientHello reader. It only cares about the SNI extension and where in
/// the TCP payload that hostname sits, because that offset is what the desync
/// planner splits on.
/// </summary>
public static class TlsClientHello
{
    private const byte ContentTypeHandshake = 0x16;
    private const byte HandshakeTypeClientHello = 0x01;
    private const ushort ExtensionServerName = 0x0000;

    public static bool IsClientHello(ReadOnlySpan<byte> payload)
        => payload.Length >= 6 && payload[0] == ContentTypeHandshake && payload[5] == HandshakeTypeClientHello;

    public static bool TryParse(ReadOnlySpan<byte> payload, out TlsClientHelloInfo info)
    {
        info = default;
        if (!IsClientHello(payload))
        {
            return false;
        }

        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]) + 5;

        // 5 record header + 4 handshake header + 2 version + 32 random
        var pos = 43;
        if (!Advance(payload, ref pos, 1, out var sessionIdLength))
        {
            return Partial(recordLength, out info);
        }

        pos += sessionIdLength;

        if (!Advance(payload, ref pos, 2, out var cipherSuitesLength))
        {
            return Partial(recordLength, out info);
        }

        pos += cipherSuitesLength;

        if (!Advance(payload, ref pos, 1, out var compressionLength))
        {
            return Partial(recordLength, out info);
        }

        pos += compressionLength;

        if (!Advance(payload, ref pos, 2, out var extensionsLength))
        {
            return Partial(recordLength, out info);
        }

        var extensionsEnd = Math.Min(pos + extensionsLength, payload.Length);

        while (pos + 4 <= extensionsEnd)
        {
            var extensionType = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
            var extensionLength = BinaryPrimitives.ReadUInt16BigEndian(payload[(pos + 2)..]);
            pos += 4;

            if (extensionType == ExtensionServerName)
            {
                if (TryReadServerName(payload, pos, Math.Min(pos + extensionLength, payload.Length), out var name, out var offset, out var length))
                {
                    info = new TlsClientHelloInfo(name, offset, length, recordLength);
                    return true;
                }

                return Partial(recordLength, out info);
            }

            pos += extensionLength;
        }

        return Partial(recordLength, out info);
    }

    private static bool TryReadServerName(ReadOnlySpan<byte> payload, int start, int end, out string? name, out int offset, out int length)
    {
        name = null;
        offset = 0;
        length = 0;

        var pos = start;
        if (pos + 2 > end)
        {
            return false;
        }

        var listEnd = Math.Min(pos + 2 + BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]), end);
        pos += 2;

        while (pos + 3 <= listEnd)
        {
            var nameType = payload[pos];
            var nameLength = BinaryPrimitives.ReadUInt16BigEndian(payload[(pos + 1)..]);
            pos += 3;

            if (pos + nameLength > listEnd)
            {
                return false;
            }

            if (nameType == 0 && nameLength > 0)
            {
                name = Encoding.ASCII.GetString(payload.Slice(pos, nameLength));
                offset = pos;
                length = nameLength;
                return true;
            }

            pos += nameLength;
        }

        return false;
    }

    /// <summary>A ClientHello we could not fully walk still tells us the record length.</summary>
    private static bool Partial(int recordLength, out TlsClientHelloInfo info)
    {
        info = new TlsClientHelloInfo(null, 0, 0, recordLength);
        return true;
    }

    private static bool Advance(ReadOnlySpan<byte> payload, ref int pos, int width, out int value)
    {
        value = 0;
        if (pos + width > payload.Length)
        {
            return false;
        }

        value = width == 1 ? payload[pos] : BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += width;
        return true;
    }
}
