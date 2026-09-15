using System.Buffers.Binary;
using System.Net;
using AwesomeAssertions;
using Axcall;
using Packet.Ax25;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

public class IpPacketTests
{
    [Fact]
    public void A_Minimal_Header_Reads()
    {
        var packet = Icmp("44.131.20.1", "44.131.20.2");

        IpPacket.TryRead(packet, out var ip).Should().BeTrue();
        ip.Protocol.Should().Be(IpPacket.ProtocolIcmp);
        ip.Source.Should().Be(IPAddress.Parse("44.131.20.1"));
        ip.Destination.Should().Be(IPAddress.Parse("44.131.20.2"));
        ip.HeaderLength.Should().Be(20);
        ip.IsFragment.Should().BeFalse();
    }

    /// <summary>
    /// IPv6 arrives on a TUN device whether anyone asked for it or not, and
    /// there is no PID for it here. Failing to read it is how it gets dropped.
    /// </summary>
    [Fact]
    public void An_Ipv6_Packet_Is_Not_Read()
    {
        var packet = new byte[40];
        packet[0] = 0x60;

        IpPacket.TryRead(packet, out _).Should().BeFalse();
    }

    [Fact]
    public void A_Truncated_Packet_Is_Not_Read()
        => IpPacket.TryRead(new byte[19], out _).Should().BeFalse();

    [Fact]
    public void A_Total_Length_Longer_Than_The_Bytes_Is_Not_Read()
    {
        var packet = Icmp("44.131.20.1", "44.131.20.2");
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 9999);

        IpPacket.TryRead(packet, out _).Should().BeFalse("a header that disagrees with the packet has been mangled");
    }

    [Fact]
    public void Trailing_Bytes_Past_The_Total_Length_Are_Ignored()
    {
        var packet = Icmp("44.131.20.1", "44.131.20.2");
        var padded = new byte[packet.Length + 16];
        packet.CopyTo(padded, 0);

        IpPacket.TryRead(padded, out var ip).Should().BeTrue();
        ip.TotalLength.Should().Be(packet.Length);
    }

    [Fact]
    public void Ports_Read_From_The_First_Fragment_Only()
    {
        var first = Tcp(destinationPort: 22, fragmentOffset: 0);
        IpPacket.TryRead(first, out var head).Should().BeTrue();
        head.DestinationPort.Should().Be(22);

        var later = Tcp(destinationPort: 22, fragmentOffset: 200);
        IpPacket.TryRead(later, out var tail).Should().BeTrue();
        tail.DestinationPort.Should().BeNull("a trailing fragment has no TCP header in it");
        tail.FragmentOffset.Should().Be(200);
    }

    [Fact]
    public void Icmp_Has_No_Ports()
    {
        IpPacket.TryRead(Icmp("44.131.20.1", "44.131.20.2"), out var ip).Should().BeTrue();
        ip.DestinationPort.Should().BeNull();
        ip.SourcePort.Should().BeNull();
    }

    [Fact]
    public void A_Description_Names_What_It_Is()
    {
        IpPacket.TryRead(Tcp(destinationPort: 22, fragmentOffset: 0), out var ip).Should().BeTrue();
        ip.Describe().Should().Contain("tcp").And.Contain("->22");
    }

    /// <summary>
    /// The receive path has to take IP out of an I-frame as well as a UI
    /// frame: ax25rtd.conf(5) has a per-route mode of datagram or virtual
    /// connect, and both are configured in the field.
    /// </summary>
    [Fact]
    public void Ip_Is_Taken_From_A_Connected_Mode_Frame_As_Well_As_A_Ui_Frame()
    {
        var packet = Icmp("44.131.20.1", "44.131.20.2");

        var ui = Ax25Frame.Ui(new Callsign("GB7RDG"), new Callsign("M0LTE", 7), packet, Ax25Pid.Ip);
        Ax25Ip.TryGetPayload(ui, Ax25Pid.Ip, out var fromUi).Should().BeTrue();
        fromUi.ToArray().Should().Equal(packet);

        var i = Ax25Frame.I(new Callsign("GB7RDG"), new Callsign("M0LTE", 7), ns: 0, nr: 0, info: packet, pid: Ax25Pid.Ip);
        Ax25Ip.TryGetPayload(i, Ax25Pid.Ip, out var fromI).Should().BeTrue();
        fromI.ToArray().Should().Equal(packet);
    }

    [Fact]
    public void A_Frame_With_A_Different_Pid_Is_Not_Ip()
    {
        var text = Ax25Frame.Ui(new Callsign("GB7RDG"), new Callsign("M0LTE", 7), "hello"u8, Ax25Pid.NoLayer3);

        Ax25Ip.TryGetPayload(text, Ax25Pid.Ip, out _).Should().BeFalse();
    }

    /// <summary>
    /// The size a peer will take, measured rather than assumed. See
    /// <see cref="Ax25Ip.MaxFrameBytes"/>.
    /// </summary>
    [Fact]
    public void The_Mtu_Shrinks_By_One_Address_Slot_Per_Digipeater()
    {
        Ax25Ip.MaxMtu(0).Should().Be(Ax25Ip.MaxFrameBytes - 16);
        Ax25Ip.MaxMtu(1).Should().Be(Ax25Ip.MaxMtu(0) - 7);
        Ax25Ip.MaxMtu(2).Should().Be(Ax25Ip.MaxMtu(0) - 14);
        Ax25Ip.DefaultMtu.Should().BeLessThan(Ax25Ip.MaxMtu(2), "the default should leave room for a path");
    }

    [Fact]
    public void A_Datagram_Frame_Carries_Pid_Cc_And_The_Digipeater_Path()
    {
        var frame = Ax25Ip.Datagram(
            new Callsign("GB7RDG"),
            new Callsign("M0LTE", 7),
            Icmp("44.131.20.1", "44.131.20.2"),
            [new Callsign("WIDE1", 1)]);

        frame.Pid.Should().Be(Ax25Pid.Ip);
        frame.IsUi.Should().BeTrue();
        frame.Digipeaters.Should().ContainSingle().Which.Callsign.Should().Be(new Callsign("WIDE1", 1));
    }

    private static byte[] Icmp(string source, string destination)
    {
        var packet = new byte[28];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[8] = 64;
        packet[9] = IpPacket.ProtocolIcmp;
        IPAddress.Parse(source).TryWriteBytes(packet.AsSpan(12), out _);
        IPAddress.Parse(destination).TryWriteBytes(packet.AsSpan(16), out _);
        return packet;
    }

    private static byte[] Tcp(ushort destinationPort, int fragmentOffset)
    {
        var packet = new byte[40];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)(fragmentOffset / 8));
        packet[8] = 64;
        packet[9] = IpPacket.ProtocolTcp;
        IPAddress.Parse("44.131.20.1").TryWriteBytes(packet.AsSpan(12), out _);
        IPAddress.Parse("44.131.20.2").TryWriteBytes(packet.AsSpan(16), out _);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 40000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), destinationPort);
        return packet;
    }
}

public class IpPrefixTests
{
    [Theory]
    [InlineData("44.131.20.0/24", "44.131.20.7", true)]
    [InlineData("44.131.20.0/24", "44.131.21.7", false)]
    [InlineData("44.0.0.0/8", "44.131.20.7", true)]
    [InlineData("any", "8.8.8.8", true)]
    [InlineData("44.131.20.2", "44.131.20.2", true)]
    [InlineData("44.131.20.2", "44.131.20.3", false)]
    public void Containment(string prefix, string address, bool contains)
    {
        IpPrefix.TryParse(prefix, out var parsed, out var error).Should().BeTrue(error);
        parsed.Contains(IPAddress.Parse(address)).Should().Be(contains);
    }

    /// <summary>
    /// A prefix that quietly means something other than what was typed is
    /// worse than one that is rewritten to what it meant.
    /// </summary>
    [Fact]
    public void Host_Bits_Are_Normalised_Away()
    {
        IpPrefix.TryParse("44.131.20.7/24", out var prefix, out _).Should().BeTrue();
        prefix.ToString().Should().Be("44.131.20.0/24");
    }

    [Theory]
    [InlineData("")]
    [InlineData("banana")]
    [InlineData("44.131.20.0/33")]
    [InlineData("2001:db8::/32")]
    public void Nonsense_Does_Not_Parse(string text)
        => IpPrefix.TryParse(text, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("224.0.0.251")]
    [InlineData("239.255.255.250")]
    [InlineData("255.255.255.255")]
    [InlineData("169.254.7.7")]
    public void The_Chatty_Ranges_Are_Recognised(string address)
        => IpPrefix.IsChatty(IpPrefix.ToUInt32(IPAddress.Parse(address))).Should().BeTrue();

    [Theory]
    [InlineData("44.131.20.2")]
    [InlineData("8.8.8.8")]
    [InlineData("192.168.1.1")]
    public void An_Ordinary_Unicast_Address_Is_Not_Chatty(string address)
        => IpPrefix.IsChatty(IpPrefix.ToUInt32(IPAddress.Parse(address))).Should().BeFalse();

    [Theory]
    [InlineData(24, "255.255.255.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(32, "255.255.255.255")]
    [InlineData(0, "0.0.0.0")]
    public void A_Length_Becomes_A_Mask(int length, string mask)
        => IpPrefix.ToAddress(new IpPrefix(0, length).Mask).Should().Be(IPAddress.Parse(mask));
}
