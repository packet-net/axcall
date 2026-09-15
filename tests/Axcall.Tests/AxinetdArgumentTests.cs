using AwesomeAssertions;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// axinetd's command line. It shares the ports file and the transport spec
/// with axcall; what is its own is the rules file and which callsigns it ends
/// up answering for.
/// </summary>
[Collection(ConfigFilesCollection.Name)]
public sealed class AxinetdArgumentTests
{
    private sealed class ConfigScope : IDisposable
    {
        private readonly string? previousPorts;
        private readonly string? previousInetd;
        private readonly string portsPath;
        private readonly string inetdPath;

        public ConfigScope(string? ports = null, string? inetd = null)
        {
            portsPath = Path.Combine(Path.GetTempPath(), $"axinetd-ports-{Guid.NewGuid():N}");
            inetdPath = Path.Combine(Path.GetTempPath(), $"axinetd-rules-{Guid.NewGuid():N}");
            if (ports is not null) File.WriteAllText(portsPath, ports);
            if (inetd is not null) File.WriteAllText(inetdPath, inetd);

            previousPorts = Environment.GetEnvironmentVariable(PortsFile.PathEnvVar);
            previousInetd = Environment.GetEnvironmentVariable(InetdFile.PathEnvVar);
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, portsPath);
            Environment.SetEnvironmentVariable(InetdFile.PathEnvVar, inetdPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, previousPorts);
            Environment.SetEnvironmentVariable(InetdFile.PathEnvVar, previousInetd);
            try { File.Delete(portsPath); } catch (IOException) { }
            try { File.Delete(inetdPath); } catch (IOException) { }
        }
    }

    private const string OnePort = "radio  M0LTE-7  /dev/ttyUSB0:57600  256  4  txdelay=300  144.800 MHz\n";

    private const string ThreeRules = """
        M0LTE-3  radio  exec  /bin/true
        M0LTE-1  radio  exec  /usr/local/bin/bbs --user %r
        M0LTE-2  other  tcp   127.0.0.1:8080
        """;

    [Fact]
    public void Answers_for_every_callsign_on_this_port()
    {
        using var _ = new ConfigScope(ports: OnePort, inetd: ThreeRules);

        var parsed = Axinetd.Program.ParseArgs(["radio"]);

        parsed.Should().NotBeNull();
        // M0LTE-2 names another port, so it belongs to another instance.
        parsed!.Callsigns.Should().Equal(new Callsign("M0LTE", 1), new Callsign("M0LTE", 3));
        parsed.Rules.Should().HaveCount(2);
    }

    /// <summary>
    /// The listener takes one callsign as its own and the rest as aliases, so
    /// the order has to be the same every run rather than whatever the file or
    /// a hash gave back.
    /// </summary>
    [Fact]
    public void The_callsigns_come_out_in_a_stable_order()
    {
        using var _ = new ConfigScope(ports: OnePort, inetd: ThreeRules);

        var first = Axinetd.Program.ParseArgs(["radio"])!.Callsigns;
        var again = Axinetd.Program.ParseArgs(["radio"])!.Callsigns;

        first.Should().Equal(again);
        first[0].Should().Be(new Callsign("M0LTE", 1));
    }

    [Fact]
    public void A_rule_without_a_port_applies_to_whichever_port_this_is()
    {
        using var _ = new ConfigScope(ports: OnePort, inetd: "M0LTE-1  -  exec  /bin/true\n");

        Axinetd.Program.ParseArgs(["radio"])!.Callsigns.Should().Equal(new Callsign("M0LTE", 1));
    }

    [Fact]
    public void Nothing_to_answer_for_is_a_startup_error()
    {
        // Better than starting and sitting there deaf: the operator meant
        // something by running this, and on this port it would do nothing.
        using var _ = new ConfigScope(ports: OnePort, inetd: "M0LTE-2  other  exec  /bin/true\n");

        Axinetd.Program.ParseArgs(["radio"]).Should().BeNull();
    }

    [Fact]
    public void A_broken_rules_file_is_refused_rather_than_ignored()
    {
        using var _ = new ConfigScope(ports: OnePort, inetd: "M0LTE-1  radio  exec  relative/path\n");

        Axinetd.Program.ParseArgs(["radio"]).Should().BeNull();
    }

    [Fact]
    public void The_port_still_supplies_the_transport_and_its_defaults()
    {
        using var _ = new ConfigScope(ports: OnePort, inetd: "M0LTE-1  radio  exec  /bin/true\n");

        var parsed = Axinetd.Program.ParseArgs(["radio"]);

        parsed!.Transport.Device.Should().Be("/dev/ttyUSB0");
        parsed.BaudRate.Should().Be(57600);
        parsed.Paclen.Should().Be(256);
        parsed.Window.Should().Be(4);
        parsed.Channel.TxDelay.Should().Be(30);
    }

    /// <summary>
    /// The identity comes from the rules, not from the port. A port's callsign
    /// is who axcall calls out as; who axinetd answers for is a different
    /// question with a different answer.
    /// </summary>
    [Fact]
    public void The_ports_file_callsign_does_not_decide_who_it_answers_for()
    {
        using var _ = new ConfigScope(ports: OnePort, inetd: "GB7RDG-1  radio  exec  /bin/true\n");

        Axinetd.Program.ParseArgs(["radio"])!.Callsigns.Should().Equal(new Callsign("GB7RDG", 1));
    }

    [Fact]
    public void An_explicit_config_file_replaces_the_search()
    {
        using var scope = new ConfigScope(ports: OnePort, inetd: "M0LTE-1  radio  exec  /bin/true\n");
        var other = Path.Combine(Path.GetTempPath(), $"axinetd-other-{Guid.NewGuid():N}");
        File.WriteAllText(other, "GB7CIP-1  radio  exec  /bin/true\n");
        try
        {
            Axinetd.Program.ParseArgs(["--config", other, "radio"])!
                .Callsigns.Should().Equal(new Callsign("GB7CIP", 1));
        }
        finally
        {
            File.Delete(other);
        }
    }

    [Fact]
    public void An_unknown_option_is_refused()
    {
        using var _ = new ConfigScope(ports: OnePort, inetd: "M0LTE-1  radio  exec  /bin/true\n");

        Axinetd.Program.ParseArgs(["--frobnicate", "radio"]).Should().BeNull();
    }
}
