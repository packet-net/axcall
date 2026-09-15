using System.Buffers.Binary;
using System.Net;
using AwesomeAssertions;
using Axcall;
using Xunit;

namespace Axcall.Tests;

public class EgressFilterTests
{
    /// <summary>
    /// The whole point of the class: a rule set that does not mention a packet
    /// does not transmit it.
    /// </summary>
    [Fact]
    public void Nothing_Goes_Out_Unless_A_Rule_Says_So()
    {
        var filter = new EgressFilter([new EgressRule(Allow: true, IpPacket.ProtocolIcmp, IpPrefix.Any)]);

        Allows(filter, Packet(IpPacket.ProtocolIcmp, "44.131.20.2")).Should().BeTrue();
        Allows(filter, Packet(IpPacket.ProtocolTcp, "44.131.20.2", destinationPort: 22)).Should().BeFalse();
    }

    [Fact]
    public void An_Empty_Rule_Set_Denies_Everything()
    {
        var ping = Read(Packet(IpPacket.ProtocolIcmp, "44.131.20.2"));
        EgressFilter.DenyAll.Allows(in ping, out var reason).Should().BeFalse();
        reason.Should().NotBeEmpty("the operator has to be able to see why");
    }

    [Fact]
    public void The_First_Matching_Rule_Decides()
    {
        var filter = new EgressFilter(
        [
            new EgressRule(Allow: false, IpPacket.ProtocolTcp, IpPrefix.Any, 22),
            new EgressRule(Allow: true, IpPacket.ProtocolTcp, IpPrefix.Any),
        ]);

        Allows(filter, Packet(IpPacket.ProtocolTcp, "44.131.20.2", destinationPort: 22)).Should().BeFalse();
        Allows(filter, Packet(IpPacket.ProtocolTcp, "44.131.20.2", destinationPort: 23)).Should().BeTrue();
    }

    /// <summary>
    /// The reason this class exists. A machine with an unfiltered interface on
    /// a radio channel announces itself to the neighbourhood without being
    /// asked, under somebody's personal licence.
    /// </summary>
    [Theory]
    [InlineData("224.0.0.251")]    // mDNS
    [InlineData("239.255.255.250")] // SSDP
    [InlineData("255.255.255.255")] // DHCP and friends
    [InlineData("169.254.1.1")]     // link-local
    public void A_Wildcard_Rule_Does_Not_Reach_The_Chatty_Addresses(string destination)
    {
        var permissive = new EgressFilter([new EgressRule(Allow: true, Protocol: null, IpPrefix.Any)]);

        var mdns = Read(Packet(IpPacket.ProtocolUdp, destination, destinationPort: 5353));
        permissive.Allows(in mdns, out var reason).Should().BeFalse();
        reason.Should().Contain("multicast or broadcast");
    }

    /// <summary>
    /// Naming the range is the escape hatch: somebody who writes it down has
    /// thought about it, which is the difference that matters.
    /// </summary>
    [Fact]
    public void A_Rule_That_Names_The_Range_Does_Reach_It()
    {
        IpPrefix.TryParse("224.0.0.0/4", out var multicast, out _).Should().BeTrue();
        var filter = new EgressFilter([new EgressRule(Allow: true, IpPacket.ProtocolUdp, multicast)]);

        Allows(filter, Packet(IpPacket.ProtocolUdp, "224.0.0.251", destinationPort: 5353)).Should().BeTrue();
    }

    [Fact]
    public void The_Default_Policy_Passes_Ping_And_Blocks_The_Noise()
    {
        var filter = EgressFilter.Default;

        Allows(filter, Packet(IpPacket.ProtocolIcmp, "44.131.20.2")).Should().BeTrue("ping is what people set an IP link up for");
        Allows(filter, Packet(IpPacket.ProtocolTcp, "44.131.20.2", destinationPort: 22)).Should().BeTrue("so is ssh");

        Allows(filter, Packet(IpPacket.ProtocolUdp, "224.0.0.251", destinationPort: 5353)).Should().BeFalse("mDNS");
        Allows(filter, Packet(IpPacket.ProtocolUdp, "44.131.20.2", destinationPort: 5353)).Should().BeFalse("mDNS, unicast form");
        Allows(filter, Packet(IpPacket.ProtocolUdp, "44.131.20.2", destinationPort: 137)).Should().BeFalse("NetBIOS name service");
        Allows(filter, Packet(IpPacket.ProtocolTcp, "44.131.20.2", destinationPort: 445)).Should().BeFalse("SMB");
        Allows(filter, Packet(IpPacket.ProtocolUdp, "44.131.20.2", destinationPort: 1900)).Should().BeFalse("SSDP");
        Allows(filter, Packet(IpPacket.ProtocolUdp, "255.255.255.255", destinationPort: 67)).Should().BeFalse("DHCP");
    }

    [Fact]
    public void An_Unrecognised_Protocol_Does_Not_Go_Out_Under_The_Default_Policy()
        => Allows(EgressFilter.Default, Packet(protocol: 89, "44.131.20.2")).Should().BeFalse("OSPF has no business here");

    /// <summary>
    /// A later fragment does not carry the ports, so a port rule cannot judge
    /// it. Letting it through would make the rule evadable by fragmenting.
    /// </summary>
    [Fact]
    public void A_Trailing_Fragment_Does_Not_Satisfy_A_Port_Rule()
    {
        var filter = new EgressFilter([new EgressRule(Allow: true, IpPacket.ProtocolTcp, IpPrefix.Any, 22)]);

        var fragment = Packet(IpPacket.ProtocolTcp, "44.131.20.2", destinationPort: 22, fragmentOffset: 200);
        Allows(filter, fragment).Should().BeFalse();
    }

    [Fact]
    public void A_Trailing_Fragment_Passes_A_Rule_That_Does_Not_Mention_Ports()
    {
        var filter = new EgressFilter([new EgressRule(Allow: true, IpPacket.ProtocolTcp, IpPrefix.Any)]);

        var fragment = Packet(IpPacket.ProtocolTcp, "44.131.20.2", destinationPort: 22, fragmentOffset: 200);
        Allows(filter, fragment).Should().BeTrue("fragmentation is normal, and this rule was never about ports");
    }

    [Theory]
    [InlineData("allow icmp", true, IpPacket.ProtocolIcmp)]
    [InlineData("deny udp", false, IpPacket.ProtocolUdp)]
    [InlineData("allow 89", true, (byte)89)]
    public void Rules_Parse(string text, bool allow, byte protocol)
    {
        EgressFilter.TryParseRule(text, out var rule, out var error).Should().BeTrue(error);
        rule!.Allow.Should().Be(allow);
        rule.Protocol.Should().Be(protocol);
    }

    [Fact]
    public void A_Rule_Can_Name_A_Destination_And_A_Port_Range()
    {
        EgressFilter.TryParseRule("allow tcp to 44.0.0.0/8 port 8000-8010", out var rule, out var error)
            .Should().BeTrue(error);

        rule!.Destination.ToString().Should().Be("44.0.0.0/8");
        rule.FirstPort.Should().Be(8000);
        rule.LastPort.Should().Be(8010);
    }

    [Fact]
    public void Any_Means_Any_Protocol()
    {
        EgressFilter.TryParseRule("allow any to 44.0.0.0/8", out var rule, out var error).Should().BeTrue(error);
        rule!.Protocol.Should().BeNull();
    }

    [Theory]
    [InlineData("maybe tcp", "not 'allow' or 'deny'")]
    [InlineData("allow", "missing protocol")]
    [InlineData("allow sctp", "is not a protocol")]
    [InlineData("allow tcp to", "needs a destination")]
    [InlineData("allow tcp to notanaddress", "is not an IPv4 address")]
    [InlineData("allow icmp port 22", "only tcp and udp")]
    [InlineData("allow tcp port 90-80", "runs backwards")]
    [InlineData("allow tcp banana", "expected 'to' or 'port'")]
    public void Bad_Rules_Say_Why(string text, string expected)
    {
        EgressFilter.TryParseRule(text, out _, out var error).Should().BeFalse();
        error.Should().Contain(expected);
    }

    [Fact]
    public void A_Rule_Prints_Back_The_Way_It_Was_Written()
        => new EgressRule(Allow: false, IpPacket.ProtocolUdp, IpPrefix.Any, 137, 139)
            .ToString().Should().Be("deny udp to any port 137-139");

    private static bool Allows(EgressFilter filter, byte[] packet)
    {
        var ip = Read(packet);
        return filter.Allows(in ip, out _);
    }

    private static IpPacket Read(byte[] packet)
    {
        IpPacket.TryRead(packet, out var ip).Should().BeTrue();
        return ip;
    }

    /// <summary>Build the smallest IPv4 packet the filter can be asked about.</summary>
    private static byte[] Packet(
        byte protocol,
        string destination,
        string source = "44.131.20.1",
        ushort destinationPort = 0,
        int fragmentOffset = 0)
    {
        var packet = new byte[28];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)(fragmentOffset / 8));
        packet[8] = 64;
        packet[9] = protocol;
        IPAddress.Parse(source).TryWriteBytes(packet.AsSpan(12), out _);
        IPAddress.Parse(destination).TryWriteBytes(packet.AsSpan(16), out _);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), destinationPort);
        return packet;
    }
}
