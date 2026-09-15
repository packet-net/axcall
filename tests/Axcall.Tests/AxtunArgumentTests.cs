using System.Net;
using AwesomeAssertions;
using Axcall;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// axtun's command line. It shares the ports file, the transport spec and the
/// channel-access options with axcall; what is its own is the interface, the
/// address, and where the routes and the egress policy come from.
/// </summary>
[Collection(ConfigFilesCollection.Name)]
public sealed class AxtunArgumentTests
{
    private sealed class ConfigScope : IDisposable
    {
        private readonly string? previousPorts;
        private readonly string? previousAxtun;
        private readonly string portsPath;
        private readonly string axtunPath;

        public ConfigScope(string? ports = null, string? axtun = null)
        {
            portsPath = Path.Combine(Path.GetTempPath(), $"axtun-ports-{Guid.NewGuid():N}");
            axtunPath = Path.Combine(Path.GetTempPath(), $"axtun-config-{Guid.NewGuid():N}");
            if (ports is not null) File.WriteAllText(portsPath, ports);
            if (axtun is not null) File.WriteAllText(axtunPath, axtun);

            previousPorts = Environment.GetEnvironmentVariable(PortsFile.PathEnvVar);
            previousAxtun = Environment.GetEnvironmentVariable(AxtunFile.PathEnvVar);
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, portsPath);
            Environment.SetEnvironmentVariable(AxtunFile.PathEnvVar, axtunPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, previousPorts);
            Environment.SetEnvironmentVariable(AxtunFile.PathEnvVar, previousAxtun);
            try { File.Delete(portsPath); } catch (IOException) { }
            try { File.Delete(axtunPath); } catch (IOException) { }
        }
    }

    private const string OnePort = "radio  M0LTE-7  /dev/ttyUSB0:57600  256  4  txdelay=300  144.800 MHz\n";

    [Fact]
    public void A_Port_Name_Supplies_The_Transport_And_The_Callsign()
    {
        using var _ = new ConfigScope(ports: OnePort);

        var parsed = Axtun.Program.ParseArgs(["radio"]);

        parsed.Should().NotBeNull();
        parsed!.MyCall.Should().Be(new Callsign("M0LTE", 7));
        parsed.Transport.Device.Should().Be("/dev/ttyUSB0");
        parsed.Interface.Should().Be(Axtun.Program.DefaultInterface);
        parsed.Mtu.Should().Be(Ax25Ip.DefaultMtu);
        parsed.Address.Should().BeNull("an interface configured by hand is a supported way to run");
    }

    [Fact]
    public void An_Address_Carries_Its_Prefix_Length()
    {
        using var _ = new ConfigScope(ports: OnePort);

        var parsed = Axtun.Program.ParseArgs(["--addr", "44.131.20.1/24", "radio"]);

        parsed!.Address!.Address.Should().Be(IPAddress.Parse("44.131.20.1"));
        parsed.Address.PrefixLength.Should().Be(24);
    }

    /// <summary>
    /// Without a prefix there is no way to know what is on-link, and guessing
    /// a classful default would be wrong roughly every time.
    /// </summary>
    [Fact]
    public void An_Address_Without_A_Prefix_Length_Is_Refused()
    {
        Axtun.Program.TryParseLocalAddress("44.131.20.1", out _, out var error).Should().BeFalse();
        error.Should().Contain("needs a prefix length");
    }

    [Theory]
    [InlineData("banana/24", "is not an IPv4 address")]
    [InlineData("44.131.20.1/33", "prefix length in 0..32")]
    [InlineData("2001:db8::1/64", "is not an IPv4 address")]
    public void A_Bad_Address_Says_Why(string text, string expected)
    {
        Axtun.Program.TryParseLocalAddress(text, out _, out var error).Should().BeFalse();
        error.Should().Contain(expected);
    }

    [Fact]
    public void The_Config_File_Supplies_Routes_And_The_Egress_Policy()
    {
        using var _ = new ConfigScope(ports: OnePort, axtun: """
            route 44.131.20.2 PN0TST
            allow icmp
            """);

        var parsed = Axtun.Program.ParseArgs(["radio"]);

        parsed!.Config.Routes.Should().ContainSingle();
        parsed.Config.Filter.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void With_No_Config_The_Default_Policy_Applies()
    {
        using var _ = new ConfigScope(ports: OnePort);

        var parsed = Axtun.Program.ParseArgs(["radio"]);

        parsed!.Config.Routes.Should().BeEmpty();
        parsed.Config.Filter.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void An_Absurd_Mtu_Is_Refused()
    {
        using var _ = new ConfigScope(ports: OnePort);

        Axtun.Program.ParseArgs(["--addr", "44.131.20.1/24", "--mtu", "9000", "radio"]).Should().BeNull();
        Axtun.Program.ParseArgs(["--addr", "44.131.20.1/24", "--mtu", "40", "radio"]).Should().BeNull();
        Axtun.Program.ParseArgs(["--addr", "44.131.20.1/24", "--mtu", "256", "radio"])!.Mtu.Should().Be(256);
    }

    /// <summary>
    /// The 312-byte ceiling is one observation about one peer, not a property
    /// of AX.25. Refusing to go above it would make this tool the reason
    /// somebody cannot use a link that works, so it warns instead.
    /// </summary>
    [Fact]
    public void An_Mtu_Above_The_Only_Tested_Peer_Is_Allowed()
    {
        using var _ = new ConfigScope(ports: OnePort);

        var above = Ax25Ip.MaxMtu(0) + 1;
        Axtun.Program.ParseArgs(["--addr", "44.131.20.1/24", "--mtu", $"{above}", "radio"])!
            .Mtu.Should().Be(above);
    }

    /// <summary>
    /// Setting the MTU means reconfiguring the interface, which is the thing
    /// --addr asks for. Quietly reconfiguring an interface somebody else set up
    /// is not what they meant.
    /// </summary>
    [Fact]
    public void An_Mtu_Without_An_Address_Is_Refused()
    {
        using var _ = new ConfigScope(ports: OnePort);

        Axtun.Program.ParseArgs(["--mtu", "200", "radio"]).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("this-name-is-far-too-long")]
    [InlineData("../etc/passwd")]
    public void A_Bad_Interface_Name_Is_Refused(string name)
    {
        using var _ = new ConfigScope(ports: OnePort);

        Axtun.Program.ParseArgs(["--dev", name, "radio"]).Should().BeNull();
    }

    [Fact]
    public void The_Command_Line_Beats_The_Ports_File_For_Channel_Access()
    {
        using var _ = new ConfigScope(ports: OnePort);

        var parsed = Axtun.Program.ParseArgs(["--txdelay", "500", "radio"]);

        parsed!.Channel.TxDelay.Should().Be(50, "txdelay is in 10 ms units on the wire");
    }

    [Fact]
    public void A_Port_With_No_Callsign_And_No_Flag_Is_Refused()
    {
        using var _ = new ConfigScope(ports: "radio  -  /dev/ttyUSB0:57600\n");

        Axtun.Program.ParseArgs(["radio"]).Should().BeNull();
    }

    [Fact]
    public void A_Tcp_Modem_Works_Without_A_Ports_File()
    {
        using var _ = new ConfigScope();

        var parsed = Axtun.Program.ParseArgs(["-s", "M0LTE-7", "--tcp", "127.0.0.1:8100"]);

        parsed.Should().NotBeNull();
        parsed!.Transport.Host.Should().Be("127.0.0.1");
        parsed.Transport.TcpPort.Should().Be(8100);
    }

    [Fact]
    public void An_Unknown_Option_Is_Refused()
    {
        using var _ = new ConfigScope(ports: OnePort);

        Axtun.Program.ParseArgs(["--tunnel", "radio"]).Should().BeNull();
    }
}
