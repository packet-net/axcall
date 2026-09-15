using System.Net;
using AwesomeAssertions;
using Axcall;
using Packet.Ax25;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

public class AxArpTests
{
    private static readonly Callsign Us = new("M0LTE", 7);
    private static readonly Callsign Them = new("GB7RDG");
    private static readonly IPAddress OurAddress = IPAddress.Parse("44.131.20.1");
    private static readonly IPAddress TheirAddress = IPAddress.Parse("44.131.20.2");

    [Fact]
    public void A_Reply_Round_Trips()
    {
        var reply = new AxArpMessage(AxArpOperation.Reply, Us, OurAddress, Them, TheirAddress);

        AxArpMessage.TryParse(reply.ToBytes(), out var parsed).Should().BeTrue();

        parsed!.Operation.Should().Be(AxArpOperation.Reply);
        parsed.SenderCallsign.Should().Be(Us);
        parsed.SenderAddress.Should().Be(OurAddress);
        parsed.TargetCallsign.Should().Be(Them);
        parsed.TargetAddress.Should().Be(TheirAddress);
    }

    [Fact]
    public void A_Message_Is_Thirty_Bytes()
        => new AxArpMessage(AxArpOperation.Request, Us, OurAddress, AxArpMessage.NoCallsign, TheirAddress)
            .ToBytes().Length.Should().Be(AxArpMessage.WireLength);

    /// <summary>
    /// The layout, byte for byte, against a request built by hand from the
    /// LinBPQ struct. This is the one test that would catch a field moving.
    /// </summary>
    [Fact]
    public void The_Wire_Layout_Is_Fixed()
    {
        var request = new AxArpMessage(
            AxArpOperation.Request, Us, OurAddress, AxArpMessage.NoCallsign, TheirAddress,
            AxArpMessage.ProtocolTypeBpq);

        var bytes = request.ToBytes();

        bytes[0..2].Should().Equal([0x00, 0x03], "hardware type 3 is AX.25 level 2");
        bytes[2..4].Should().Equal([0x00, 0xCC], "the protocol type asked for");
        bytes[4].Should().Be(7, "an AX.25 hardware address is one 7 octet address slot");
        bytes[5].Should().Be(4, "an IPv4 address is 4 octets");
        bytes[6..8].Should().Equal([0x00, 0x01], "operation 1 is a request");
        bytes[15..19].Should().Equal([44, 131, 20, 1]);
        bytes[26..30].Should().Equal([44, 131, 20, 2]);
    }

    /// <summary>
    /// A request has nothing to put in the target hardware address, because
    /// that is the question. Every implementation leaves it zero, and zero is
    /// not a callsign.
    /// </summary>
    [Fact]
    public void A_Request_With_An_Empty_Target_Callsign_Still_Parses()
    {
        var request = new AxArpMessage(AxArpOperation.Request, Us, OurAddress, AxArpMessage.NoCallsign, TheirAddress);

        var bytes = request.ToBytes();
        bytes.AsSpan(19, 7).Clear();

        AxArpMessage.TryParse(bytes, out var parsed).Should().BeTrue();
        parsed!.TargetCallsign.Base.Should().BeEmpty();
        parsed.TargetAddress.Should().Be(TheirAddress);
    }

    /// <summary>
    /// Two live implementations put different values in the protocol type
    /// field and neither checks it, so neither do we; but a reply has to hand
    /// back what the request used.
    /// </summary>
    [Theory]
    [InlineData(AxArpMessage.ProtocolTypeIp)]
    [InlineData(AxArpMessage.ProtocolTypeBpq)]
    [InlineData((ushort)0x1234)]
    public void The_Protocol_Type_Is_Carried_Not_Checked(ushort protocolType)
    {
        var message = new AxArpMessage(AxArpOperation.Request, Us, OurAddress, Them, TheirAddress, protocolType);

        AxArpMessage.TryParse(message.ToBytes(), out var parsed).Should().BeTrue();
        parsed!.ProtocolType.Should().Be(protocolType);
    }

    [Fact]
    public void A_Short_Message_Is_Not_One()
        => AxArpMessage.TryParse(new byte[AxArpMessage.WireLength - 1], out _).Should().BeFalse();

    [Fact]
    public void An_Ethernet_Hardware_Type_Is_Not_One()
    {
        var bytes = new AxArpMessage(AxArpOperation.Request, Us, OurAddress, Them, TheirAddress).ToBytes();
        bytes[1] = 1; // Ethernet

        AxArpMessage.TryParse(bytes, out _).Should().BeFalse("this is the AX.25 form, not the one off a LAN");
    }

    [Fact]
    public void An_Unknown_Operation_Is_Not_Acted_On()
    {
        var bytes = new AxArpMessage(AxArpOperation.Request, Us, OurAddress, Them, TheirAddress).ToBytes();
        bytes[7] = 3; // RARP request

        AxArpMessage.TryParse(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void An_Arp_Frame_Carries_Pid_Cd_To_The_Broadcast_Address()
    {
        var request = new AxArpMessage(AxArpOperation.Request, Us, OurAddress, AxArpMessage.NoCallsign, TheirAddress);
        var frame = Ax25Ip.Arp(Ax25Ip.Broadcast, Us, request.ToBytes());

        frame.Pid.Should().Be(Ax25Pid.Arp);
        frame.IsUi.Should().BeTrue();
        frame.Destination.Callsign.Should().Be(new Callsign("QST"));
        frame.Source.Callsign.Should().Be(Us);
    }
}
