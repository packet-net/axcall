using System.Buffers.Binary;
using System.Net;
using Packet.Core;

namespace Axcall;

/// <summary>Which half of an ARP exchange a message is.</summary>
public enum AxArpOperation
{
    Request = 1,
    Reply = 2,
}

/// <summary>
/// An ARP message as it travels on an AX.25 channel: ordinary RFC 826 ARP with
/// callsigns where the hardware addresses go.
/// </summary>
/// <remarks>
/// <para>
/// Thirty bytes, in a UI frame with PID 0xCD:
/// </para>
/// <code>
/// 0  hardware type       2   always 3, "AX.25 level 2"
/// 2  protocol type       2   see ProtocolType below
/// 4  hardware length     1   always 7, one AX.25 address slot
/// 5  protocol length     1   always 4, an IPv4 address
/// 6  operation           2   1 request, 2 reply
/// 8  sender callsign     7
/// 15 sender address      4
/// 19 target callsign     7
/// 26 target address      4
/// </code>
/// <para>
/// This exists because a station that is not in our map has no other way to
/// become reachable. The outbound direction is resolved from the config file,
/// deliberately: a static answer beats a discovery protocol for deciding who
/// to key the transmitter at. But refusing to answer an ARP request makes us
/// undiscoverable to anyone who has not hand-configured us, which is most
/// people, so we answer.
/// </para>
/// </remarks>
/// <param name="Operation">Request or reply.</param>
/// <param name="SenderCallsign">The station the message is from.</param>
/// <param name="SenderAddress">Its IP address.</param>
/// <param name="TargetCallsign">The station being asked about; all zeroes in a request.</param>
/// <param name="TargetAddress">The address being asked about.</param>
/// <param name="ProtocolType">
/// The protocol type field as it appeared on the wire, carried so a reply can
/// hand back what the request used. See <see cref="ProtocolTypeIp"/>.
/// </param>
public sealed record AxArpMessage(
    AxArpOperation Operation,
    Callsign SenderCallsign,
    IPAddress SenderAddress,
    Callsign TargetCallsign,
    IPAddress TargetAddress,
    ushort ProtocolType = AxArpMessage.ProtocolTypeIp)
{
    /// <summary>Bytes an AX.25 ARP message occupies.</summary>
    public const int WireLength = 30;

    /// <summary>ARP hardware type 3, "AX.25 level 2" in the IANA registry.</summary>
    public const ushort HardwareTypeAx25 = 3;

    /// <summary>
    /// AX25_P_IP: the AX.25 PID for IP widened to sixteen bits. The protocol
    /// type every implementation puts in an AX.25 ARP message, and the only
    /// one the Linux kernel will answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Observed from LinBPQ, from XRouter, and from the Linux kernel's own
    /// AX.25 stack, all three of which send exactly this. The kernel is the
    /// one that also <em>checks</em> it: an ARP request carrying anything else
    /// is discarded without a reply. Measured by asking a kernel peer for its
    /// own address twice, once with each value: 0x0800 got silence, 0x00CC got
    /// an answer.
    /// </para>
    /// <para>
    /// This is not a free choice and it is not the obvious one. RFC 826's
    /// protocol type field would naturally hold ETH_P_IP, and the kernel's
    /// generic ARP code fills it in that way for every other link type;
    /// <c>arp_create</c> has an explicit ARPHRD_AX25 case that overrides it,
    /// and <c>arp_process</c> has the matching check. Reading half of that and
    /// guessing is how this shipped as 0x0800 in the first place, which would
    /// have meant no Linux peer ever answering an ARP request from us.
    /// </para>
    /// </remarks>
    public const ushort ProtocolTypeIp = 0x00CC;

    /// <summary>
    /// ETH_P_IP, the value RFC 826 would lead you to expect in this field.
    /// </summary>
    /// <remarks>
    /// Accepted on receive and never sent. Nothing observed puts it here, and
    /// the Linux kernel actively refuses it, but accepting it costs nothing
    /// and there is no reason to be the implementation that rejects a
    /// reasonable reading of the RFC.
    /// </remarks>
    public const ushort ProtocolTypeEthernetIp = 0x0800;

    private const int HardwareLength = Ax25Address.EncodedLength;
    private const int ProtocolLength = 4;

    /// <summary>Encode this message into its 30 bytes.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[WireLength];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>Encode this message into a span of at least <see cref="WireLength"/> bytes.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < WireLength)
            throw new ArgumentException($"an ARP message needs {WireLength} bytes of room (got {destination.Length})", nameof(destination));

        BinaryPrimitives.WriteUInt16BigEndian(destination, HardwareTypeAx25);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], ProtocolType);
        destination[4] = HardwareLength;
        destination[5] = ProtocolLength;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], (ushort)Operation);

        // The extension bit is set on both slots. It means nothing here, since
        // these are not header slots, but it is what every implementation
        // emits and there is no reason to be the odd one out.
        new Ax25Address(SenderCallsign, CrhBit: false, ExtensionBit: true).Write(destination[8..]);
        SenderAddress.TryWriteBytes(destination[15..], out _);
        new Ax25Address(TargetCallsign, CrhBit: false, ExtensionBit: true).Write(destination[19..]);
        TargetAddress.TryWriteBytes(destination[26..], out _);
    }

    /// <summary>
    /// Read an ARP message, or fail without throwing on anything that is not
    /// one we can act on.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, out AxArpMessage? message)
    {
        message = null;

        if (bytes.Length < WireLength)
            return false;

        if (BinaryPrimitives.ReadUInt16BigEndian(bytes) != HardwareTypeAx25)
            return false;

        // Read but not checked, so a peer using ETH_P_IP is still heard;
        // see ProtocolTypeIp for why we only ever send AX25_P_IP.
        var protocolType = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);

        if (bytes[4] != HardwareLength || bytes[5] != ProtocolLength)
            return false;

        var operation = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        if (operation is not ((ushort)AxArpOperation.Request or (ushort)AxArpOperation.Reply))
            return false;

        // The sender slot has to decode: a message from nobody is not
        // actionable. The target slot routinely does not, because a request
        // leaves it all zeroes, which is the question being asked.
        if (!TryReadCallsign(bytes[8..15], out var sender))
            return false;
        _ = TryReadCallsign(bytes[19..26], out var target);

        message = new AxArpMessage(
            (AxArpOperation)operation,
            sender,
            new IPAddress(bytes[15..19]),
            target,
            new IPAddress(bytes[26..30]),
            protocolType);
        return true;
    }

    /// <summary>The unknown station in a request: an empty callsign, not a null one.</summary>
    public static readonly Callsign NoCallsign = new("");

    private static bool TryReadCallsign(ReadOnlySpan<byte> slot, out Callsign callsign)
    {
        try
        {
            callsign = Ax25Address.Read(slot).Callsign;
            return callsign.Base.Length > 0;
        }
        catch (ArgumentException)
        {
            // An all-zero or otherwise unreadable slot. Not an error in a
            // request, where the target is exactly what is being asked about.
            callsign = NoCallsign;
            return false;
        }
    }
}
