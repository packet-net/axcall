using System.Net;
using AwesomeAssertions;
using Axcall;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

[Collection(ConfigFilesCollection.Name)]
public class AxtunFileTests : IDisposable
{
    private readonly string dir;

    public AxtunFileTests()
    {
        dir = Path.Combine(Path.GetTempPath(), $"axtun-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_Route_Names_A_Prefix_And_A_Station()
    {
        var config = Load("""
            route 44.131.20.2    PN0TST
            route 44.131.91.0/24 GB7RDG-1
            """);

        config.Routes.Should().HaveCount(2);
        config.Lookup(Value("44.131.20.2"))!.Callsign.Should().Be(new Callsign("PN0TST"));
        config.Lookup(Value("44.131.91.7"))!.Callsign.Should().Be(new Callsign("GB7RDG", 1));
        config.Lookup(Value("8.8.8.8")).Should().BeNull();
    }

    [Fact]
    public void Default_Is_A_Route_To_Everything()
    {
        var config = Load("route default GB7RDG-1");

        config.Lookup(Value("8.8.8.8"))!.Callsign.Should().Be(new Callsign("GB7RDG", 1));
    }

    /// <summary>
    /// Longest prefix wins, as it does in every routing table, so a host route
    /// beats the subnet it sits in and the subnet beats the default.
    /// </summary>
    [Fact]
    public void The_Most_Specific_Route_Wins_Whatever_Order_It_Is_In()
    {
        var config = Load("""
            route default          GB7RDG-1
            route 44.131.20.0/24   GB7CIP
            route 44.131.20.2      PN0TST
            """);

        config.Lookup(Value("44.131.20.2"))!.Callsign.Should().Be(new Callsign("PN0TST"));
        config.Lookup(Value("44.131.20.9"))!.Callsign.Should().Be(new Callsign("GB7CIP"));
        config.Lookup(Value("10.0.0.1"))!.Callsign.Should().Be(new Callsign("GB7RDG", 1));
    }

    /// <summary>
    /// Digipeating is not done anywhere in this suite, for connected mode or
    /// for IP, so a route through one is refused rather than quietly sent
    /// direct. A station that looks configured and is unreachable is worse
    /// than one that fails to start.
    /// </summary>
    [Fact]
    public void A_Route_Through_A_Digipeater_Is_Refused_And_Says_Why()
    {
        AxtunFile.TryParseLine("route 44.131.91.0/24 GB7RDG-1 via WIDE1-1", out _, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("digipeater paths are not supported");
    }

    [Fact]
    public void Filter_Rules_Sit_In_The_Same_File()
    {
        var config = Load("""
            route default GB7RDG-1
            allow icmp
            deny udp to any port 123
            """);

        config.EgressRules.Should().HaveCount(2);
        config.Filter.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void A_File_With_No_Filter_Rules_Gets_The_Default_Policy()
        => Load("route default GB7RDG-1").Filter.IsDefault.Should().BeTrue();

    [Fact]
    public void Comments_And_Blank_Lines_Are_Ignored()
    {
        var config = Load("""

            # who is out there
            route 44.131.20.2 PN0TST   # the test node

            """);

        config.Routes.Should().ContainSingle();
    }

    [Fact]
    public void A_Later_Route_For_The_Same_Range_Replaces_The_Earlier_One()
    {
        var config = Load("""
            route 44.131.20.0/24 GB7CIP
            route 44.131.20.0/24 PN0TST
            """);

        config.Routes.Should().ContainSingle();
        config.Lookup(Value("44.131.20.9"))!.Callsign.Should().Be(new Callsign("PN0TST"));
    }

    [Theory]
    [InlineData("banana 1 2", "is not a keyword")]
    [InlineData("route", "expected: route")]
    [InlineData("route 44.131.20.2", "expected: route")]
    [InlineData("route notanaddress PN0TST", "is not an IPv4 address")]
    [InlineData("route 44.131.20.2 toolongforacallsign", "invalid callsign")]
    [InlineData("route 44.131.20.2 PN0TST through WIDE1-1", "a route is a prefix and a callsign")]
    [InlineData("route 44.131.20.2 PN0TST via WIDE1-1", "digipeater paths are not supported")]
    [InlineData("allow nonsense", "is not a protocol")]
    public void A_Bad_Line_Fails_The_Load_And_Says_Where(string line, string expected)
    {
        var path = Write($"route default GB7RDG\n{line}\n");

        AxtunFile.TryLoad([path], out var config, out var error).Should().BeFalse();
        config.Should().BeNull();
        error.Should().Contain(":2:").And.Contain(expected);
    }

    [Fact]
    public void A_Missing_File_Is_Not_An_Error()
    {
        AxtunFile.TryLoad([Path.Combine(dir, "not-there")], out var config, out var error).Should().BeTrue();
        error.Should().BeNull();
        config!.Routes.Should().BeEmpty();
        config.Filter.IsDefault.Should().BeTrue("no config means the default policy, not no policy");
    }

    private AxtunConfig Load(string text)
    {
        AxtunFile.TryLoad([Write(text)], out var config, out var error).Should().BeTrue(error);
        return config!;
    }

    private string Write(string text)
    {
        var path = Path.Combine(dir, "axtun");
        File.WriteAllText(path, text);
        return path;
    }

    private static uint Value(string address) => IpPrefix.ToUInt32(IPAddress.Parse(address));
}
