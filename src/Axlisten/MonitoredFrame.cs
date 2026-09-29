using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;

namespace Axlisten;

/// <summary>One frame heard on one port, as it came off the TNC.</summary>
/// <param name="Port">The port's name: a ports-file name, or the spec as written.</param>
/// <param name="KissPort">The KISS port nibble. Non-zero only on a multi-channel TNC.</param>
/// <param name="ReceivedAt">When the frame arrived.</param>
/// <param name="Bytes">The AX.25 frame, without flags or FCS.</param>
/// <param name="Radio">Signal metadata, for the TNCs that report it.</param>
internal sealed record MonitoredFrame(
    string Port,
    byte KissPort,
    DateTimeOffset ReceivedAt,
    ReadOnlyMemory<byte> Bytes,
    RadioMetadata? Radio = null)
{
    private Ax25Frame? frame;
    private bool parsed;

    /// <summary>The port as printed: the name, plus the channel on a multi-channel TNC.</summary>
    public string Label => KissPort == 0 ? Port : $"{Port}[{KissPort}]";

    /// <summary>The decoded frame, or null for bytes that are not one.</summary>
    public Ax25Frame? Frame
    {
        get
        {
            if (!parsed)
            {
                frame = Parse(Bytes.Span, LooksExtended(Bytes.Span));
                parsed = true;
            }
            return frame;
        }
    }

    /// <summary>
    /// True when the source SSID's first reserved bit is clear, which is how
    /// the Linux stack marked a modulo-128 link.
    /// </summary>
    /// <remarks>
    /// A monitor was not party to the negotiation, and an I or S frame's
    /// control field is a different width under each modulus, so this bit is
    /// all it has to go on. It is what the classic axlisten read. Stations that
    /// leave the bit set on a modulo-128 link, Direwolf and (until
    /// packet-net/packet.net#859) this suite among them, are read as modulo 8.
    /// </remarks>
    public static bool LooksExtended(ReadOnlySpan<byte> bytes)
        => bytes.Length > 13 && (bytes[13] & 0x40) == 0;

    /// <summary>
    /// Parse a frame, reading an I or S frame's control field as two octets
    /// when <paramref name="extended"/> is set, and falling back to one if
    /// that reading fails.
    /// </summary>
    public static Ax25Frame? Parse(ReadOnlySpan<byte> bytes, bool extended = false)
    {
        try
        {
            if (extended
                && Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: true, out var wide)
                && wide is not null)
            {
                return wide;
            }

            return Ax25Frame.TryParse(bytes, out var normal) ? normal : null;
        }
        catch (ArgumentException)
        {
            // Off the air, anything is possible. A monitor must not die of it.
            return null;
        }
    }
}
