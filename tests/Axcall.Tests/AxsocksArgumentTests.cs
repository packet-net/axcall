using System.Net;
using AwesomeAssertions;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// axsocks' command line. It shares the ports file, the transport spec and the
/// channel parameters with axcall, so what is tested here is the wiring and
/// the parts that are its own: where to listen, and where the hosts come from.
/// </summary>
/// <remarks>
/// Both config files are pinned through their environment variables, which are
/// process-wide, so all of it lives in this one class; xunit runs a class's
/// tests one at a time.
/// </remarks>
public sealed class AxsocksArgumentTests
{
    private sealed class ConfigScope : IDisposable
    {
        private readonly string? previousPorts;
        private readonly string? previousHosts;
        private readonly string portsPath;
        private readonly string hostsPath;

        public ConfigScope(string? ports = null, string? hosts = null)
        {
            portsPath = Path.Combine(Path.GetTempPath(), $"axsocks-ports-{Guid.NewGuid():N}");
            hostsPath = Path.Combine(Path.GetTempPath(), $"axsocks-hosts-{Guid.NewGuid():N}");
            if (ports is not null) File.WriteAllText(portsPath, ports);
            if (hosts is not null) File.WriteAllText(hostsPath, hosts);

            previousPorts = Environment.GetEnvironmentVariable(PortsFile.PathEnvVar);
            previousHosts = Environment.GetEnvironmentVariable(HostsFile.PathEnvVar);
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, portsPath);
            Environment.SetEnvironmentVariable(HostsFile.PathEnvVar, hostsPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, previousPorts);
            Environment.SetEnvironmentVariable(HostsFile.PathEnvVar, previousHosts);
            try { File.Delete(portsPath); } catch (IOException) { }
            try { File.Delete(hostsPath); } catch (IOException) { }
        }
    }

    private const string OnePort = "radio  M0LTE-7  /dev/ttyUSB0:57600  256  4  txdelay=300  144.800 MHz\n";

    [Fact]
    public void Listens_on_loopback_by_default()
    {
        // Not 0.0.0.0. Binding this where the network can reach it hands
        // anyone who can open the port the use of a callsign and a transmitter.
        using var _ = new ConfigScope();

        var parsed = Axsocks.Program.ParseArgs(["--serial", "/dev/ttyUSB0", "-s", "M0LTE-7"]);

        parsed.Should().NotBeNull();
        parsed!.Bind.Should().Be(new IPEndPoint(IPAddress.Loopback, 1080));
    }

    [Theory]
    [InlineData("8080", "127.0.0.1:8080")]
    [InlineData("0.0.0.0:1080", "0.0.0.0:1080")]
    [InlineData("127.0.0.1:9999", "127.0.0.1:9999")]
    [InlineData("[::1]:1080", "[::1]:1080")]
    public void Accepts_a_bare_port_or_an_address_and_port(string spec, string expected)
    {
        Axsocks.Program.TryParseBind(spec, out var bind, out var error).Should().BeTrue(error);
        bind.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0")]
    [InlineData("99999")]
    [InlineData("127.0.0.1")]
    public void Rejects_a_listen_spec_that_is_not_one(string spec)
    {
        Axsocks.Program.TryParseBind(spec, out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void A_named_port_supplies_the_transport_the_callsign_and_the_defaults()
    {
        using var _ = new ConfigScope(ports: OnePort);

        var parsed = Axsocks.Program.ParseArgs(["radio"]);

        parsed.Should().NotBeNull();
        parsed!.MyCall.Should().Be(new Callsign("M0LTE", 7));
        parsed.Transport.Device.Should().Be("/dev/ttyUSB0");
        parsed.BaudRate.Should().Be(57600);
        parsed.Paclen.Should().Be(256);
        parsed.Window.Should().Be(4);
        parsed.PortName.Should().Be("radio");
        // The port's channel access comes with it: "this radio needs 300 ms of
        // TX delay" is a fact about the port, not about who is using it.
        parsed.Channel.TxDelay.Should().Be(30);
    }

    [Fact]
    public void The_command_line_beats_the_ports_file()
    {
        using var _ = new ConfigScope(ports: OnePort);

        var parsed = Axsocks.Program.ParseArgs(["-s", "M0LTE-9", "-p", "128", "--txdelay", "500", "radio"]);

        parsed.Should().NotBeNull();
        parsed!.MyCall.Should().Be(new Callsign("M0LTE", 9));
        parsed.Paclen.Should().Be(128);
        parsed.Channel.TxDelay.Should().Be(50);
    }

    [Fact]
    public void A_port_without_a_callsign_needs_one_on_the_line()
    {
        using var _ = new ConfigScope(ports: "radio  -  /dev/ttyUSB0\n");

        Axsocks.Program.ParseArgs(["radio"]).Should().BeNull();
        Axsocks.Program.ParseArgs(["-s", "M0LTE-7", "radio"]).Should().NotBeNull();
    }

    [Fact]
    public void The_hosts_file_is_loaded_at_startup()
    {
        // Loaded once when the command line is parsed rather than per
        // connection, so a typo in it is a startup error and not a surprise
        // three hours in.
        using var _ = new ConfigScope(hosts: "gb7rdg  GB7RDG-1  radio\n");

        var parsed = Axsocks.Program.ParseArgs(["--serial", "/dev/ttyUSB0", "-s", "M0LTE-7"]);

        parsed.Should().NotBeNull();
        parsed!.Hosts.Should().ContainKey("gb7rdg");
        parsed.Hosts["gb7rdg"].Callsign.Should().Be(new Callsign("GB7RDG", 1));
    }

    [Fact]
    public void A_broken_hosts_file_is_refused_rather_than_ignored()
    {
        using var _ = new ConfigScope(hosts: "gb7rdg\n");

        Axsocks.Program.ParseArgs(["--serial", "/dev/ttyUSB0", "-s", "M0LTE-7"]).Should().BeNull();
    }

    [Fact]
    public void An_unknown_option_is_refused()
    {
        using var _ = new ConfigScope();

        Axsocks.Program.ParseArgs(["--frobnicate", "--serial", "/dev/ttyUSB0", "-s", "M0LTE-7"]).Should().BeNull();
    }

    [Fact]
    public void A_destination_on_the_line_is_refused()
    {
        // axsocks takes a port and nothing else: the destination of any given
        // session is whatever the SOCKS client asks for.
        using var _ = new ConfigScope(ports: OnePort);

        Axsocks.Program.ParseArgs(["radio", "gb7rdg"]).Should().BeNull();
    }

    [Fact]
    public void A_window_above_the_modulo_8_ceiling_is_refused()
    {
        using var _ = new ConfigScope();

        Axsocks.Program.ParseArgs(["--serial", "/dev/ttyUSB0", "-s", "M0LTE-7", "-w", "8"]).Should().BeNull();
        Axsocks.Program.ParseArgs(["--serial", "/dev/ttyUSB0", "-s", "M0LTE-7", "-w", "8", "--mod128"]).Should().NotBeNull();
    }
}
