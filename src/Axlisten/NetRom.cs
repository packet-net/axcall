using System.Globalization;
using System.Text;
using Packet.Ax25;

namespace Axlisten;

/// <summary>
/// Enough NET/ROM to say what a frame is doing: routing broadcasts, and the
/// network and transport headers of everything else.
/// </summary>
/// <remarks>
/// NET/ROM is most of what is on a packet channel that is not a keyboard
/// session, so a monitor that prints it as a hex dump is a monitor that shows
/// the least of what is there. The layouts are the 1988 Software 2000 ones,
/// which BPQ, XRouter, JNOS and the Linux stack all still speak.
/// </remarks>
internal static class NetRom
{
    private const int AddressBytes = 7;
    private const int AliasBytes = 6;
    private const int RouteBytes = AddressBytes + AliasBytes + AddressBytes + 1;
    private const int HeaderBytes = AddressBytes + AddressBytes + 1 + 5;

    public static bool Describe(Ax25Frame frame, List<PayloadLine> lines)
    {
        var info = frame.Info.Span;

        if (info[0] == 0xFF && frame.IsUi && frame.Destination.Callsign.Base == "NODES")
            return DescribeNodes(info, lines);

        return DescribeNetwork(info, lines);
    }

    private static bool DescribeNodes(ReadOnlySpan<byte> info, List<PayloadLine> lines)
    {
        if (info.Length < 1 + AliasBytes)
            return false;

        var alias = Alias(info.Slice(1, AliasBytes));
        var routes = info[(1 + AliasBytes)..];
        var count = routes.Length / RouteBytes;

        lines.Add(new PayloadLine(
            string.Create(CultureInfo.InvariantCulture,
                $"NET/ROM nodes from {(alias.Length > 0 ? alias : "(no alias)")}, {count} route{(count == 1 ? "" : "s")}"),
            LineKind.Summary));

        for (int i = 0; i < count; i++)
        {
            var route = routes.Slice(i * RouteBytes, RouteBytes);
            var destination = PayloadDecoder.ReadCallsign(route[..AddressBytes]);
            var destinationAlias = Alias(route.Slice(AddressBytes, AliasBytes));
            var neighbour = PayloadDecoder.ReadCallsign(route.Slice(AddressBytes + AliasBytes, AddressBytes));
            var quality = route[RouteBytes - 1];

            var node = destinationAlias.Length > 0 ? $"{destinationAlias}:{destination}" : $"{destination}";
            lines.Add(new PayloadLine(
                string.Create(CultureInfo.InvariantCulture, $"  {node,-17} via {neighbour,-9} quality {quality}"),
                LineKind.Summary));
        }

        return true;
    }

    private static bool DescribeNetwork(ReadOnlySpan<byte> info, List<PayloadLine> lines)
    {
        if (info.Length < HeaderBytes)
            return false;

        var origin = PayloadDecoder.ReadCallsign(info[..AddressBytes]);
        var destination = PayloadDecoder.ReadCallsign(info.Slice(AddressBytes, AddressBytes));
        if (origin is null || destination is null)
            return false;

        var ttl = info[14];
        byte index = info[15], id = info[16], txSeq = info[17], rxSeq = info[18], opcode = info[19];
        var data = info[HeaderBytes..];

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"NET/ROM {origin}>{destination} ttl={ttl} ");

        switch (opcode & 0x0F)
        {
            case 0 when index == 0x0C && id == 0x0C:
                // The protocol-extension opcode, with the family JNOS and the
                // Linux stack used for IP carried over NET/ROM.
                sb.Append("IP");
                lines.Add(new PayloadLine(sb.ToString(), LineKind.Summary));
                if (Axcall.IpPacket.TryRead(data, out var ip))
                    lines.Add(new PayloadLine($"IP {ip.Describe()}", LineKind.Summary));
                return true;

            case 1:
                sb.Append(CultureInfo.InvariantCulture, $"CONN REQ circuit={index:X2}{id:X2}");
                if (data.Length >= 1 + AddressBytes + AddressBytes)
                {
                    sb.Append(CultureInfo.InvariantCulture,
                        $" window={data[0]} user={PayloadDecoder.ReadCallsign(data.Slice(1, AddressBytes))}"
                        + $" node={PayloadDecoder.ReadCallsign(data.Slice(1 + AddressBytes, AddressBytes))}");
                }
                data = [];
                break;

            case 2:
                // Your circuit, then mine: the acknowledger's own circuit is
                // the second pair.
                sb.Append(CultureInfo.InvariantCulture, $"CONN ACK circuit={index:X2}{id:X2}");
                if ((opcode & 0x80) != 0) sb.Append(" refused");
                else if (data.Length >= 1) sb.Append(CultureInfo.InvariantCulture, $" window={data[0]}");
                data = [];
                break;

            case 3:
                sb.Append(CultureInfo.InvariantCulture, $"DISC REQ circuit={index:X2}{id:X2}");
                break;

            case 4:
                sb.Append(CultureInfo.InvariantCulture, $"DISC ACK circuit={index:X2}{id:X2}");
                break;

            case 5:
                sb.Append(CultureInfo.InvariantCulture, $"INFO circuit={index:X2}{id:X2} tx={txSeq} rx={rxSeq}");
                break;

            case 6:
                sb.Append(CultureInfo.InvariantCulture, $"INFO ACK circuit={index:X2}{id:X2} rx={rxSeq}");
                break;

            default:
                sb.Append(CultureInfo.InvariantCulture, $"opcode {opcode & 0x0F}");
                break;
        }

        if ((opcode & 0x0F) != 2)
        {
            if ((opcode & 0x80) != 0) sb.Append(" choke");
            if ((opcode & 0x40) != 0) sb.Append(" nak");
            if ((opcode & 0x20) != 0) sb.Append(" more");
        }

        lines.Add(new PayloadLine(sb.ToString(), LineKind.Summary));

        if (data.Length > 0)
            PayloadDecoder.DescribeData(data, lines, alwaysHex: false);

        return true;
    }

    private static string Alias(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(AliasBytes);
        foreach (var b in bytes)
            sb.Append(b is > 0x20 and < 0x7F ? (char)b : ' ');
        return sb.ToString().Trim();
    }
}
