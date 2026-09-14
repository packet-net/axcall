using System.Text;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// The one line per frame that -d writes. Plain ASCII, and each field printed
/// only where the frame type actually carries it.
/// </summary>
public sealed class FrameTraceTests
{
    private static readonly Callsign Us = new("M0LTE", 7);
    private static readonly Callsign Them = new("GB7RDG", 0);
    private static readonly DateTimeOffset At = new(2026, 9, 14, 18, 45, 51, 123, TimeSpan.Zero);

    private static string Format(Ax25Frame frame, FrameDirection direction = FrameDirection.Transmitted)
        => FrameTrace.Format(new Ax25FrameEventArgs { Frame = frame, Direction = direction, Timestamp = At });

    [Fact]
    public void Outbound_Sabm_Reads_As_A_Polling_Command()
    {
        Format(Ax25Frame.Sabm(Them, Us))
            .Should().Be("18:45:51.123 > M0LTE-7>GB7RDG SABM C P");
    }

    [Fact]
    public void Inbound_Ua_Reads_As_A_Final_Response()
    {
        Format(Ax25Frame.Ua(Us, Them, finalBit: true), FrameDirection.Received)
            .Should().Be("18:45:51.123 < GB7RDG>M0LTE-7 UA R F");
    }

    [Fact]
    public void I_Frame_Carries_Both_Sequence_Numbers_The_Pid_And_The_Length()
    {
        Format(Ax25Frame.I(Them, Us, nr: 2, ns: 3, info: Encoding.ASCII.GetBytes("hello")))
            .Should().Be("18:45:51.123 > M0LTE-7>GB7RDG I C ns=3 nr=2 pid=F0 len=5");
    }

    [Fact]
    public void Supervisory_Frames_Carry_Only_Nr()
    {
        // N(S) lives in bits that encode the supervisory type on an S frame, so
        // printing it would be printing nonsense.
        var line = Format(Ax25Frame.Rr(Us, Them, nr: 4, isCommand: false, pollFinal: true), FrameDirection.Received);
        line.Should().Be("18:45:51.123 < GB7RDG>M0LTE-7 RR R F nr=4");
        line.Should().NotContain("ns=");
    }

    [Theory]
    [InlineData("RNR")]
    [InlineData("REJ")]
    [InlineData("SREJ")]
    public void Every_Supervisory_Type_Is_Named(string expected)
    {
        var frame = expected switch
        {
            "RNR" => Ax25Frame.Rnr(Them, Us, nr: 1, isCommand: true),
            "REJ" => Ax25Frame.Rej(Them, Us, nr: 1, isCommand: true),
            _ => Ax25Frame.Srej(Them, Us, nr: 1, isCommand: true),
        };
        Format(frame).Should().Contain($" {expected} ").And.Contain("nr=1");
    }

    [Fact]
    public void U_Frames_Carry_Neither_Sequence_Number()
    {
        var line = Format(Ax25Frame.Disc(Them, Us));
        line.Should().Be("18:45:51.123 > M0LTE-7>GB7RDG DISC C P");
        line.Should().NotContain("nr=").And.NotContain("ns=");
    }

    [Fact]
    public void Sabme_Is_Named_Distinctly_From_Sabm()
    {
        Format(Ax25Frame.Sabme(Them, Us)).Should().Contain("SABME");
    }

    [Fact]
    public void Poll_Bit_Is_Omitted_When_Clear()
    {
        Format(Ax25Frame.Rr(Them, Us, nr: 0, isCommand: true, pollFinal: false))
            .Should().Be("18:45:51.123 > M0LTE-7>GB7RDG RR C nr=0");
    }

    [Fact]
    public void Digipeaters_Are_Listed_With_A_Star_On_Hops_Already_Repeated()
    {
        var line = Format(Ax25Frame.Sabm(Them, Us, digipeaters: [new Callsign("GB7CIP", 0), new Callsign("MB7UR", 0)]));
        line.Should().Contain("via GB7CIP,MB7UR");
    }

    [Fact]
    public void An_Empty_Info_Field_Prints_No_Length()
    {
        Format(Ax25Frame.I(Them, Us, nr: 0, ns: 0, info: ReadOnlySpan<byte>.Empty))
            .Should().NotContain("len=");
    }

    [Fact]
    public void Extended_Sequence_Numbers_Are_Printed()
    {
        // Modulo 128 widens N(S) and N(R) to 7 bits, and the trace should show
        // the real values rather than a modulo-8 truncation.
        Format(Ax25Frame.I(Them, Us, nr: 100, ns: 90, info: Encoding.ASCII.GetBytes("x"), extended: true))
            .Should().Contain("ns=90 nr=100");
    }

    [Fact]
    public void Every_Line_Is_Plain_Ascii()
    {
        // A trace that reaches journalctl must not arrive as <E2><80><94>.
        var frames = new[]
        {
            Ax25Frame.Sabm(Them, Us),
            Ax25Frame.Ua(Us, Them, finalBit: true),
            Ax25Frame.I(Them, Us, nr: 0, ns: 0, info: Encoding.ASCII.GetBytes("hello")),
            Ax25Frame.Rr(Us, Them, nr: 1, isCommand: false),
            Ax25Frame.Disc(Them, Us),
        };
        foreach (var frame in frames)
        {
            Format(frame).Should().MatchRegex("^[\\x20-\\x7E]+$");
        }
    }
}
