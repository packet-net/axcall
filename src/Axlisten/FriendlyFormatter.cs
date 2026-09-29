using System.Globalization;
using System.Text;
using Axcall;
using Packet.Ax25;

namespace Axlisten;

/// <summary>Turns a heard frame into what is written for it, newline included.</summary>
internal interface IFrameFormatter
{
    string Format(MonitoredFrame frame);
}

internal enum TimeStyle
{
    Local,
    Utc,
    None,
}

/// <summary>
/// The default output: one header line per frame in the same shape as
/// <c>axcall -d</c>, then what the frame carries, decoded, indented under it.
/// </summary>
/// <remarks>
/// <code>
/// 18:45:51.123 radio  G4ABC-1>APRS via WIDE1-1* UI C pid=F0 len=24
///                     APRS position 51.5000 -0.1667, House QTH (VHF)
///                     Test
/// 18:45:52.004 radio  GB7RDG>NODES UI C pid=CF len=48
///                     NET/ROM nodes from RDG, 2 routes
/// </code>
/// </remarks>
internal sealed class FriendlyFormatter(bool color, TimeStyle time, bool alwaysHex, int portWidth) : IFrameFormatter
{
    // Grows to fit a channel label such as "radio[1]", which cannot be known
    // until a multi-channel TNC sends one.
    private int portWidth = portWidth;

    // SGR codes. Kept to the eight basic colours and bold/dim, which every
    // terminal anyone runs a monitor in has.
    private const string Reset = "\e[0m";
    private const string Dim = "\e[2m";
    private const string Bold = "\e[1m";
    private const string Cyan = "\e[36m";
    private const string Yellow = "\e[33m";
    private const string Green = "\e[32m";
    private const string Red = "\e[31m";

    public string Format(MonitoredFrame frame)
    {
        var sb = new StringBuilder(128);

        switch (time)
        {
            case TimeStyle.Local:
                Paint(sb, Dim, frame.ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                sb.Append(' ');
                break;
            case TimeStyle.Utc:
                Paint(sb, Dim, frame.ReceivedAt.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z");
                sb.Append(' ');
                break;
        }

        portWidth = Math.Max(portWidth, frame.Label.Length);
        Paint(sb, Cyan, frame.Label.PadRight(portWidth));
        sb.Append("  ");

        // Everything under the header lines up with where the frame starts,
        // so the eye can run down the left edge of the addresses.
        var indent = new string(' ', VisibleLength(sb));

        if (frame.Frame is not { } ax25)
        {
            Paint(sb, Red, $"unreadable frame, {frame.Bytes.Length} bytes");
            sb.Append('\n');
            var raw = new List<PayloadLine>();
            PayloadDecoder.HexDump(frame.Bytes.Span, raw);
            AppendLines(sb, indent, raw);
            return sb.ToString();
        }

        var header = new StringBuilder(80);
        FrameTrace.AppendFrame(header, ax25);
        if (frame.Radio is { } radio)
        {
            if (radio.RssiDbm is { } rssi) header.Append(CultureInfo.InvariantCulture, $" rssi={rssi:0.#}dBm");
            if (radio.SnrDb is { } snr) header.Append(CultureInfo.InvariantCulture, $" snr={snr:0.#}dB");
        }
        Paint(sb, HeaderColour(ax25.FrameType), header.ToString());
        sb.Append('\n');

        AppendLines(sb, indent, PayloadDecoder.Describe(ax25, alwaysHex));
        return sb.ToString();
    }

    private void AppendLines(StringBuilder sb, string indent, List<PayloadLine> lines)
    {
        foreach (var line in lines)
        {
            sb.Append(indent);
            switch (line.Kind)
            {
                case LineKind.Summary: Paint(sb, Green, line.Text); break;
                case LineKind.Hex: Paint(sb, Dim, line.Text); break;
                default: sb.Append(line.Text); break;
            }
            sb.Append('\n');
        }
    }

    // Frames that carry something stand out; the link's own housekeeping
    // recedes, and connects and disconnects are marked, since those are the
    // moments people scroll back looking for.
    private static string HeaderColour(Ax25FrameType type) => type switch
    {
        Ax25FrameType.I or Ax25FrameType.Ui => Bold,
        Ax25FrameType.Rr or Ax25FrameType.Rnr or Ax25FrameType.Rej or Ax25FrameType.Srej => Dim,
        _ => Yellow,
    };

    private void Paint(StringBuilder sb, string code, string text)
    {
        if (color) sb.Append(code).Append(text).Append(Reset);
        else sb.Append(text);
    }

    private static int VisibleLength(StringBuilder sb)
    {
        int length = 0;
        bool escape = false;
        for (int i = 0; i < sb.Length; i++)
        {
            var c = sb[i];
            if (escape) { if (c == 'm') escape = false; continue; }
            if (c == '\e') { escape = true; continue; }
            length++;
        }
        return length;
    }
}
