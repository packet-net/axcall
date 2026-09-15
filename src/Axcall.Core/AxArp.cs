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
    /// ETH_P_IP. What the Linux kernel is expected to put in the protocol type
    /// field of an ARP message on an AX.25 device, because the field is filled
    /// in by the generic ARP code from the protocol rather than by anything
    /// AX.25 specific.
    /// </summary>
    /// <remarks>
    /// Read from the kernel's ARP path, not observed on the air. Nothing here
    /// has yet been tested against a kernel AX.25 peer, so this is the value
    /// most likely to be right rather than the value known to be right. It is
    /// the one sent, which makes it the assumption worth checking first if a
    /// kernel peer ever ignores an ARP request from us.
    /// </remarks>
    public const ushort ProtocolTypeIp = 0x0800;

    /// <summary>
    /// 0x00CC, the AX.25 PID for IP widened to sixteen bits. What LinBPQ puts
    /// in the protocol type field.
    /// </summary>
    /// <remarks>
    /// Two values in one field. LinBPQ does not check it: it dispatches on the
    /// operation code alone and reflects whatever it was sent, which was
    /// confirmed by sending it one of each and reading the replies. The
    /// NOS-derived stacks are widely said to use this value too; that has not
    /// been tested here. So we send <see cref="ProtocolTypeIp"/>, accept
    /// anything, and echo back what a request used, which is the only
    /// behaviour that cannot be wrong about a field two implementations
    /// disagree on.
    /// </remarks>
    public const ushort ProtocolTypeBpq = 0x00CC;

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

        // The protocol type is read but not checked; see ProtocolTypeBpq.
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
