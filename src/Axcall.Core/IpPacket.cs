using System.Buffers.Binary;
using System.Net;

namespace Axcall;

/// <summary>
/// Just enough of an IPv4 header to decide what to do with a packet, read in
/// place without copying.
/// </summary>
/// <remarks>
/// This is not an IP stack. The kernel on the other side of the TUN device is
/// the IP stack; all that is wanted here is the source, the destination, the
/// protocol and, when it is the first fragment of something with ports, the
/// ports. That is the whole of what the egress filter and the routing table
/// need to look at.
/// </remarks>
public readonly ref struct IpPacket
{
    private readonly ReadOnlySpan<byte> bytes;

    private IpPacket(ReadOnlySpan<byte> bytes) => this.bytes = bytes;

    /// <summary>Smallest IPv4 header.</summary>
    public const int MinHeaderBytes = 20;

    public const byte ProtocolIcmp = 1;
    public const byte ProtocolTcp = 6;
    public const byte ProtocolUdp = 17;

    /// <summary>
    /// Read an IPv4 packet, or fail on anything that is not one.
    /// </summary>
    /// <remarks>
    /// IPv6 fails here, and that is deliberate for now rather than an
    /// oversight: a TUN device carries both, the encapsulation has no PID for
    /// IPv6, and a packet whose shape is not understood is a packet the filter
    /// cannot judge. Something it cannot judge does not go out.
    /// </remarks>
    public static bool TryRead(ReadOnlySpan<byte> packet, out IpPacket ip)
    {
        ip = default;

        if (packet.Length < MinHeaderBytes)
            return false;

        if (packet[0] >> 4 != 4)
            return false;

        int headerLength = (packet[0] & 0x0F) * 4;
        if (headerLength < MinHeaderBytes || headerLength > packet.Length)
            return false;

        // A total length that disagrees with what we were handed is a packet
        // somebody has already mangled. The kernel does not produce them.
        int total = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (total < headerLength || total > packet.Length)
            return false;

        ip = new IpPacket(packet[..total]);
        return true;
    }

    public int HeaderLength => (bytes[0] & 0x0F) * 4;

    public int TotalLength => BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);

    public byte Protocol => bytes[9];

    public IPAddress Source => new(bytes[12..16]);

    public IPAddress Destination => new(bytes[16..20]);

    /// <summary>The source address as the 32 bits it is, in host order.</summary>
    public uint SourceValue => BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);

    /// <summary>The destination address as the 32 bits it is, in host order.</summary>
    public uint DestinationValue => BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);

    /// <summary>Offset of this fragment within the whole packet, in bytes.</summary>
    public int FragmentOffset => (BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]) & 0x1FFF) * 8;

    /// <summary>Whether more fragments follow this one.</summary>
    public bool MoreFragments => (bytes[6] & 0x20) != 0;

    /// <summary>Whether this packet is a fragment of a larger one.</summary>
    public bool IsFragment => MoreFragments || FragmentOffset > 0;

    /// <summary>
    /// The destination port, for TCP and UDP, when this is the first fragment.
    /// </summary>
    /// <remarks>
    /// Null for anything else, including a later fragment, which by
    /// construction does not carry the ports. A filter that matches on port
    /// therefore cannot match a trailing fragment; see
    /// <see cref="EgressFilter"/> for what is done about that.
    /// </remarks>
    public ushort? DestinationPort => TryReadPort(2);

    /// <summary>The source port, under the same conditions as <see cref="DestinationPort"/>.</summary>
    public ushort? SourcePort => TryReadPort(0);

    private ushort? TryReadPort(int offset)
    {
        if (Protocol is not (ProtocolTcp or ProtocolUdp))
            return null;
        if (FragmentOffset > 0)
            return null;

        var payload = bytes[HeaderLength..];
        return payload.Length < offset + 2
            ? null
            : BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
    }

    /// <summary>A one-line description, for logs and for filter drop messages.</summary>
    public string Describe()
    {
        var protocol = Protocol switch
        {
            ProtocolIcmp => "icmp",
            ProtocolTcp => "tcp",
            ProtocolUdp => "udp",
            _ => $"ip-protocol-{Protocol}",
        };

        var ports = SourcePort is { } sp && DestinationPort is { } dp ? $" {sp}->{dp}" : "";
        var fragment = IsFragment ? $" frag+{FragmentOffset}{(MoreFragments ? "+" : "")}" : "";
        return $"{protocol} {Source}->{Destination}{ports}{fragment} {TotalLength} bytes";
    }
}
