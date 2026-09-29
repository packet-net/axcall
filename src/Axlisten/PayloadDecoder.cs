using System.Globalization;
using System.Text;
using Axcall;
using Packet.Ax25;
using Packet.Core;

namespace Axlisten;

/// <summary>What a line under a frame header is, so it can be coloured as such.</summary>
internal enum LineKind
{
    /// <summary>A decoded protocol header: IP, ARP, NET/ROM.</summary>
    Summary,

    /// <summary>Text the station sent.</summary>
    Text,

    /// <summary>A hex dump of something that is not text.</summary>
    Hex,
}

internal readonly record struct PayloadLine(string Text, LineKind Kind);

/// <summary>
/// Turns an information field into lines a person can read: text as text,
/// the protocols a packet channel actually carries as a one-line summary, and
/// anything else as a hex dump.
/// </summary>
internal static class PayloadDecoder
{
    private const byte PidSegment = 0x08;

    public static List<PayloadLine> Describe(Ax25Frame frame, bool alwaysHex)
    {
        var lines = new List<PayloadLine>();
        var info = frame.Info.Span;
        if (info.Length == 0)
            return lines;

        bool described = frame.Pid switch
        {
            Ax25Pid.Ip => DescribeIp(info, lines),
            Ax25Pid.Arp => DescribeArp(info, lines),
            Ax25Pid.NetRom => NetRom.Describe(frame, lines),
            PidSegment => DescribeSegment(info, lines),
            Ax25Pid.NoLayer3 when frame.IsUi => AprsSummary.Describe(frame, lines),
            _ => false,
        };

        if (!described)
            DescribeData(info, lines, alwaysHex);
        else if (alwaysHex)
            HexDump(info, lines);

        return lines;
    }

    /// <summary>
    /// Text if it reads as text, otherwise a hex dump. With
    /// <paramref name="alwaysHex"/>, text is followed by its dump as well.
    /// </summary>
    public static void DescribeData(ReadOnlySpan<byte> data, List<PayloadLine> lines, bool alwaysHex)
    {
        if (TryText(data, out var text))
        {
            foreach (var line in text)
                lines.Add(new PayloadLine(line, LineKind.Text));
            if (!alwaysHex)
                return;
        }
        HexDump(data, lines);
    }

    private static bool DescribeIp(ReadOnlySpan<byte> info, List<PayloadLine> lines)
    {
        lines.Add(new PayloadLine(
            IpPacket.TryRead(info, out var ip) ? $"IP {ip.Describe()}" : $"IP, malformed ({info.Length} bytes)",
            LineKind.Summary));
        return true;
    }

    private static bool DescribeArp(ReadOnlySpan<byte> info, List<PayloadLine> lines)
    {
        if (!AxArpMessage.TryParse(info, out var arp) || arp is null)
        {
            lines.Add(new PayloadLine($"ARP, not AX.25 ARP ({info.Length} bytes)", LineKind.Summary));
            return false;
        }

        lines.Add(new PayloadLine(
            arp.Operation == AxArpOperation.Request
                ? $"ARP who has {arp.TargetAddress}? tell {arp.SenderAddress} ({arp.SenderCallsign})"
                : $"ARP {arp.SenderAddress} is at {arp.SenderCallsign}",
            LineKind.Summary));
        return true;
    }

    private static bool DescribeSegment(ReadOnlySpan<byte> info, List<PayloadLine> lines)
    {
        // Section 6.6: one octet saying whether this is the first segment and
        // how many are still to come. The first also carries the real PID.
        var first = (info[0] & 0x80) != 0;
        var remaining = info[0] & 0x7F;
        var body = info[1..];
        var pid = "";
        if (first && body.Length > 0)
        {
            pid = $", pid={body[0]:X2}";
            body = body[1..];
        }

        lines.Add(new PayloadLine(
            string.Create(CultureInfo.InvariantCulture, $"segment{(first ? ", first" : "")}, {remaining} to come{pid}"),
            LineKind.Summary));
        HexDump(body, lines);
        return true;
    }

    /// <summary>
    /// Read an information field as text, if it is text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Text is UTF-8 when it decodes as such, which covers ASCII, and Latin-1
    /// otherwise, which is what an old BBS banner is closer to than anything.
    /// It is split into lines on CR, LF or CRLF, since stations use all three.
    /// </para>
    /// <para>
    /// Control characters other than tab never reach the terminal. This is
    /// printing whatever anybody within range chose to transmit, and an escape
    /// sequence is a way to rewrite a terminal's title, its colours or worse.
    /// They are shown as a dot, as every monitor since the TNC-2 has done.
    /// </para>
    /// </remarks>
    public static bool TryText(ReadOnlySpan<byte> data, out List<string> lines)
    {
        lines = [];

        string decoded;
        bool utf8;
        try
        {
            decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
            utf8 = true;
        }
        catch (DecoderFallbackException)
        {
            decoded = Encoding.Latin1.GetString(data);
            utf8 = false;
        }

        // Binary is the likelier reading of anything with a NUL in it, or with
        // more than one unprintable byte in eight. In Latin-1, 0x80 to 0x9F
        // print nothing either, and a routing table is full of them.
        int unprintable = 0;
        foreach (var b in data)
        {
            if (b == 0)
                return false;
            if ((b < 0x20 && b is not ((byte)'\r' or (byte)'\n' or (byte)'\t'))
                || b == 0x7F
                || (!utf8 && b is >= 0x80 and < 0xA0))
            {
                unprintable++;
            }
        }
        if (unprintable * 8 > data.Length)
            return false;

        var sb = new StringBuilder();
        for (int i = 0; i < decoded.Length; i++)
        {
            var c = decoded[i];
            if (c is '\r' or '\n')
            {
                lines.Add(sb.ToString());
                sb.Clear();
                if (c == '\r' && i + 1 < decoded.Length && decoded[i + 1] == '\n')
                    i++;
                continue;
            }
            sb.Append(char.IsControl(c) && c != '\t' ? '.' : c);
        }
        if (sb.Length > 0)
            lines.Add(sb.ToString());

        return true;
    }

    /// <summary>Sixteen bytes a line, offset, hex, then the printable ones.</summary>
    public static void HexDump(ReadOnlySpan<byte> data, List<PayloadLine> lines)
    {
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            var row = data.Slice(offset, Math.Min(16, data.Length - offset));
            var sb = new StringBuilder(76);
            sb.Append(CultureInfo.InvariantCulture, $"{offset:x4}  ");
            for (int i = 0; i < 16; i++)
            {
                if (i == 8) sb.Append(' ');
                if (i < row.Length) sb.Append(CultureInfo.InvariantCulture, $"{row[i]:x2} ");
                else sb.Append("   ");
            }
            sb.Append(" |");
            foreach (var b in row)
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            sb.Append('|');
            lines.Add(new PayloadLine(sb.ToString(), LineKind.Hex));
        }
    }

    /// <summary>
    /// A shifted AX.25 address slot as a callsign, or null for a slot that is
    /// not one.
    /// </summary>
    public static Callsign? ReadCallsign(ReadOnlySpan<byte> slot)
    {
        try
        {
            var call = Ax25Address.Read(slot).Callsign;
            return call.Base.Length > 0 ? call : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
