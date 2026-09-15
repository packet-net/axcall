using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Axcall;

/// <summary>
/// One line per frame, in either direction, for <c>-d</c>.
/// </summary>
/// <remarks>
/// <para>
/// The kernel version's <c>-d</c> set <c>SO_DEBUG</c> on the AX.25 socket, which
/// meant kernel tracing. <see cref="Ax25Listener.FrameTraced"/> gives us the
/// parsed frame and its direction instead, which is strictly more useful.
/// </para>
/// <para>
/// The shape follows ham convention: direction, addresses, frame type, then C or
/// R for command or response and P or F for the poll/final bit, then whatever
/// that frame type actually carries. Plain ASCII, so it survives a pipe into
/// journalctl:
/// </para>
/// <code>
/// 18:45:51.123 > M0LTE-7>GB7RDG SABM C P
/// 18:45:51.520 &lt; GB7RDG>M0LTE-7 UA R F
/// 18:45:51.521 > M0LTE-7>GB7RDG I C P ns=0 nr=0 pid=F0 len=12
/// 18:45:52.004 &lt; GB7RDG>M0LTE-7 RR R F nr=1
/// </code>
/// </remarks>
public static class FrameTrace
{
    /// <summary>Format one traced frame. Never throws: tracing must not take the link down.</summary>
    public static string Format(Ax25FrameEventArgs e)
    {
        var sb = new StringBuilder(80);

        sb.Append(e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(e.Direction == FrameDirection.Transmitted ? " > " : " < ");

        var frame = e.Frame;
        sb.Append(frame.Source.Callsign);
        sb.Append('>');
        sb.Append(frame.Destination.Callsign);

        if (frame.Digipeaters.Count > 0)
        {
            sb.Append(" via ");
            for (int i = 0; i < frame.Digipeaters.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(frame.Digipeaters[i].Callsign);
                // A set C/H bit on a digipeater means that hop has repeated it.
                if (frame.Digipeaters[i].CrhBit) sb.Append('*');
            }
        }

        var type = frame.FrameType;
        sb.Append(' ');
        sb.Append(Name(type));

        // The address-field C bits encode command or response (section 6.1.2).
        // A frame with neither or both set is a v1 legacy frame, and is left
        // unlabelled rather than guessed at.
        if (frame.IsCommand) sb.Append(" C");
        else if (frame.IsResponse) sb.Append(" R");

        // The same bit is "poll" on a command and "final" on a response.
        if (frame.PollFinal) sb.Append(frame.IsResponse ? " F" : " P");

        // N(S) and N(R) live in bits that mean something else on other frame
        // types, so each is printed only where it exists.
        if (type is Ax25FrameType.I)
        {
            sb.Append(CultureInfo.InvariantCulture, $" ns={frame.Ns} nr={frame.Nr}");
        }
        else if (type is Ax25FrameType.Rr or Ax25FrameType.Rnr or Ax25FrameType.Rej or Ax25FrameType.Srej)
        {
            sb.Append(CultureInfo.InvariantCulture, $" nr={frame.Nr}");
        }

        if (frame.Pid is byte pid)
        {
            sb.Append(CultureInfo.InvariantCulture, $" pid={pid:X2}");
        }

        if (frame.Info.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" len={frame.Info.Length}");
        }

        return sb.ToString();
    }

    // Upper-case spelling, as the spec and every other monitor writes them.
    private static string Name(Ax25FrameType type) => type switch
    {
        Ax25FrameType.I => "I",
        Ax25FrameType.Rr => "RR",
        Ax25FrameType.Rnr => "RNR",
        Ax25FrameType.Rej => "REJ",
        Ax25FrameType.Srej => "SREJ",
        Ax25FrameType.Ui => "UI",
        Ax25FrameType.Sabm => "SABM",
        Ax25FrameType.Sabme => "SABME",
        Ax25FrameType.Disc => "DISC",
        Ax25FrameType.Ua => "UA",
        Ax25FrameType.Dm => "DM",
        Ax25FrameType.Frmr => "FRMR",
        Ax25FrameType.Xid => "XID",
        Ax25FrameType.Test => "TEST",
        _ => "?",
    };
}
