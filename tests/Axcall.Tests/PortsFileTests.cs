using AwesomeAssertions;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// The ports file and the bare-name port argument it backs, which is what lets
/// "axcall radio gb7rdg" work the way "axcall ax0 gb7rdg" did against the
/// kernel stack.
/// </summary>
/// <remarks>
/// Every test here points <c>AXCALL_PORTS</c> at a temporary file. That
/// variable is process-wide, so this class shares
/// <see cref="ConfigFilesCollection"/> with everything else that pins it;
/// without that, two classes racing on one environment variable fail in ways
/// that look like parser bugs.
/// </remarks>
[Collection(ConfigFilesCollection.Name)]
public sealed class PortsFileTests
{
    /// <summary>
    /// Points <see cref="PortsFile.PathEnvVar"/> at a temporary file for the
    /// life of the scope, restoring whatever was there before.
    /// </summary>
    private sealed class PortsFileScope : IDisposable
    {
        private readonly string? previous;
        private readonly string path;

        private PortsFileScope(string? content)
        {
            path = Path.Combine(Path.GetTempPath(), $"axcall-ports-{Guid.NewGuid():N}");
            if (content is not null)
                File.WriteAllText(path, content);
            previous = Environment.GetEnvironmentVariable(PortsFile.PathEnvVar);
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, path);
        }

        /// <summary>A ports file with these contents.</summary>
        public static PortsFileScope With(string content) => new(content);

        /// <summary>A ports file path that does not exist.</summary>
        public static PortsFileScope Missing() => new(null);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, previous);
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private const string TwoPorts = """
        # name   callsign   transport            paclen  window  description
        radio    M0LTE-7    /dev/ttyUSB0:57600   256     4       144.800 MHz

        node     M0LTE-7    10.45.0.66:8001      -       -       LinBPQ
        """;

    [Fact]
    public void Named_Serial_Port_Supplies_Transport_Callsign_And_Defaults()
    {
        using var _ = PortsFileScope.With(TwoPorts);

        var parsed = Program.ParseArgs(["radio", "GB7RDG"]);

        parsed.Should().NotBeNull();
        parsed!.Transport.IsSerial.Should().BeTrue();
        parsed.Transport.Device.Should().Be("/dev/ttyUSB0");
        parsed.BaudRate.Should().Be(57600);
        parsed.MyCall.ToString().Should().Be("M0LTE-7");
        parsed.Target!.Value.Base.Should().Be("GB7RDG");
        parsed.Paclen.Should().Be(256);
        parsed.Window.Should().Be(4);
    }

    [Fact]
    public void Named_Tcp_Port_Supplies_Host_And_Port()
    {
        using var _ = PortsFileScope.With(TwoPorts);

        var parsed = Program.ParseArgs(["node", "GB7RDG"]);

        parsed.Should().NotBeNull();
        parsed!.Transport.IsSerial.Should().BeFalse();
        parsed.Transport.Host.Should().Be("10.45.0.66");
        parsed.Transport.TcpPort.Should().Be(8001);
        // The "-" columns leave the library's own defaults in place.
        parsed.Paclen.Should().BeNull();
        parsed.Window.Should().BeNull();
    }

    [Fact]
    public void Named_Port_Works_In_Listen_Mode_With_No_Destination()
    {
        using var _ = PortsFileScope.With(TwoPorts);

        var parsed = Program.ParseArgs(["--listen", "node"]);

        parsed.Should().NotBeNull();
        parsed!.Listen.Should().BeTrue();
        parsed.Target.Should().BeNull();
        parsed.Transport.Host.Should().Be("10.45.0.66");
        parsed.MyCall.ToString().Should().Be("M0LTE-7");
    }

    [Fact]
    public void Flags_Beat_The_Ports_File()
    {
        using var _ = PortsFileScope.With(TwoPorts);

        var parsed = Program.ParseArgs(["radio", "GB7RDG", "-s", "M0LTE-1", "-p", "128", "-w", "2", "--baud", "9600"]);

        parsed.Should().NotBeNull();
        parsed!.MyCall.ToString().Should().Be("M0LTE-1");
        parsed.Paclen.Should().Be(128);
        parsed.Window.Should().Be(2);
        parsed.BaudRate.Should().Be(9600);
    }

    [Fact]
    public void Later_Files_Replace_Same_Named_Entries()
    {
        // The system file is read before the user file, so a user entry wins.
        // AXCALL_PORTS pins a single file, so the merge is driven through
        // TryLoad's explicit-path overload instead.
        var system = Path.Combine(Path.GetTempPath(), $"axcall-system-{Guid.NewGuid():N}");
        var user = Path.Combine(Path.GetTempPath(), $"axcall-user-{Guid.NewGuid():N}");
        var absent = Path.Combine(Path.GetTempPath(), $"axcall-absent-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(system, """
                radio    M0LTE-7    /dev/ttyUSB0
                node     M0LTE-7    10.45.0.66:8001
                """);
            File.WriteAllText(user, "radio  M0LTE-9  /dev/ttyUSB1");

            PortsFile.TryLoad([system, absent, user], out var entries, out var error).Should().BeTrue();

            error.Should().BeNull();
            // The replaced entry is replaced whole, and the one only the system
            // file names survives. A path that does not exist is skipped.
            entries!.Should().HaveCount(2);
            entries["radio"].Transport.Device.Should().Be("/dev/ttyUSB1");
            entries["radio"].Callsign!.Value.ToString().Should().Be("M0LTE-9");
            entries["node"].Transport.Host.Should().Be("10.45.0.66");
        }
        finally
        {
            foreach (var path in new[] { system, user })
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public async Task Port_Without_A_Callsign_Still_Needs_Mycall()
    {
        using var _ = PortsFileScope.With("radio  -  /dev/ttyUSB0");

        var code = await Program.Main(["radio", "GB7RDG"]);
        code.Should().Be(2);
    }

    [Fact]
    public void Port_Without_A_Callsign_Is_Fine_With_Mycall()
    {
        using var _ = PortsFileScope.With("radio  -  /dev/ttyUSB0");

        var parsed = Program.ParseArgs(["radio", "GB7RDG", "-s", "M0LTE"]);

        parsed.Should().NotBeNull();
        parsed!.MyCall.ToString().Should().Be("M0LTE");
        parsed.BaudRate.Should().Be(57600);
    }

    [Fact]
    public async Task Unknown_Port_Name_Is_A_Usage_Error()
    {
        using var _ = PortsFileScope.With(TwoPorts);

        var code = await Program.Main(["nosuchport", "GB7RDG", "-s", "M0LTE"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Bare_Name_With_No_Ports_File_Is_A_Usage_Error()
    {
        using var _ = PortsFileScope.Missing();

        var code = await Program.Main(["radio", "GB7RDG", "-s", "M0LTE"]);
        code.Should().Be(2);
    }

    [Fact]
    public void Missing_File_Is_Not_An_Error_In_Itself()
    {
        using var _ = PortsFileScope.Missing();

        PortsFile.TryLoad(out var entries, out var error).Should().BeTrue();
        error.Should().BeNull();
        entries.Should().BeEmpty();
    }

    [Theory]
    // Fewer than the three mandatory columns.
    [InlineData("radio  M0LTE-7")]
    [InlineData("radio")]
    // A callsign that is not one.
    [InlineData("radio  not+a+call  /dev/ttyUSB0")]
    // A transport that is neither a path nor host:port.
    [InlineData("radio  M0LTE-7  somethingelse")]
    [InlineData("radio  M0LTE-7  localhost:99999")]
    // A name that would be read as a transport if it appeared on the command line.
    [InlineData("/dev/ttyUSB0  M0LTE-7  /dev/ttyUSB0")]
    // Optional columns that are present but not numbers.
    [InlineData("radio  M0LTE-7  /dev/ttyUSB0  notanumber")]
    [InlineData("radio  M0LTE-7  /dev/ttyUSB0  256  0")]
    public void Malformed_Lines_Are_Reported(string line)
    {
        PortsFile.TryParseLine(line, out var entry, out var error).Should().BeFalse();
        entry.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("   # an indented comment")]
    public void Blank_And_Comment_Lines_Are_Skipped(string line)
    {
        PortsFile.TryParseLine(line, out var entry, out var error).Should().BeTrue();
        entry.Should().BeNull();
        error.Should().BeNull();
    }

    [Fact]
    public async Task A_Malformed_Line_Fails_The_Whole_File()
    {
        // A typo in a config file should be reported, not quietly turned into
        // "unknown port" for the entry below it.
        using var _ = PortsFileScope.With("""
            radio    M0LTE-7    /dev/ttyUSB0
            broken
            node     M0LTE-7    10.45.0.66:8001
            """);

        var code = await Program.Main(["node", "GB7RDG"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task File_Window_Above_The_Mod8_Ceiling_Is_Rejected()
    {
        using var _ = PortsFileScope.With("radio  M0LTE-7  /dev/ttyUSB0  -  9");

        var code = await Program.Main(["radio", "GB7RDG"]);
        code.Should().Be(2);
    }

    [Fact]
    public void File_Window_Above_The_Mod8_Ceiling_Is_Fine_With_Mod128()
    {
        using var _ = PortsFileScope.With("radio  M0LTE-7  /dev/ttyUSB0  -  9");

        var parsed = Program.ParseArgs(["radio", "GB7RDG", "-m", "e"]);

        parsed.Should().NotBeNull();
        parsed!.Mod128.Should().BeTrue();
        parsed.Window.Should().Be(9);
    }

    [Fact]
    public void Channel_Settings_Are_Read_From_Key_Value_Columns()
    {
        using var _ = PortsFileScope.With(
            "radio  M0LTE-7  /dev/ttyUSB0  256  4  txdelay=300 persist=63 slottime=100 txtail=20  144.800 MHz");

        var parsed = Program.ParseArgs(["radio", "GB7RDG"]);

        parsed.Should().NotBeNull();
        // Held in KISS units: the three timers in steps of 10 ms.
        parsed!.Channel.TxDelay.Should().Be(30);
        parsed.Channel.Persist.Should().Be(63);
        parsed.Channel.SlotTime.Should().Be(10);
        parsed.Channel.TxTail.Should().Be(2);
    }

    [Fact]
    public void A_Description_Containing_An_Equals_Sign_Is_Not_A_Setting()
    {
        using var _ = PortsFileScope.With("radio  M0LTE-7  /dev/ttyUSB0  -  -  band=2m is just text");

        PortsFile.TryParseLine("radio  M0LTE-7  /dev/ttyUSB0  -  -  band=2m is just text", out var entry, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        entry!.Channel.Any.Should().BeFalse();
    }

    [Fact]
    public void Flags_Override_Channel_Settings_One_At_A_Time()
    {
        using var _ = PortsFileScope.With("radio  M0LTE-7  /dev/ttyUSB0  -  -  txdelay=300 persist=63");

        var parsed = Program.ParseArgs(["radio", "GB7RDG", "--persist", "128"]);

        parsed.Should().NotBeNull();
        // The flag replaces persist and leaves txdelay from the file alone.
        parsed!.Channel.Persist.Should().Be(128);
        parsed.Channel.TxDelay.Should().Be(30);
    }

    [Theory]
    [InlineData("radio  M0LTE-7  /dev/ttyUSB0  -  -  txdelay=305")]
    [InlineData("radio  M0LTE-7  /dev/ttyUSB0  -  -  txdelay=3000")]
    [InlineData("radio  M0LTE-7  /dev/ttyUSB0  -  -  persist=256")]
    [InlineData("radio  M0LTE-7  /dev/ttyUSB0  -  -  slottime=notanumber")]
    public void Malformed_Channel_Settings_Are_Reported(string line)
    {
        PortsFile.TryParseLine(line, out var entry, out var error).Should().BeFalse();
        entry.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Baud_Suffix_Applies_When_No_Flag_Overrides_It()
    {
        using var _ = PortsFileScope.With("radio  M0LTE-7  /dev/ttyUSB0:19200");

        var parsed = Program.ParseArgs(["radio", "GB7RDG"]);

        parsed.Should().NotBeNull();
        parsed!.BaudRate.Should().Be(19200);
    }
}
