using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AtomDpi.Core.Dns;

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
        var trimmed = name.TrimEnd('.');
        if (trimmed.Length == 0)
        {
            return [0];
        }

        var labels = trimmed.Split('.');
        var length = 1;
        foreach (var label in labels)
        {
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
