using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Axlisten;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// What axlisten prints for a frame: the friendly default, JSON, and the
/// classic ax25-apps layout.
/// </summary>
public sealed class AxlistenFormatTests
{
    private static readonly Callsign Us = new("M0LTE", 7);
    private static readonly Callsign Them = new("GB7RDG", 0);
    private static readonly DateTimeOffset At = new(2026, 9, 14, 18, 45, 51, 123, TimeSpan.Zero);

    private static MonitoredFrame Heard(Ax25Frame frame, string port = "radio", byte kissPort = 0, DateTimeOffset? at = null, RadioMetadata? radio = null)
        => new(port, kissPort, at ?? At, frame.ToBytes(), radio);

    private static MonitoredFrame Heard(byte[] bytes)
        => new("radio", 0, At, bytes);

    private static string Friendly(MonitoredFrame frame, bool hex = false, bool color = false)
        => new FriendlyFormatter(color, TimeStyle.None, hex, portWidth: 5).Format(frame);

    private static string Classic(MonitoredFrame frame, ClassicDump dump = ClassicDump.Ascii, int t = 1, bool eightBit = false)
        => new ClassicFormatter(dump, t, eightBit, ibm: false, color: false).Format(frame);

    private static Ax25Frame Ui(string text, Callsign? to = null, byte pid = Ax25Pid.NoLayer3, params Callsign[] via)
        => Ax25Frame.Ui(to ?? new Callsign("APRS", 0), Us, Encoding.UTF8.GetBytes(text), pid, digipeaters: via);

    private static Ax25Frame Ui(byte[] info, byte pid, Callsign? to = null)
        => Ax25Frame.Ui(to ?? new Callsign("QST", 0), Us, info, pid);

    // --- friendly ----------------------------------------------------------

    [Fact]
    public void Friendly_Shows_The_Axcall_Trace_Line_Then_The_Text_Beneath_It()
    {
        Friendly(Heard(Ui("Hello\rworld")))
            .Should().Be(
                "radio  M0LTE-7>APRS UI C pid=F0 len=11\n"
                + "       Hello\n"
                + "       world\n");
    }

    [Fact]
    public void Friendly_Link_Frames_Are_One_Line()
    {
        Friendly(Heard(Ax25Frame.Sabm(Them, Us))).Should().Be("radio  M0LTE-7>GB7RDG SABM C P\n");
    }

    [Fact]
    public void Friendly_Pads_The_Port_And_Marks_A_Kiss_Channel()
    {
        new FriendlyFormatter(false, TimeStyle.None, false, portWidth: 9)
            .Format(Heard(Ax25Frame.Sabm(Them, Us), "vhf", kissPort: 1))
            .Should().StartWith("vhf[1]     M0LTE-7>GB7RDG");
    }

    [Fact]
    public void Friendly_Utc_Time_Is_Marked_As_Such()
    {
        new FriendlyFormatter(false, TimeStyle.Utc, false, 5).Format(Heard(Ax25Frame.Sabm(Them, Us)))
            .Should().StartWith("18:45:51.123Z radio  M0LTE-7");
    }

    [Fact]
    public void Friendly_Shows_Digipeaters_And_Which_Have_Repeated()
    {
        var frame = Ui("hi", null, Ax25Pid.NoLayer3, new Callsign("WIDE1", 1), new Callsign("WIDE2", 1));
        var bytes = frame.ToBytes();
        bytes[20] |= 0x80; // WIDE1-1 has repeated it
        Friendly(Heard(bytes)).Should().StartWith("radio  M0LTE-7>APRS via WIDE1-1*,WIDE2-1 UI C");
    }

    [Fact]
    public void Control_Characters_Never_Reach_The_Terminal()
    {
        // An escape sequence off the air could retitle the terminal or worse.
        var output = Friendly(Heard(Ui("evil\e]0;owned\agone")));
        output.Should().NotContain("\e").And.NotContain("\a");
        output.Should().Contain("evil.]0;owned.gone");
    }

    [Fact]
    public void Binary_Is_Hex_Dumped()
    {
        var output = Friendly(Heard(Ui([0x00, 0x01, 0x02, 0x41, 0xFF], pid: 0xF0)));
        output.Should().Contain("0000  00 01 02 41 ff").And.Contain("|...A.|");
    }

    [Fact]
    public void Hex_Adds_A_Dump_Under_Text_Too()
    {
        Friendly(Heard(Ui("Hi")), hex: true).Should().Contain("       Hi\n").And.Contain("0000  48 69");
    }

    [Fact]
    public void Utf8_Text_Is_Shown_As_Written()
    {
        Friendly(Heard(Ui("73 de Zoë"))).Should().Contain("73 de Zoë");
    }

    [Fact]
    public void Ip_Is_Summarised()
    {
        var packet = new byte[28];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 28);
        packet[8] = 64;
        packet[9] = 1; // ICMP
        IPAddress.Parse("44.131.20.1").TryWriteBytes(packet.AsSpan(12), out _);
        IPAddress.Parse("44.131.20.10").TryWriteBytes(packet.AsSpan(16), out _);

        Friendly(Heard(Ui(packet, Ax25Pid.Ip)))
            .Should().Contain("       IP icmp 44.131.20.1->44.131.20.10 28 bytes\n");
    }

    [Fact]
    public void Arp_Is_Summarised()
    {
        var request = new AxArpMessage(AxArpOperation.Request, Us, IPAddress.Parse("44.131.20.1"),
            AxArpMessage.NoCallsign, IPAddress.Parse("44.131.20.10"));
        Friendly(Heard(Ui(request.ToBytes(), Ax25Pid.Arp)))
            .Should().Contain("ARP who has 44.131.20.10? tell 44.131.20.1 (M0LTE-7)");
    }

    private static byte[] Slot(Callsign call)
    {
        var slot = new byte[7];
        new Ax25Address(call, CrhBit: false, ExtensionBit: false).Write(slot);
        return slot;
    }

    [Fact]
    public void NetRom_Nodes_Broadcasts_List_Their_Routes()
    {
        var info = new List<byte> { 0xFF };
        info.AddRange("RDG   "u8.ToArray());
        info.AddRange(Slot(new Callsign("GB7CIP", 0)));
        info.AddRange("CIP   "u8.ToArray());
        info.AddRange(Slot(new Callsign("GB7RDG", 1)));
        info.Add(192);

        var output = Friendly(Heard(Ui([.. info], Ax25Pid.NetRom, new Callsign("NODES", 0))));
        output.Should().Contain("NET/ROM nodes from RDG, 1 route\n");
        output.Should().Contain("CIP:GB7CIP").And.Contain("via GB7RDG-1").And.Contain("quality 192");
    }

    [Fact]
    public void NetRom_Info_Shows_The_Circuit_And_The_Text()
    {
        var info = new List<byte>();
        info.AddRange(Slot(new Callsign("GB7RDG", 0)));
        info.AddRange(Slot(new Callsign("GB7CIP", 0)));
        info.AddRange(new byte[] { 7, 0x01, 0x02, 3, 4, 0x05 });
        info.AddRange("hello\r"u8.ToArray());

        var frame = Ax25Frame.I(new Callsign("GB7CIP", 0), Them, nr: 0, ns: 0, info: [.. info], pid: Ax25Pid.NetRom);
        var output = Friendly(Heard(frame));
        output.Should().Contain("NET/ROM GB7RDG>GB7CIP ttl=7 INFO circuit=0102 tx=3 rx=4\n");
        output.Should().Contain("       hello\n");
    }

    [Fact]
    public void An_Unreadable_Frame_Is_Dumped_Not_Dropped()
    {
        var output = Friendly(Heard([0x01, 0x02, 0x03]));
        output.Should().Contain("unreadable frame, 3 bytes").And.Contain("0000  01 02 03");
    }

    [Fact]
    public void Signal_Reports_Are_Shown_When_The_Tnc_Gives_Them()
    {
        var radio = new RadioMetadata(-87.5f, 12f, null, null, null, null, null, null, null, null);
        Friendly(Heard(Ax25Frame.Sabm(Them, Us), radio: radio)).Should().Contain("SABM C P rssi=-87.5dBm snr=12dB");
    }

    [Fact]
    public void Colour_Is_Only_There_When_Asked_For()
    {
        Friendly(Heard(Ui("hi"))).Should().NotContain("\e[");
        Friendly(Heard(Ui("hi")), color: true).Should().Contain("\e[");
    }

    // --- modulo 128 ----------------------------------------------------------

    [Fact]
    public void A_Link_Seen_Set_Up_With_Sabme_Is_Read_As_Modulo_128()
    {
        var tracker = new LinkTracker();
        tracker.Observe(Heard(Ax25Frame.Sabme(Them, Us)));

        // This suite's own frames leave the source's reserved bit set, so only
        // having seen the SABME says what width the control field is.
        var info = Heard(Ax25Frame.I(Them, Us, nr: 100, ns: 90, info: "hi"u8, extended: true));
        tracker.Observe(info);

        info.Frame!.Ns.Should().Be(90);
        info.Frame.Nr.Should().Be(100);
        info.Frame.Info.ToArray().Should().Equal("hi"u8.ToArray());
    }

    [Fact]
    public void A_Disconnect_Forgets_The_Link()
    {
        var tracker = new LinkTracker();
        tracker.Observe(Heard(Ax25Frame.Sabme(Them, Us)));
        tracker.Observe(Heard(Ax25Frame.Disc(Them, Us)));

        var info = Heard(Ax25Frame.I(Them, Us, nr: 2, ns: 3, info: "hi"u8));
        tracker.Observe(info);
        info.LinkExtended.Should().BeNull();
        info.Frame!.Ns.Should().Be(3);
    }

    // --- JSON ----------------------------------------------------------------

    [Fact]
    public void Json_Is_One_Object_Per_Line_With_Only_The_Fields_The_Frame_Has()
    {
        var line = new JsonFormatter().Format(Heard(Ui("Hello", null, Ax25Pid.NoLayer3, new Callsign("WIDE1", 1))));
        line.Should().EndWith("\n");
        line.TrimEnd('\n').Should().NotContain("\n");

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        root.GetProperty("time").GetString().Should().Be("2026-09-14T18:45:51.123Z");
        root.GetProperty("port").GetString().Should().Be("radio");
        root.GetProperty("from").GetString().Should().Be("M0LTE-7");
        root.GetProperty("to").GetString().Should().Be("APRS");
        root.GetProperty("via")[0].GetProperty("call").GetString().Should().Be("WIDE1-1");
        root.GetProperty("type").GetString().Should().Be("UI");
        root.GetProperty("cr").GetString().Should().Be("command");
        root.GetProperty("pid").GetInt32().Should().Be(0xF0);
        root.GetProperty("text").GetString().Should().Be("Hello");
        Convert.FromBase64String(root.GetProperty("info").GetString()!).Should().Equal("Hello"u8.ToArray());
        root.TryGetProperty("ns", out _).Should().BeFalse();
        root.TryGetProperty("nr", out _).Should().BeFalse();
    }

    [Fact]
    public void Json_Carries_Sequence_Numbers_Where_They_Exist()
    {
        using var doc = JsonDocument.Parse(new JsonFormatter().Format(Heard(Ax25Frame.I(Them, Us, nr: 2, ns: 3, info: "x"u8))));
        doc.RootElement.GetProperty("ns").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("nr").GetInt32().Should().Be(2);
    }

    [Fact]
    public void Json_Reports_An_Unreadable_Frame_With_Its_Bytes()
    {
        using var doc = JsonDocument.Parse(new JsonFormatter().Format(Heard([1, 2, 3])));
        doc.RootElement.GetProperty("error").GetString().Should().Be("unreadable frame");
        doc.RootElement.GetProperty("raw").GetString().Should().Be("AQID");
    }

    // --- classic -------------------------------------------------------------
    // Expected strings are worked by hand from ax25dump.c and listen.c.

    [Fact]
    public void Classic_Sabm_Is_A_Polling_Command()
    {
        Classic(Heard(Ax25Frame.Sabm(Them, Us))).Should().Be("radio: fm M0LTE-7 to GB7RDG ctl SABM+ \n");
    }

    [Fact]
    public void Classic_Ua_Is_A_Final_Response()
    {
        Classic(Heard(Ax25Frame.Ua(Us, Them, finalBit: true))).Should().Be("radio: fm GB7RDG to M0LTE-7 ctl UA- \n");
    }

    [Fact]
    public void Classic_I_Frame_Prints_Nr_Then_Ns_Then_The_Data()
    {
        Classic(Heard(Ax25Frame.I(Them, Us, nr: 2, ns: 3, info: "hello"u8)))
            .Should().Be("radio: fm M0LTE-7 to GB7RDG ctl I23^ pid=F0(Text) len 5 \n0000  hello\n");
    }

    [Fact]
    public void Classic_Supervisory_Response()
    {
        Classic(Heard(Ax25Frame.Rr(Us, Them, nr: 4, isCommand: false, pollFinal: true)))
            .Should().Be("radio: fm GB7RDG to M0LTE-7 ctl RR4- \n");
    }

    [Fact]
    public void Classic_Via_Lists_Digipeaters_With_A_Star_For_Repeated()
    {
        var bytes = Ui("hi", null, Ax25Pid.NoLayer3, new Callsign("WIDE1", 1), new Callsign("WIDE2", 1)).ToBytes();
        bytes[20] |= 0x80;
        Classic(Heard(bytes))
            .Should().Be("radio: fm M0LTE-7 to APRS via WIDE1-1* WIDE2-1 ctl UI^ pid=F0(Text) len 2 \n0000  hi\n");
    }

    [Fact]
    public void Classic_Hex_Dump()
    {
        Classic(Heard(Ui("Hello\r\n")), ClassicDump.Hex)
            .Should().EndWith("\n0000  48 65 6C 6C 6F 0D 0A " + new string(' ', 27) + " | Hello..\n");
    }

    [Fact]
    public void Classic_Readable_Dump_Is_The_Text_As_Lines()
    {
        Classic(Heard(Ui("line1\r\nline2\r")), ClassicDump.Readable).Should().EndWith("len 13 \nline1\nline2\n");
    }

    [Fact]
    public void Classic_Ascii_Dump_Wraps_At_64()
    {
        var output = Classic(Heard(Ui(new string('x', 70))));
        output.Should().Contain($"\n0000  {new string('x', 64)}\n0040  xxxxxx\n");
    }

    [Fact]
    public void Classic_Seven_Bit_By_Default_And_Eight_With_Dash_8()
    {
        var frame = Heard(Ui([0x41, 0xE9], 0xF0));
        Classic(frame).Should().EndWith("0000  A.\n");
        Classic(frame, eightBit: true).Should().EndWith("0000  Aé\n");
    }

    [Fact]
    public void Classic_Short_Frame_Is_A_Bad_Header()
    {
        Classic(Heard([1, 2, 3])).Should().Be("radio: AX25: bad header!\n");
    }

    [Fact]
    public void Classic_Timestamps()
    {
        var first = Heard(Ax25Frame.Sabm(Them, Us));
        var second = Heard(Ax25Frame.Sabm(Them, Us), at: At.AddSeconds(1.5));

        Classic(first, t: 2).Should().EndWith($"SABM+ {At.ToUnixTimeSeconds()}.123000 \n");

        var sincePrevious = new ClassicFormatter(ClassicDump.Ascii, 3, false, false, false);
        sincePrevious.Format(first).Should().EndWith("SABM+ 00:00:00.000000 \n");
        sincePrevious.Format(second).Should().EndWith("SABM+ 00:00:01.500000 \n");

        var local = At.ToLocalTime();
        Classic(first, t: 0).Should().EndWith($"SABM+ {local:HH:mm:ss}.123000 \n");
        Classic(first, t: 4).Should().EndWith($"SABM+ {local:yyyy-MM-dd HH:mm:ss}.123000 \n");
    }
}
