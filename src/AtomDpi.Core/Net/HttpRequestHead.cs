using System.Text;

namespace AtomDpi.Core.Net;

public readonly record struct HttpRequestHeadInfo(
    string? Host,
    int HostHeaderNameOffset,
    int HostValueOffset,
    int HostValueLength)
{
    public bool HasHost => !string.IsNullOrEmpty(Host);
}

/// <summary>
/// Just enough plaintext HTTP parsing to find the Host header, which is the only
/// thing a port 80 DPI filter keys on.
/// </summary>
public static class HttpRequestHead
{
    private static readonly string[] Methods =
    [
        "GET ", "POST ", "HEAD ", "PUT ", "DELETE ", "OPTIONS ", "PATCH ", "TRACE ", "CONNECT ",
    ];

    public static bool IsRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5)
        {
            return false;
        }

        foreach (var method in Methods)
        {
            if (payload.Length >= method.Length && StartsWithAscii(payload, method))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out HttpRequestHeadInfo info)
    {
        info = default;
        if (!IsRequest(payload))
        {
            return false;
        }

        var pos = 0;
        while (pos < payload.Length)
        {
            var lineEnd = IndexOfCrLf(payload, pos);
            if (lineEnd < 0)
            {
                // Header block is split across packets; treat what we have as the whole line.
                lineEnd = payload.Length;
            }

            if (lineEnd == pos)
            {
                break; // end of headers
            }

            var line = payload[pos..lineEnd];
            if (IsHostHeader(line))
            {
                var colon = line.IndexOf((byte)':');
                var valueStart = colon + 1;
                while (valueStart < line.Length && (line[valueStart] == ' ' || line[valueStart] == '\t'))
                {
                    valueStart++;
                }

                var valueEnd = line.Length;
                while (valueEnd > valueStart && (line[valueEnd - 1] == ' ' || line[valueEnd - 1] == '\t'))
                {
                    valueEnd--;
                }

                var host = Encoding.ASCII.GetString(line[valueStart..valueEnd]);
                info = new HttpRequestHeadInfo(host, pos, pos + valueStart, valueEnd - valueStart);
                return true;
            }

            if (lineEnd == payload.Length)
            {
                break;
            }

            pos = lineEnd + 2;
        }

        return false;
    }

    private static bool IsHostHeader(ReadOnlySpan<byte> line)
    {
        if (line.Length < 5)
        {
            return false;
        }

        return (line[0] is (byte)'H' or (byte)'h')
            && (line[1] is (byte)'o' or (byte)'O')
            && (line[2] is (byte)'s' or (byte)'S')
            && (line[3] is (byte)'t' or (byte)'T')
            && line[4] == (byte)':';
    }

    private static int IndexOfCrLf(ReadOnlySpan<byte> payload, int from)
    {
        for (var i = from; i + 1 < payload.Length; i++)
        {
            if (payload[i] == (byte)'\r' && payload[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> payload, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (payload[i] != (byte)value[i])
            {
                return false;
            }
        }

        return true;
    }
}
