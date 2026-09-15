using AwesomeAssertions;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// The inetd file: which callsign a caller reached, and what should answer.
/// </summary>
public sealed class InetdFileTests
{
    private static InetdRule Parse(string line)
    {
        InetdFile.TryParseLine(line, out var rule, out var error).Should().BeTrue(error);
        rule.Should().NotBeNull();
        return rule!;
    }

    private static string Reject(string line)
    {
        InetdFile.TryParseLine(line, out _, out var error).Should().BeFalse();
        return error!;
    }

    [Fact]
    public void Reads_an_exec_rule_with_its_arguments()
    {
        var rule = Parse("M0LTE-1  radio  exec  /usr/local/bin/bbs --user %r");

        rule.Callsign.Should().Be(new Callsign("M0LTE", 1));
        rule.Port.Should().Be("radio");
        rule.Action.Should().Be(InetdAction.Exec);
        rule.Target.Should().Be("/usr/local/bin/bbs");
        rule.Arguments.Should().Equal("--user", "%r");
    }

    [Fact]
    public void Reads_a_tcp_rule()
    {
        var rule = Parse("M0LTE-2  radio  tcp  127.0.0.1:8080");

        rule.Action.Should().Be(InetdAction.Tcp);
        rule.Target.Should().Be("127.0.0.1:8080");
        rule.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void The_port_column_is_optional()
    {
        Parse("M0LTE-1  -  exec  /bin/true").Port.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    public void Blank_and_comment_lines_are_not_rules(string line)
    {
        InetdFile.TryParseLine(line, out var rule, out var error).Should().BeTrue(error);
        rule.Should().BeNull();
    }

    [Fact]
    public void A_short_line_is_an_error()
    {
        Reject("M0LTE-1  radio  exec").Should().Contain("4 columns");
    }

    [Fact]
    public void An_unknown_action_is_an_error()
    {
        Reject("M0LTE-1  radio  frobnicate  /bin/true").Should().Contain("want exec or tcp");
    }

    /// <summary>
    /// A bare program name would be resolved against whatever PATH the service
    /// inherited, which is not a thing to leave to chance when the trigger is a
    /// stranger calling in.
    /// </summary>
    [Fact]
    public void Exec_insists_on_an_absolute_path()
    {
        Reject("M0LTE-1  radio  exec  bbs").Should().Contain("absolute path");
    }

    [Fact]
    public void Tcp_takes_one_endpoint_and_nothing_else()
    {
        Reject("M0LTE-1  radio  tcp  127.0.0.1:8080 extra").Should().Contain("one host:port");
        Reject("M0LTE-1  radio  tcp  not-an-endpoint").Should().Contain("tcp:");
    }

    [Fact]
    public void An_invalid_callsign_is_an_error()
    {
        Reject("toolongforacallsign  radio  exec  /bin/true").Should().Contain("invalid callsign");
    }

    [Fact]
    public void Later_files_replace_earlier_rules_for_the_same_callsign()
    {
        var system = TempFile("M0LTE-1  radio  exec  /bin/true\nM0LTE-2  radio  tcp  127.0.0.1:8080\n");
        var user = TempFile("M0LTE-1  radio  exec  /bin/false\n");
        try
        {
            InetdFile.TryLoad([system, user], out var rules, out var error).Should().BeTrue(error);
            rules![new Callsign("M0LTE", 1)].Target.Should().Be("/bin/false");
            rules[new Callsign("M0LTE", 2)].Action.Should().Be(InetdAction.Tcp);
        }
        finally
        {
            File.Delete(system);
            File.Delete(user);
        }
    }

    [Fact]
    public void A_malformed_line_fails_the_whole_load_and_says_where()
    {
        var path = TempFile("M0LTE-1  radio  exec  /bin/true\nbroken\n");
        try
        {
            InetdFile.TryLoad([path], out var rules, out var error).Should().BeFalse();
            rules.Should().BeNull();
            error.Should().Contain(":2:");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_is_not_an_error()
    {
        InetdFile.TryLoad([Path.Combine(Path.GetTempPath(), "axcall-no-such-inetd")], out var rules, out var error)
            .Should().BeTrue(error);
        rules.Should().BeEmpty();
    }

    [Fact]
    public void The_caller_is_substituted_into_the_arguments()
    {
        var rule = Parse("M0LTE-1  radio  exec  /usr/local/bin/bbs --user %r --log /var/log/%r.log");

        InetdFile.ExpandArguments(rule, new Callsign("GB7RDG", 1))
            .Should().Equal("--user", "GB7RDG-1", "--log", "/var/log/GB7RDG-1.log");
    }

    [Fact]
    public void Arguments_without_the_placeholder_are_left_alone()
    {
        var rule = Parse("M0LTE-1  radio  exec  /bin/true --plain");

        InetdFile.ExpandArguments(rule, new Callsign("GB7RDG", 1)).Should().Equal("--plain");
    }

    private static string TempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"axcall-inetd-{Guid.NewGuid():N}");
        File.WriteAllText(path, contents);
        return path;
    }
}
