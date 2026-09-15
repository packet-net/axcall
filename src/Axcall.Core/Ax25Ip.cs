using Packet.Ax25;
using Packet.Core;

namespace Axcall;

/// <summary>
/// Carrying IP on an AX.25 channel: the frame shapes, and the sizes a real
/// implementation on the other end will actually accept.
/// </summary>
/// <remarks>
/// <para>
/// IP goes out in UI frames with PID 0xCC. The alternative, running IP inside
/// a connected-mode session, puts two retransmission timers on the same loss:
/// AX.25's T1 and TCP's RTO. TCP then measures a round trip that includes the
/// link layer's own retries, its estimator learns a number that has nothing to
/// do with the path, and throughput collapses. So datagram out.
/// </para>
/// <para>
/// Inbound is the opposite: accept both. The kernel's ax25rtd.conf(5) has a
/// per-route mode of "datagram" or "virtual connect", both are configured in
/// the field, and LinBPQ hands a received IP frame to its IP stack either way
/// (it records which one it arrived as, and uses that for the reply, but it
/// does not refuse either). A receiver that insisted on UI would be
/// unreachable from a peer whose route says vc, for no gain.
/// </para>
/// <para>
/// Everything goes direct. Layer 2 digipeating is not done anywhere in this
/// suite, for connected mode or for IP, so a station behind a repeater is not
/// reachable and is not pretended to be: one heard through a repeater is
/// deliberately not learned, because replying to it direct would go into the
/// ground.
/// </para>
/// </remarks>
public static class Ax25Ip
{
    /// <summary>
    /// The largest frame, excluding the FCS, that every peer tested so far
    /// will accept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a number from the specification, and it is not a property
    /// of AX.25. It is one observation about one implementation: LinBPQ
    /// 6.0.25.28 discards any KISS frame over 329 bytes including the KISS
    /// type byte, without a word on the air or in its log. A 544-byte frame
    /// was carried to its port by the channel simulator and vanished. 329
    /// minus the KISS type byte is 328.
    /// </para>
    /// <para>
    /// JNOS, XRouter and the kernel's own AX.25 stack have not been tested and
    /// may well take more, or less. Treat this as the default ceiling rather
    /// than a limit: <c>axtun --mtu</c> will go above it, and says so when it
    /// does.
    /// </para>
    /// </remarks>
    public const int MaxFrameBytes = 328;

    /// <summary>
    /// The largest frame this code will build at all.
    /// </summary>
    /// <remarks>
    /// A backstop rather than a measurement. AX.25 v2.2 negotiates N1 in an
    /// XID exchange and does not fix a ceiling worth quoting here, so this is
    /// simply a size past which a configuration is more likely to be a typo
    /// than an intention.
    /// </remarks>
    public const int AbsoluteMaxFrameBytes = 1024;

    /// <summary>Header bytes an IP-bearing frame spends before the payload.</summary>
    /// <remarks>Two addresses, control, PID. There are never any repeaters.</remarks>
    public const int HeaderBytes = (2 * Ax25Address.EncodedLength) + 2;

    /// <summary>
    /// The MTU to use when nobody says otherwise.
    /// </summary>
    /// <remarks>
    /// Chosen to sit under everything observed rather than to maximise
    /// throughput. LinBPQ fragments its own output at an IP total length of
    /// 252 and accepts up to 312 bytes of payload from us. The kernel's AX.25
    /// devices are documented as defaulting to 256, which has been read and
    /// not tested here. 236 clears all of that with room for a couple of
    /// room to spare, and at 1200 baud a full frame is already about 1.7
    /// seconds on air, so the last 20 bytes are not the problem.
    /// </remarks>
    public const int DefaultMtu = 236;

    /// <summary>The largest IP packet that fits inside <see cref="MaxFrameBytes"/>.</summary>
    public static int MaxMtu() => MaxFrameBytes - HeaderBytes;

    /// <summary>The largest IP packet this code will build at all.</summary>
    public static int AbsoluteMaxMtu() => AbsoluteMaxFrameBytes - HeaderBytes;

    /// <summary>Wrap an IP packet in a UI frame addressed to a station.</summary>
    /// <remarks>
    /// Direct, never through a repeater. Layer 2 digipeating is not done
    /// anywhere in this suite; see the remarks on <see cref="Ax25Ip"/>.
    /// </remarks>
    public static Ax25Frame Datagram(Callsign destination, Callsign source, ReadOnlySpan<byte> packet)
        => Ax25Frame.Ui(destination, source, packet, Ax25Pid.Ip);

    /// <summary>Wrap an ARP message in a UI frame.</summary>
    /// <remarks>
    /// A request goes to QST, the broadcast address every AX.25 implementation
    /// answers on; a reply goes back to whoever asked.
    /// </remarks>
    public static Ax25Frame Arp(Callsign destination, Callsign source, ReadOnlySpan<byte> message)
        => Ax25Frame.Ui(destination, source, message, Ax25Pid.Arp);

    /// <summary>
    /// The AX.25 broadcast destination, as used for ARP requests and beacons.
    /// </summary>
    public static readonly Callsign Broadcast = new("QST");

    /// <summary>
    /// The payload of a frame carrying <paramref name="pid"/>, whether it
    /// arrived as a UI frame or inside a connected-mode session.
    /// </summary>
    public static bool TryGetPayload(Ax25Frame frame, byte pid, out ReadOnlyMemory<byte> payload)
    {
        payload = default;

        if (frame.Pid != pid)
            return false;

        // UI and I are the two frame types that carry a PID and user data. A
        // frame with the right PID that is neither is not something to guess at.
        if (frame.FrameType is not (Ax25FrameType.Ui or Ax25FrameType.I))
            return false;

        payload = frame.Info;
        return payload.Length > 0;
    }
}
