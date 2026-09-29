using System.Globalization;
using System.Text;

namespace Axlisten;

internal enum ClassicDump
{
    /// <summary>The default: 64 characters a line, with an offset.</summary>
    Ascii,

    /// <summary><c>-h</c>: hex and ASCII, 16 bytes a line.</summary>
    Hex,

    /// <summary><c>-r</c>: the text as sent, one line per line.</summary>
    Readable,
}

/// <summary>
/// The output of the classic axlisten from ax25-apps, so scripts, log parsers
/// and fingers trained on it keep working.
/// </summary>
/// <remarks>
/// <para>
/// A port of <c>ax25dump.c</c> and the dump routines of <c>listen.c</c>. It
/// works on the raw bytes, as they did, rather than on a parsed frame, so a
/// malformed frame prints the way it always printed:
/// </para>
/// <code>
/// radio: fm M0LTE-7 to GB7RDG ctl SABM+ 18:45:51.123456
/// radio: fm GB7RDG to M0LTE-7 ctl UA- 18:45:51.520112
/// radio: fm M0LTE-7 to GB7RDG ctl I00^ pid=F0(Text) len 5 18:45:51.521003
/// 0000  hello
/// </code>
/// <para>
/// Deliberate differences, all additive: SREJ, XID and TEST are named rather
/// than printed as <c>[invalid]</c>; an I or UI frame with no PID still ends
/// its line; and the protocol decoders the original carried for ARP, IP,
/// NET/ROM, ROSE, FlexNet and OpenTRAC are not ported, so their payloads are
/// dumped like any other data. The friendly default output decodes the ones
/// that are still on the air.
/// </para>
/// </remarks>
internal sealed class ClassicFormatter : IFrameFormatter
{
    private enum T { Port, Data, Error, Protocol, AxHdr, Addr, Timestamp }

    // ax25dump.c's frame types: the control octet with the P/F bit masked off
    // for U frames, the low nibble for S frames.
    private const int I = 0x00, Rr = 0x01, Rnr = 0x05, Rej = 0x09, Srej = 0x0D;
    private const int Sabm = 0x2F, Sabme = 0x6F, Disc = 0x43, Dm = 0x0F, Ua = 0x63, Frmr = 0x87, Ui = 0x03;
    private const int Xid = 0xAF, Test = 0xE3;

    // The low two bits of a U frame's control octet.
    private const int U = 0x03;

    private const int Alen = 6, Axlen = 7;

    // IBM code page 437, 128 to 159, as listen.c mapped them for -i.
    private static readonly byte[] IbmMap =
    [
        199, 252, 233, 226, 228, 224, 229, 231,
        234, 235, 232, 239, 238, 236, 196, 197,
        201, 230, 198, 244, 246, 242, 251, 249,
        255, 214, 220, 162, 163, 165, 32, 32,
    ];

    private readonly ClassicDump dump;
    private readonly int timestamps;
    private readonly bool eightBit;
    private readonly bool ibm;
    private readonly bool color;

    private DateTimeOffset? previous;
    private DateTimeOffset? first;
    private DateTimeOffset now;

    /// <param name="dump">How to show the information field.</param>
    /// <param name="timestamps">
    /// How many times <c>-t</c> was given: 0 local time, 1 none, 2 Unix time,
    /// 3 since the previous frame, 4 date and local time, 5 since the first.
    /// </param>
    /// <param name="eightBit"><c>-8</c>: pass bytes above 127.</param>
    /// <param name="ibm"><c>-i</c>: map IBM code page 437 line drawing.</param>
    /// <param name="color"><c>-c</c>: colour, in ANSI rather than curses.</param>
    public ClassicFormatter(ClassicDump dump, int timestamps, bool eightBit, bool ibm, bool color)
    {
        if (timestamps is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(timestamps));

        this.dump = dump;
        this.timestamps = timestamps;
        this.eightBit = eightBit;
        this.ibm = ibm;
        this.color = color;
    }

    public string Format(MonitoredFrame frame)
    {
        var sb = new StringBuilder(128);
        now = frame.ReceivedAt;
        Out(sb, T.Port, $"{frame.Label}: ");
        Ax25Dump(sb, frame.Bytes.Span);
        return sb.ToString();
    }

    private void Ax25Dump(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        const int command = 1, response = 2;
        int cmdrsp;
        bool extseq;
        string dama;

        if (data.Length < 8)
        {
            Out(sb, T.Error, "AX25: bad header!\n");
            return;
        }

        if ((data[1] & 0x01) != 0)
        {
            // A FlexNet compressed header: a 14-bit circuit id and the
            // destination packed six bits a character.
            Out(sb, T.Protocol, " ");
            Span<char> call = stackalloc char[6];
            call[0] = (char)(' ' + (data[2] >> 2));
            call[1] = (char)(' ' + ((data[2] << 4) & 0x30) + (data[3] >> 4));
            call[2] = (char)(' ' + ((data[3] << 2) & 0x3C) + (data[4] >> 6));
            call[3] = (char)(' ' + (data[4] & 0x3F));
            call[4] = (char)(' ' + (data[5] >> 2));
            call[5] = (char)(' ' + ((data[5] << 4) & 0x30) + (data[6] >> 4));
            var name = new string(call).TrimStart(' ');
            var space = name.IndexOf(' ', StringComparison.Ordinal);
            if (space >= 0) name = name[..space];
            var ssid = (data[6] & 0x0F) != 0 ? $"-{data[6] & 0x0F}" : "";
            Out(sb, T.Addr, string.Create(CultureInfo.InvariantCulture,
                $"{(data[0] << 6) | ((data[1] >> 2) & 0x3F)}->{name}{ssid}"));
            cmdrsp = (data[1] & 0x02) != 0 ? command : response;
            dama = "";
            extseq = false;
            data = data[7..];
        }
        else
        {
            if (data.Length < Axlen + Axlen + 1)
            {
                Out(sb, T.Error, "AX25: bad header!\n");
                return;
            }

            extseq = (data[Axlen + Alen] & 0x40) != 0x40;
            if (extseq)
                Out(sb, T.Protocol, "EAX25: ");

            dama = (data[Axlen + Alen] & 0x20) == 0x20 ? "" : " [DAMA]";

            Out(sb, T.AxHdr, "fm ");
            Out(sb, T.Addr, Pax25(data[Axlen..]));
            Out(sb, T.AxHdr, " to ");
            Out(sb, T.Addr, Pax25(data));

            cmdrsp = 0;
            if ((data[Alen] & 0x80) != 0 && (data[Axlen + Alen] & 0x80) == 0) cmdrsp = command;
            if ((data[Axlen + Alen] & 0x80) != 0 && (data[Alen] & 0x80) == 0) cmdrsp = response;

            var end = (data[Axlen + Alen] & 0x01) != 0;
            data = data[(Axlen + Axlen)..];

            if (!end)
            {
                Out(sb, T.AxHdr, " via");
                while (!end)
                {
                    // The original walked off the end of a frame whose address
                    // field never terminated. This stops.
                    if (data.Length < Axlen)
                    {
                        Out(sb, T.Error, " AX25: bad header!\n");
                        return;
                    }
                    Out(sb, T.Addr, $" {Pax25(data)}{((data[Alen] & 0x80) != 0 ? "*" : "")}");
                    end = (data[Alen] & 0x01) != 0;
                    data = data[Axlen..];
                }
            }
        }

        if (data.Length == 0)
        {
            Out(sb, T.AxHdr, $"{dama} ");
            Timestamp(sb);
            Out(sb, T.AxHdr, "\n");
            return;
        }

        if (!TryType(data, extseq, out var type, out var ns, out var nr, out var pf, out var ctlen))
        {
            Out(sb, T.Error, " AX25: bad header!\n");
            return;
        }
        data = data[ctlen..];

        Out(sb, T.AxHdr, $" ctl {DecodeType(type)}");

        if ((type & 0x03) != U)
            Out(sb, T.AxHdr, nr.ToString(CultureInfo.InvariantCulture));
        if (type == I)
            Out(sb, T.AxHdr, ns.ToString(CultureInfo.InvariantCulture));

        if (cmdrsp == command) Out(sb, T.AxHdr, pf ? "+" : "^");
        else if (cmdrsp == response) Out(sb, T.AxHdr, pf ? "-" : "v");

        if (type is I or Ui)
        {
            if (data.Length == 0)
            {
                Out(sb, T.AxHdr, $"{dama} ");
                Timestamp(sb);
                Out(sb, T.AxHdr, "\n");
                return;
            }

            int pid = data[0];
            data = data[1..];
            Out(sb, T.AxHdr, string.Create(CultureInfo.InvariantCulture, $" pid={pid:X}{PidName(pid)}"));
            Out(sb, T.AxHdr, string.Create(CultureInfo.InvariantCulture, $"{dama} len {data.Length} "));
            Timestamp(sb);

            if (pid == 0x08 && data.Length > 0)
            {
                int seg = data[0];
                data = data[1..];
                Out(sb, T.AxHdr, string.Create(CultureInfo.InvariantCulture,
                    $"{((seg & 0x80) != 0 ? " First seg;" : "")} remain {seg & 0x7F}"));
                if ((seg & 0x80) != 0 && data.Length > 0)
                    data = data[1..];
            }
            Out(sb, T.AxHdr, "\n");
            DataDump(sb, data);
        }
        else if (type == Frmr && data.Length >= 3)
        {
            Out(sb, T.AxHdr, string.Create(CultureInfo.InvariantCulture, $": {data[0]:X2}"));
            Out(sb, T.AxHdr, string.Create(CultureInfo.InvariantCulture,
                $" Vr = {(data[1] >> 5) & 7} Vs = {(data[1] >> 1) & 7}"));
            if ((data[2] & 1) != 0) Out(sb, T.Error, " Invalid control field");
            if ((data[2] & 2) != 0) Out(sb, T.Error, " Illegal I-field");
            if ((data[2] & 4) != 0) Out(sb, T.Error, " Too-long I-field");
            if ((data[2] & 8) != 0) Out(sb, T.Error, " Invalid seq number");
            Out(sb, T.AxHdr, $"{dama} ");
            Timestamp(sb);
            Out(sb, T.AxHdr, "\n");
        }
        else if (type is Sabm or Ua && data.Length >= 2)
        {
            // FlexNet puts its header-compression handle in the SABM and UA.
            Out(sb, T.AxHdr, string.Create(CultureInfo.InvariantCulture, $" [{(data[0] << 8) | data[1]}]{dama} "));
            Timestamp(sb);
            Out(sb, T.AxHdr, "\n");
        }
        else
        {
            Out(sb, T.AxHdr, $"{dama} ");
            Timestamp(sb);
            Out(sb, T.AxHdr, "\n");
        }
    }

    private static bool TryType(ReadOnlySpan<byte> data, bool extseq, out int type, out int ns, out int nr, out bool pf, out int length)
    {
        ns = nr = 0;
        if (extseq)
        {
            if ((data[0] & 0x01) == 0)
            {
                type = I;
                ns = (data[0] >> 1) & 127;
                length = 2;
            }
            else if ((data[0] & 0x02) != 0)
            {
                type = data[0] & ~0x10;
                pf = (data[0] & 0x10) != 0;
                length = 1;
                return true;
            }
            else
            {
                type = data[0];
                length = 2;
            }

            if (data.Length < 2)
            {
                pf = false;
                return false;
            }
            nr = (data[1] >> 1) & 127;
            pf = (data[1] & 0x01) != 0;
            return true;
        }

        length = 1;
        pf = (data[0] & 0x10) != 0;
        if ((data[0] & 0x01) == 0)
        {
            type = I;
            ns = (data[0] >> 1) & 7;
            nr = (data[0] >> 5) & 7;
        }
        else if ((data[0] & 0x02) != 0)
        {
            type = data[0] & ~0x10;
        }
        else
        {
            type = data[0] & 0x0F;
            nr = (data[0] >> 5) & 7;
        }
        return true;
    }

    private static string DecodeType(int type) => type switch
    {
        I => "I",
        Sabm => "SABM",
        Sabme => "SABME",
        Disc => "DISC",
        Dm => "DM",
        Ua => "UA",
        Rr => "RR",
        Rnr => "RNR",
        Rej => "REJ",
        Srej => "SREJ",
        Frmr => "FRMR",
        Ui => "UI",
        Xid => "XID",
        Test => "TEST",
        _ => "[invalid]",
    };

    private static string PidName(int pid) => pid switch
    {
        0x08 => "(segment)",
        0xCD => "(ARP)",
        0xCF => "(NET/ROM)",
        0xCC => "(IP)",
        0x01 => "(X.25)",
        0xC3 => "(TEXNET)",
        0xCE => "(FLEXNET)",
        0x77 => "(OPENTRAC)",
        0xF0 => "(Text)",
        _ => "",
    };

    /// <summary>An address slot the way pax25() printed it: spaces dropped, SSID only if set.</summary>
    private static string Pax25(ReadOnlySpan<byte> slot)
    {
        var sb = new StringBuilder(9);
        for (int i = 0; i < Alen; i++)
        {
            var c = (char)((slot[i] >> 1) & 0x7F);
            if (!char.IsAsciiLetterOrDigit(c) && c != ' ')
                return "[invalid]";
            if (c != ' ')
                sb.Append(c);
        }
        var ssid = (slot[Alen] >> 1) & 0x0F;
        if (ssid != 0)
            sb.Append(CultureInfo.InvariantCulture, $"-{ssid}");
        return sb.ToString();
    }

    private void Timestamp(StringBuilder sb)
    {
        var t = now;
        first ??= t;

        string? text = timestamps switch
        {
            0 => Clock(t.ToLocalTime().TimeOfDay),
            1 => null,
            2 => string.Create(CultureInfo.InvariantCulture,
                $"{t.ToUnixTimeSeconds()}.{t.Ticks % TimeSpan.TicksPerSecond / 10:D6}"),
            3 => Clock(t - (previous ?? t)),
            4 => t.ToLocalTime().ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) + Clock(t.ToLocalTime().TimeOfDay),
            _ => Clock(t - first.Value),
        };
        previous = t;

        if (text is not null)
            Out(sb, T.Timestamp, text + " ");
    }

    // ts_format(): hours wrap at a day, and there are always six decimals.
    private static string Clock(TimeSpan time)
        => string.Create(CultureInfo.InvariantCulture,
            $"{(int)time.TotalHours % 24:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Ticks % TimeSpan.TicksPerSecond / 10:D6}");

    private void DataDump(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        switch (dump)
        {
            case ClassicDump.Hex: HexDump(sb, data); break;
            case ClassicDump.Readable: ReadableDump(sb, data); break;
            default: AsciiDump(sb, data); break;
        }
    }

    private void AsciiDump(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i += 64)
        {
            var line = new StringBuilder(70);
            line.Append(CultureInfo.InvariantCulture, $"{i:X4}  ");
            foreach (var c in data.Slice(i, Math.Min(64, data.Length - i)))
                line.Append(c is 0 or (byte)'\n' ? '.' : (char)c);
            line.Append('\n');
            Out(sb, T.Data, line.ToString());
        }
    }

    private void HexDump(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i += 16)
        {
            var row = data.Slice(i, Math.Min(16, data.Length - i));
            var line = new StringBuilder(76);
            line.Append(CultureInfo.InvariantCulture, $"{i:X4}  ");
            for (int j = 0; j < 16; j++)
                line.Append(j < row.Length ? string.Create(CultureInfo.InvariantCulture, $"{row[j]:X2} ") : "   ");
            line.Append(" | ");
            foreach (var c in row)
                line.Append(c is 0 or (byte)'\n' ? '.' : (char)c);
            line.Append('\n');
            Out(sb, T.Data, line.ToString());
        }
    }

    private void ReadableDump(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        // Every CR, LF or NUL ends a line, and a run of them ends only one.
        // NUL is in that list by accident of a missing break in the original,
        // and has behaved that way ever since.
        var line = new StringBuilder(data.Length + 1);
        bool atLineStart = false;
        foreach (var c in data)
        {
            if (c is 0x00 or 0x0A or 0x0D)
            {
                if (!atLineStart) line.Append('\n');
                atLineStart = true;
                continue;
            }
            line.Append((char)c);
            atLineStart = false;
        }
        if (!atLineStart)
            line.Append('\n');
        Out(sb, T.Data, line.ToString());
    }

    /// <summary>
    /// lprintf(): control characters other than newline become dots, and so
    /// does the top half of the byte range unless <c>-8</c> was given.
    /// </summary>
    private void Out(StringBuilder sb, T type, string text)
    {
        if (color) sb.Append(Colour(type));

        foreach (var ch in text)
        {
            var c = ch;
            if (c < 32 && c != '\n')
                c = '.';
            else if (c >= 127 && c < 256 && type == T.Data)
            {
                if (!eightBit || c == 127)
                    c = '.';
                else if (c < 160)
                    c = ibm ? (char)IbmMap[c - 128] : '.';
            }
            sb.Append(c);
        }

        if (color) sb.Append("\e[0m");
    }

    // listen.c's curses pairs, in ANSI.
    private static string Colour(T type) => type switch
    {
        T.Port => "\e[32m",
        T.Error => "\e[31m",
        T.Protocol => "\e[1;36m",
        T.AxHdr => "\e[1;37m",
        T.Addr => "\e[1;32m",
        T.Timestamp => "\e[1;33m",
        _ => "\e[37m",
    };
}
