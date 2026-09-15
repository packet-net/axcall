using AwesomeAssertions;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// The hosts file: names for stations, so a SOCKS client asking for a hostname
/// gets a callsign.
/// </summary>
public sealed class HostsFileTests
{
    private static HostEntry Parse(string line)
    {
        HostsFile.TryParseLine(line, out var entry, out var error).Should().BeTrue(error);
        entry.Should().NotBeNull();
        return entry!;
    }

    private static string Reject(string line)
    {
        HostsFile.TryParseLine(line, out _, out var error).Should().BeFalse();
        return error!;
    }

    [Fact]
    public void Reads_a_name_a_callsign_and_a_port()
    {
        Parse("gb7rdg  GB7RDG-1  radio")
            .Should().Be(new HostEntry("gb7rdg", new Callsign("GB7RDG", 1), "radio"));
    }

    [Fact]
    public void Port_is_optional()
    {
        Parse("lab  M0LTE-2").Port.Should().BeNull();
        Parse("lab  M0LTE-2  -").Port.Should().BeNull();
    }

    [Fact]
    public void Callsigns_do_not_have_to_be_typed_in_capitals()
    {
        Parse("gb7rdg  gb7rdg").Callsign.Should().Be(new Callsign("GB7RDG", 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("   # indented comment")]
    public void Blank_and_comment_lines_are_not_entries(string line)
    {
        HostsFile.TryParseLine(line, out var entry, out var error).Should().BeTrue(error);
        entry.Should().BeNull();
    }

    [Fact]
    public void A_line_with_only_a_name_is_an_error()
    {
        Reject("gb7rdg").Should().Contain("2 columns");
    }

    [Fact]
    public void An_invalid_callsign_is_an_error()
    {
        Reject("gb7rdg  toolongforacallsign").Should().Contain("invalid callsign");
    }

    [Fact]
    public void A_name_that_could_not_survive_a_client_is_an_error()
    {
        // Anything outside the hostname alphabet would be mangled or rejected
        // somewhere between the user and us, so it is refused here where the
        // message can say why.
        Reject("gb7_rdg  GB7RDG").Should().Contain("not a usable hostname");
        Reject("-lead  GB7RDG").Should().Contain("not a usable hostname");
    }

    [Fact]
    public void Later_files_replace_earlier_entries_of_the_same_name()
    {
        var system = TempFile("gb7rdg  GB7RDG   radio\ngb7cip  GB7CIP-1  radio\n");
        var user = TempFile("gb7rdg  GB7RDG-7  radio\n");
        try
        {
            HostsFile.TryLoad([system, user], out var entries, out var error).Should().BeTrue(error);
            entries!["gb7rdg"].Callsign.Should().Be(new Callsign("GB7RDG", 7));
            entries["gb7cip"].Callsign.Should().Be(new Callsign("GB7CIP", 1));
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
        var path = TempFile("gb7rdg  GB7RDG\nbroken\n");
        try
        {
            HostsFile.TryLoad([path], out var entries, out var error).Should().BeFalse();
            entries.Should().BeNull();
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
        HostsFile.TryLoad([Path.Combine(Path.GetTempPath(), "axcall-no-such-hosts")], out var entries, out var error)
            .Should().BeTrue(error);
        entries.Should().BeEmpty();
    }

    [Fact]
    public void Names_resolve_regardless_of_case()
    {
        var entries = Load("gb7rdg  GB7RDG-1  radio\n");

        HostsFile.TryResolve(entries, "GB7RDG", out var callsign, out _, out var error).Should().BeTrue(error);
        callsign.Should().Be(new Callsign("GB7RDG", 1));
    }

    /// <summary>
    /// The entry wins over the literal reading, because a name is a local
    /// choice: "gb7rdg" has to be able to mean GB7RDG-1 rather than GB7RDG.
    /// </summary>
    [Fact]
    public void An_entry_beats_the_name_read_as_a_callsign()
    {
        var entries = Load("gb7rdg  GB7RDG-1  radio\n");

        HostsFile.TryResolve(entries, "gb7rdg", out var callsign, out var port, out _).Should().BeTrue();
        callsign.Should().Be(new Callsign("GB7RDG", 1));
        port.Should().Be("radio");
    }

    [Fact]
    public void A_name_that_is_a_callsign_needs_no_entry()
    {
        HostsFile.TryResolve(Load(""), "gb7rdg-1", out var callsign, out var port, out var error)
            .Should().BeTrue(error);
        callsign.Should().Be(new Callsign("GB7RDG", 1));
        port.Should().BeNull();
    }

    [Fact]
    public void An_unknown_name_that_is_not_a_callsign_lists_what_is_known()
    {
        var entries = Load("gb7rdg  GB7RDG\ngb7cip  GB7CIP-1\n");

        HostsFile.TryResolve(entries, "example.com", out _, out _, out var error).Should().BeFalse();
        error.Should().Contain("gb7rdg").And.Contain("gb7cip");
    }

    private static Dictionary<string, HostEntry> Load(string contents)
    {
        var path = TempFile(contents);
        try
        {
            HostsFile.TryLoad([path], out var entries, out var error).Should().BeTrue(error);
            return entries!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"axcall-hosts-{Guid.NewGuid():N}");
        File.WriteAllText(path, contents);
        return path;
    }
}
