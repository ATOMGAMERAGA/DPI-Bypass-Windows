using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace DpiBypass.Core.Dns;

public static class DnsRecordType
{
    public const ushort A = 1;
    public const ushort Ns = 2;
    public const ushort Cname = 5;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Https = 65;
}

public readonly record struct DnsQuestion(string Name, ushort Type, ushort Class)
{
    public string CacheKey => $"{Name.ToLowerInvariant()}|{Type}|{Class}";
}

public readonly record struct DnsResourceRecord(string Name, ushort Type, uint Ttl, byte[] Data);

/// <summary>
/// Wire format codec for the subset of DNS this app needs: build a query, read a
/// response, pull out the minimum TTL for caching. Only what the resolver and the
/// loopback proxy actually touch.
/// </summary>
public static class DnsMessage
{
    public const int HeaderLength = 12;

    public static byte[] BuildQuery(ushort id, string name, ushort type, bool recursionDesired = true)
    {
        var encodedName = EncodeName(name);
        var buffer = new byte[HeaderLength + encodedName.Length + 4];

        BinaryPrimitives.WriteUInt16BigEndian(buffer, id);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), (ushort)(recursionDesired ? 0x0100 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), 1); // one question

        encodedName.CopyTo(buffer.AsSpan(HeaderLength));
        var pos = HeaderLength + encodedName.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos), type);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos + 2), 1); // IN
        return buffer;
    }

    public static ushort GetId(ReadOnlySpan<byte> message)
        => message.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(message) : (ushort)0;

    public static void SetId(Span<byte> message, ushort id)
    {
        if (message.Length >= 2)
        {
            BinaryPrimitives.WriteUInt16BigEndian(message, id);
        }
    }

    public static int GetResponseCode(ReadOnlySpan<byte> message)
        => message.Length >= 4 ? message[3] & 0x0F : -1;

    public static bool TryReadQuestion(ReadOnlySpan<byte> message, out DnsQuestion question)
    {
        question = default;
        if (message.Length < HeaderLength)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(message[4..]) == 0)
        {
            return false;
        }

        var pos = HeaderLength;
        if (!TryReadName(message, ref pos, out var name) || pos + 4 > message.Length)
        {
            return false;
        }

        question = new DnsQuestion(
            name,
            BinaryPrimitives.ReadUInt16BigEndian(message[pos..]),
            BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 2)..]));
        return true;
    }

    public static bool TryBuildCacheKey(ReadOnlySpan<byte> query, out string key)
    {
        key = string.Empty;
        if (!TryReadQuestion(query, out _))
        {
            return false;
        }

        var normalized = query.ToArray();
        SetId(normalized, 0);
        key = Convert.ToBase64String(normalized);
        return true;
    }

    public static bool IsResponseForQuery(ReadOnlySpan<byte> query, ReadOnlySpan<byte> response)
    {
        if (query.Length < HeaderLength || response.Length < HeaderLength)
        {
            return false;
        }

        var queryFlags = BinaryPrimitives.ReadUInt16BigEndian(query[2..]);
        var responseFlags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if ((queryFlags & 0x8000) != 0
            || (responseFlags & 0x8000) == 0
            || (queryFlags & 0x7800) != (responseFlags & 0x7800)
            || GetId(query) != GetId(response))
        {
            return false;
        }

        if (!TryReadQuestion(query, out var expected) || !TryReadQuestion(response, out var actual))
        {
            return false;
        }

        return string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase)
            && expected.Type == actual.Type
            && expected.Class == actual.Class
            && BinaryPrimitives.ReadUInt16BigEndian(query[4..]) == 1
            && BinaryPrimitives.ReadUInt16BigEndian(response[4..]) == 1;
    }

    public static byte[] AgeResponseTtls(ReadOnlySpan<byte> response, TimeSpan age)
    {
        var aged = response.ToArray();
        var elapsed = age <= TimeSpan.Zero ? 0u : (uint)Math.Min(uint.MaxValue, age.TotalSeconds);
        if (elapsed == 0 || aged.Length < HeaderLength)
        {
            return aged;
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(aged.AsSpan(4));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(aged.AsSpan(6));
        var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(aged.AsSpan(8));
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(aged.AsSpan(10));
        var pos = HeaderLength;

        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadName(aged, ref pos, out _) || pos + 4 > aged.Length)
            {
                return aged;
            }

            pos += 4;
        }

        var recordCount = answerCount + authorityCount + additionalCount;
        for (var i = 0; i < recordCount; i++)
        {
            if (!TryReadName(aged, ref pos, out _) || pos + 10 > aged.Length)
            {
                return aged;
            }

            var ttlOffset = pos + 4;
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(aged.AsSpan(ttlOffset));
            BinaryPrimitives.WriteUInt32BigEndian(aged.AsSpan(ttlOffset), ttl > elapsed ? ttl - elapsed : 0);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(aged.AsSpan(pos + 8));
            pos += 10;

            if (pos + dataLength > aged.Length)
            {
                return aged;
            }

            pos += dataLength;
        }

        return aged;
    }

    /// <summary>The OPT pseudo-record type, whose CLASS field carries the UDP payload size.</summary>
    private const ushort OptRecordType = 41;

    /// <summary>What a client without EDNS can receive over UDP (RFC 1035 §4.2.1).</summary>
    public const int ClassicUdpPayloadSize = 512;

    /// <summary>
    /// The largest UDP answer this client said it can take.
    /// </summary>
    /// <remarks>
    /// A client that offers EDNS0 puts its receive buffer size in the CLASS field of an
    /// OPT record in the additional section (RFC 6891 §6.1.2). Sending more than that -
    /// or more than 512 bytes to a client that never offered EDNS at all - is a datagram
    /// the resolver will not reassemble, which is why the answer has to be truncated and
    /// the client sent to TCP instead.
    /// </remarks>
    public static int GetClientUdpPayloadSize(ReadOnlySpan<byte> query)
    {
        if (query.Length < HeaderLength)
        {
            return ClassicUdpPayloadSize;
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(query[4..]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(query[6..]);
        var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(query[8..]);
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(query[10..]);
        var pos = HeaderLength;

        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadName(query, ref pos, out _) || pos + 4 > query.Length)
            {
                return ClassicUdpPayloadSize;
            }

            pos += 4;
        }

        var records = answerCount + authorityCount + additionalCount;
        for (var i = 0; i < records; i++)
        {
            if (!TryReadName(query, ref pos, out _) || pos + 10 > query.Length)
            {
                return ClassicUdpPayloadSize;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(query[pos..]);
            var advertised = BinaryPrimitives.ReadUInt16BigEndian(query[(pos + 2)..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(query[(pos + 8)..]);
            pos += 10;

            if (type == OptRecordType)
            {
                // Below 512 the client is asking for less than the classic minimum, which
                // no resolver honours; above 4096 is a buffer nothing on the path will
                // carry unfragmented, and fragmented DNS is what this app exists to avoid.
                return Math.Clamp((int)advertised, ClassicUdpPayloadSize, 4096);
            }

            if (pos + dataLength > query.Length)
            {
                return ClassicUdpPayloadSize;
            }

            pos += dataLength;
        }

        return ClassicUdpPayloadSize;
    }

    /// <summary>
    /// The header-and-question-only form of a response, with TC set.
    /// </summary>
    /// <remarks>
    /// The honest way to say "this does not fit": the client reads TC and asks the same
    /// question again over TCP, which is a listener this proxy also runs. Sending the
    /// first N bytes of the real answer instead - which is what a plain length cap does -
    /// hands the client a message whose record counts promise sections that are not there.
    /// </remarks>
    public static byte[] BuildTruncatedResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < HeaderLength)
        {
            return response.ToArray();
        }

        var pos = HeaderLength;
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);

        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadName(response, ref pos, out _) || pos + 4 > response.Length)
            {
                return response[..Math.Min(response.Length, HeaderLength)].ToArray();
            }

            pos += 4;
        }

        var truncated = response[..pos].ToArray();
        truncated[2] = (byte)(truncated[2] | 0x82); // QR = response, TC = truncated
        BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(6), 0); // no answers
        BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(8), 0); // no authority
        BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(10), 0); // no additional
        return truncated;
    }

    /// <summary>Whether the TC bit is set, i.e. the sender is asking for a TCP retry.</summary>
    public static bool IsTruncated(ReadOnlySpan<byte> message)
        => message.Length >= 3 && (message[2] & 0x02) != 0;

    public static IReadOnlyList<DnsResourceRecord> ReadAnswers(ReadOnlySpan<byte> message)
    {
        var results = new List<DnsResourceRecord>();
        if (message.Length < HeaderLength)
        {
            return results;
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var pos = HeaderLength;

        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadName(message, ref pos, out _))
            {
                return results;
            }

            pos += 4;
        }

        for (var i = 0; i < answerCount; i++)
        {
            if (!TryReadName(message, ref pos, out var name) || pos + 10 > message.Length)
            {
                return results;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(pos + 4)..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 8)..]);
            pos += 10;

            if (pos + dataLength > message.Length)
            {
                return results;
            }

            results.Add(new DnsResourceRecord(name, type, ttl, message.Slice(pos, dataLength).ToArray()));
            pos += dataLength;
        }

        return results;
    }

    /// <summary>Smallest TTL in the answer section, clamped so the cache never pins a stale record.</summary>
    public static uint GetMinimumTtl(ReadOnlySpan<byte> message, uint floor = 30, uint ceiling = 3600)
    {
        var minimum = uint.MaxValue;
        foreach (var record in ReadAnswers(message))
        {
            minimum = Math.Min(minimum, record.Ttl);
        }

        if (minimum == uint.MaxValue)
        {
            return floor;
        }

        return Math.Clamp(minimum, floor, ceiling);
    }

    public static IEnumerable<IPAddress> ReadAddresses(ReadOnlySpan<byte> message)
    {
        var addresses = new List<IPAddress>();
        foreach (var record in ReadAnswers(message))
        {
            if (record.Type == DnsRecordType.A && record.Data.Length == 4)
            {
                addresses.Add(new IPAddress(record.Data));
            }
            else if (record.Type == DnsRecordType.Aaaa && record.Data.Length == 16)
            {
                addresses.Add(new IPAddress(record.Data));
            }
        }

        return addresses;
    }

    public static string? ReadFirstPointer(ReadOnlySpan<byte> message)
    {
        var questionCount = message.Length >= HeaderLength ? BinaryPrimitives.ReadUInt16BigEndian(message[4..]) : 0;
        var answerCount = message.Length >= HeaderLength ? BinaryPrimitives.ReadUInt16BigEndian(message[6..]) : 0;
        var pos = HeaderLength;

        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadName(message, ref pos, out _))
            {
                return null;
            }

            pos += 4;
        }

        for (var i = 0; i < answerCount; i++)
        {
            if (!TryReadName(message, ref pos, out _) || pos + 10 > message.Length)
            {
                return null;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 8)..]);
            pos += 10;

            if (type == DnsRecordType.Ptr)
            {
                var namePos = pos;
                return TryReadName(message, ref namePos, out var target) ? target : null;
            }

            pos += dataLength;
        }

        return null;
    }

    /// <summary>Builds the in-addr.arpa / ip6.arpa name for a reverse lookup.</summary>
    public static string ToReverseName(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}.in-addr.arpa";
        }

        var nibbles = new StringBuilder();
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            nibbles.Append((bytes[i] & 0x0F).ToString("x")).Append('.');
            nibbles.Append((bytes[i] >> 4).ToString("x")).Append('.');
        }

        nibbles.Append("ip6.arpa");
        return nibbles.ToString();
    }

    public static byte[] EncodeName(string name)
    {
        var trimmed = CanonicalizeName(name);
        if (trimmed.Length == 0)
        {
            return [0];
        }

        var labels = trimmed.Split('.');
        var length = 1;
        foreach (var label in labels)
        {
            if (label.Length == 0)
            {
                throw new ArgumentException("DNS name contains an empty label.", nameof(name));
            }

            length += 1 + Encoding.ASCII.GetByteCount(label);
        }

        var buffer = new byte[length];
        var pos = 0;
        foreach (var label in labels)
        {
            var count = Encoding.ASCII.GetBytes(label, 0, label.Length, buffer, pos + 1);
            if (count > 63)
            {
                throw new ArgumentException($"DNS label too long: {label}", nameof(name));
            }

            buffer[pos] = (byte)count;
            pos += count + 1;
        }

        buffer[pos] = 0;
        return buffer;
    }

    private static string CanonicalizeName(string name)
    {
        var trimmed = name.Trim().TrimEnd('.');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("DNS name contains a control character.", nameof(name));
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Expected a hostname, not a URL.", nameof(name));
        }

        var ascii = new IdnMapping().GetAscii(trimmed).ToLowerInvariant();
        if (ascii.Length > 253)
        {
            throw new ArgumentException("DNS name is too long.", nameof(name));
        }

        return ascii;
    }

    /// <summary>Reads a (possibly compressed) name and advances <paramref name="pos"/> past it.</summary>
    public static bool TryReadName(ReadOnlySpan<byte> message, ref int pos, out string name)
    {
        name = string.Empty;
        var builder = new StringBuilder();
        var cursor = pos;
        var jumped = false;
        var hops = 0;

        while (cursor < message.Length)
        {
            var length = message[cursor];

            if (length == 0)
            {
                cursor++;
                if (!jumped)
                {
                    pos = cursor;
                }

                name = builder.ToString();
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= message.Length || ++hops > 16)
                {
                    return false;
                }

                var target = ((length & 0x3F) << 8) | message[cursor + 1];
                if (!jumped)
                {
                    pos = cursor + 2;
                    jumped = true;
                }

                if (target >= message.Length)
                {
                    return false;
                }

                cursor = target;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                return false;
            }

            if (cursor + 1 + length > message.Length)
            {
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.ASCII.GetString(message.Slice(cursor + 1, length)));
            cursor += 1 + length;
        }

        return false;
    }
}
