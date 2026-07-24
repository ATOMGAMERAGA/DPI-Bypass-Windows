using System.Buffers.Binary;
using System.Text;

namespace AtomDpi.Core.Engine;

/// <summary>
/// Builds the decoy payloads. They have to look completely ordinary to an
/// inspector - a well formed handshake for an unremarkable hostname - because the
/// whole point is that the inspector believes them and the server never does.
/// </summary>
public static class FakePayloadFactory
{
    /// <summary>Hostnames nobody filters, used inside decoy packets.</summary>
    public static readonly string[] DecoyHosts =
    [
        "www.microsoft.com",
        "www.bing.com",
        "cdn.jsdelivr.net",
        "www.wikipedia.org",
    ];

    /// <summary>A syntactically valid TLS 1.2 ClientHello advertising <paramref name="serverName"/>.</summary>
    public static byte[] CreateTlsClientHello(string serverName, int minimumLength = 0)
    {
        var host = Encoding.ASCII.GetBytes(serverName);

        // server_name extension
        var sniExtension = new byte[9 + host.Length];
        BinaryPrimitives.WriteUInt16BigEndian(sniExtension, 0x0000); // type: server_name
        BinaryPrimitives.WriteUInt16BigEndian(sniExtension.AsSpan(2), (ushort)(5 + host.Length));
        BinaryPrimitives.WriteUInt16BigEndian(sniExtension.AsSpan(4), (ushort)(3 + host.Length));
        sniExtension[6] = 0x00; // host_name
        BinaryPrimitives.WriteUInt16BigEndian(sniExtension.AsSpan(7), (ushort)host.Length);
        host.CopyTo(sniExtension.AsSpan(9));

        var body = new List<byte>(256);
        body.AddRange([0x03, 0x03]); // client_version TLS 1.2
        body.AddRange(DeterministicRandom(32)); // random
        body.Add(32); // session id length
        body.AddRange(DeterministicRandom(32));

        ReadOnlySpan<byte> cipherSuites =
        [
            0x13, 0x01, 0x13, 0x02, 0x13, 0x03, 0xc0, 0x2b,
            0xc0, 0x2f, 0xc0, 0x2c, 0xc0, 0x30, 0x00, 0x9c,
        ];
        body.AddRange([(byte)(cipherSuites.Length >> 8), (byte)cipherSuites.Length]);
        body.AddRange(cipherSuites.ToArray());
        body.AddRange([0x01, 0x00]); // one compression method: null

        var extensions = new List<byte>(sniExtension);
        // supported_groups
        extensions.AddRange([0x00, 0x0a, 0x00, 0x08, 0x00, 0x06, 0x00, 0x1d, 0x00, 0x17, 0x00, 0x18]);
        // ec_point_formats
        extensions.AddRange([0x00, 0x0b, 0x00, 0x02, 0x01, 0x00]);
        // signature_algorithms
        extensions.AddRange([0x00, 0x0d, 0x00, 0x08, 0x00, 0x06, 0x04, 0x03, 0x08, 0x04, 0x04, 0x01]);

        // Pad the decoy out so it is at least as long as the packet it stands in for.
        var handshakeLength = body.Count + 2 + extensions.Count;
        var target = minimumLength - 9; // record header + handshake header
        if (target > handshakeLength)
        {
            var padBytes = target - handshakeLength - 4;
            if (padBytes > 0)
            {
                extensions.AddRange([0x00, 0x15]); // padding extension
                extensions.AddRange([(byte)(padBytes >> 8), (byte)padBytes]);
                extensions.AddRange(new byte[padBytes]);
            }
        }

        body.AddRange([(byte)(extensions.Count >> 8), (byte)extensions.Count]);
        body.AddRange(extensions);

        var record = new byte[9 + body.Count];
        record[0] = 0x16; // handshake
        record[1] = 0x03;
        record[2] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3), (ushort)(body.Count + 4));
        record[5] = 0x01; // client_hello
        record[6] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(7), (ushort)body.Count);
        body.CopyTo(record, 9);
        return record;
    }

    /// <summary>An ordinary looking GET for a hostname nobody blocks.</summary>
    public static byte[] CreateHttpRequest(string host, int minimumLength = 0)
    {
        var builder = new StringBuilder();
        builder.Append("GET / HTTP/1.1\r\n");
        builder.Append("Host: ").Append(host).Append("\r\n");
        builder.Append("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n");
        builder.Append("Accept: */*\r\n");

        var padding = minimumLength - builder.Length - 2;
        if (padding > 20)
        {
            builder.Append("X-Padding: ").Append('a', padding - 13).Append("\r\n");
        }

        builder.Append("\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Deterministic filler. Real randomness is pointless here - these bytes are
    /// never interpreted by anything that matters - and being deterministic keeps
    /// the unit tests honest.
    /// </summary>
    private static byte[] DeterministicRandom(int length)
    {
        var buffer = new byte[length];
        uint state = 0x9E3779B9;
        for (var i = 0; i < length; i++)
        {
            state = (state * 1664525) + 1013904223;
            buffer[i] = (byte)(state >> 24);
        }

        return buffer;
    }
}
