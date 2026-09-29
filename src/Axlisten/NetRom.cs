using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.NetRom.Wire;

namespace Axlisten;

/// <summary>
/// NET/ROM as lines a person can read: routing broadcasts with their routes,
/// and the network and transport headers of everything else.
/// </summary>
/// <remarks>
/// NET/ROM is most of what is on a packet channel that is not a keyboard
/// session, so a monitor that prints it as a hex dump shows the least of what
/// is there. The decoding is Packet.NetRom's; this is only the wording.
/// </remarks>
internal static class NetRom
{
    // The protocol-extension opcode with circuit 0C/0C: IP carried over
    // NET/ROM, as JNOS and the Linux stack sent it.
    private const byte IpFamily = 0x0C;

    public static bool Describe(Ax25Frame frame, List<PayloadLine> lines)
    {
        var info = frame.Info.Span;

        if (frame.IsUi
            && frame.Destination.Callsign.Base == NodesBroadcast.NodesDestination
            && NodesBroadcast.TryParse(info, out var nodes))
        {
            DescribeNodes(nodes, lines);
            return true;
        }

        if (NetRomPacket.TryParse(info, out var packet))
        {
            DescribePacket(packet, lines);
            return true;
        }

        return false;
    }

    private static void DescribeNodes(NodesBroadcast nodes, List<PayloadLine> lines)
    {
        var count = nodes.Entries.Count;
        var alias = nodes.SenderAlias.Length > 0 ? nodes.SenderAlias : "(no alias)";
        lines.Add(new PayloadLine(
            string.Create(CultureInfo.InvariantCulture, $"NET/ROM nodes from {alias}, {count} route{(count == 1 ? "" : "s")}"),
            LineKind.Summary));

        foreach (var route in nodes.Entries)
        {
            var node = route.DestinationAlias.Length > 0
                ? $"{route.DestinationAlias}:{route.Destination}"
                : route.Destination.ToString();
            lines.Add(new PayloadLine(
                string.Create(CultureInfo.InvariantCulture,
                    $"  {node,-17} via {route.BestNeighbour,-9} quality {route.BestQuality}"),
                LineKind.Summary));
        }
    }

    private static void DescribePacket(NetRomPacket packet, List<PayloadLine> lines)
    {
        var network = packet.Network;
        var transport = packet.Transport;
        var payload = packet.Payload.Span;
        var circuit = string.Create(CultureInfo.InvariantCulture, $"circuit={transport.CircuitIndex:X2}{transport.CircuitId:X2}");

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"NET/ROM {network.Origin}>{network.Destination} ttl={network.TimeToLive} ");

        if (Inp3L3RttFrame.TryFrom(packet, out var rtt))
        {
            sb.Append("L3RTT");
            if (rtt.Inp3Capable) sb.Append(" INP3");
            lines.Add(new PayloadLine(sb.ToString(), LineKind.Summary));
            return;
        }

        switch (transport.Opcode)
        {
            case (NetRomOpcode)0 when transport.CircuitIndex == IpFamily && transport.CircuitId == IpFamily:
                sb.Append("IP");
                lines.Add(new PayloadLine(sb.ToString(), LineKind.Summary));
                lines.Add(new PayloadLine(
                    Axcall.IpPacket.TryRead(payload, out var ip) ? $"IP {ip.Describe()}" : $"IP, malformed ({payload.Length} bytes)",
                    LineKind.Summary));
                return;

            case NetRomOpcode.ConnectRequest:
                sb.Append("CONN REQ ").Append(circuit);
                if (ConnectRequestInfo.TryParse(payload, out var window, out var user, out var node))
                    sb.Append(CultureInfo.InvariantCulture, $" window={window} user={user} node={node}");
                payload = [];
                break;

            case NetRomOpcode.ConnectAcknowledge:
                sb.Append("CONN ACK ").Append(circuit);
                // A choked CONN ACK is a refusal.
                if (transport.Choke) sb.Append(" refused");
                else if (ConnectAckInfo.TryReadAcceptedWindow(payload, out var accepted))
                    sb.Append(CultureInfo.InvariantCulture, $" window={accepted}");
                lines.Add(new PayloadLine(sb.ToString(), LineKind.Summary));
                return;

            case NetRomOpcode.DisconnectRequest:
                sb.Append("DISC REQ ").Append(circuit);
                break;

            case NetRomOpcode.DisconnectAcknowledge:
                sb.Append("DISC ACK ").Append(circuit);
                break;

            case NetRomOpcode.Information:
                sb.Append("INFO ").Append(circuit)
                  .Append(CultureInfo.InvariantCulture, $" tx={transport.TxSequence} rx={transport.RxSequence}");
                break;

            case NetRomOpcode.InformationAcknowledge:
                sb.Append("INFO ACK ").Append(circuit)
                  .Append(CultureInfo.InvariantCulture, $" rx={transport.RxSequence}");
                break;

            default:
                sb.Append(CultureInfo.InvariantCulture, $"opcode {(byte)transport.Opcode} ").Append(circuit);
                break;
        }

        if (transport.Choke) sb.Append(" choke");
        if (transport.Nak) sb.Append(" nak");
        if (transport.MoreFollows) sb.Append(" more");

        lines.Add(new PayloadLine(sb.ToString(), LineKind.Summary));

        if (payload.Length > 0)
            PayloadDecoder.DescribeData(payload, lines, alwaysHex: false);
    }
}
