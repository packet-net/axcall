using AwesomeAssertions;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// The command line, which is deliberately kernel AX.25 axcall's:
/// <c>axcall [options] &lt;port&gt; &lt;destination&gt;</c>, with the classic
/// short letters keeping their classic meanings and axcall's own additions
/// spelled long.
/// </summary>
/// <remarks>
/// Nothing here uses a bare port name, so nothing here reads the ports file.
/// Those cases live in <see cref="PortsFileTests"/>, which owns the
/// process-wide environment variable that points at it.
/// </remarks>
public sealed class ArgumentParsingTests
{
    // The canonical "rest of the line" for tests about one flag: a TCP port
    // given as a flag, so the single positional is the destination.
    private static string[] Line(params string[] extra)
        => [.. new[] { "G7RUX", "-s", "M0LTE", "--tcp", "localhost:8001" }, .. extra];

    [Fact]
    public async Task No_Args_Returns_Exit_Code_2()
    {
        var code = await Program.Main([]);
        code.Should().Be(2);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_Returns_Exit_Code_0(string flag)
    {
        var code = await Program.Main([flag]);
        code.Should().Be(0);
    }

    [Fact]
    public async Task Help_Flag_Wins_Over_Other_Arguments()
    {
        var code = await Program.Main(["--help", "G7RUX", "-s", "M0LTE", "--tcp", "localhost:8001"]);
        code.Should().Be(0);
    }

    [Fact]
    public async Task Short_h_Alongside_Other_Arguments_Is_A_Usage_Error()
    {
        // Classic axcall's -h selects slave mode. Printing help and exiting 0
        // would look like a successful call to a ported script, so this case
        // fails loudly rather than silently.
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "--tcp", "localhost:8001", "-h"]);
        code.Should().Be(2);
    }

    [Theory]
    // -v is classic axcall's spelling; -V and --version are ours.
    [InlineData("-v")]
    [InlineData("-V")]
    [InlineData("--version")]
    public async Task Version_Returns_Exit_Code_0(string flag)
    {
        var code = await Program.Main([flag]);
        code.Should().Be(0);
    }

    // The classic invocation shape: port first, destination second.

    [Fact]
    public void Positional_Device_Path_Is_The_Port()
    {
        var parsed = Program.ParseArgs(["/dev/ttyUSB0", "GB7RDG", "-s", "M0LTE"]);

        parsed.Should().NotBeNull();
        parsed!.Transport.IsSerial.Should().BeTrue();
        parsed.Transport.Device.Should().Be("/dev/ttyUSB0");
        parsed.BaudRate.Should().Be(57600);
        parsed.Target!.Value.Base.Should().Be("GB7RDG");
    }

    [Fact]
    public void Positional_Device_Path_Can_Carry_A_Baud_Rate()
    {
        var parsed = Program.ParseArgs(["/dev/ttyUSB0:9600", "GB7RDG", "-s", "M0LTE"]);

        parsed.Should().NotBeNull();
        parsed!.BaudRate.Should().Be(9600);
    }

    [Fact]
    public void Baud_Flag_Overrides_A_Baud_Suffix()
    {
        var parsed = Program.ParseArgs(["/dev/ttyUSB0:9600", "GB7RDG", "-s", "M0LTE", "--baud", "115200"]);

        parsed.Should().NotBeNull();
        parsed!.BaudRate.Should().Be(115200);
    }

    [Fact]
    public void Positional_Endpoint_Is_The_Port()
    {
        var parsed = Program.ParseArgs(["10.45.0.66:8001", "GB7RDG", "-s", "M0LTE"]);

        parsed.Should().NotBeNull();
        parsed!.Transport.IsSerial.Should().BeFalse();
        parsed.Transport.Host.Should().Be("10.45.0.66");
        parsed.Transport.TcpPort.Should().Be(8001);
    }

    [Fact]
    public void Serial_Flag_Leaves_The_Single_Positional_As_The_Destination()
    {
        var parsed = Program.ParseArgs(["GB7RDG", "-s", "M0LTE", "--serial", "/dev/ttyUSB0"]);

        parsed.Should().NotBeNull();
        parsed!.Transport.Device.Should().Be("/dev/ttyUSB0");
        parsed.Target!.Value.Base.Should().Be("GB7RDG");
    }

    [Fact]
    public void Listen_With_A_Transport_Flag_Takes_No_Positional()
    {
        var parsed = Program.ParseArgs(["-l", "-s", "M0LTE", "--tcp", "localhost:8001"]);

        parsed.Should().NotBeNull();
        parsed!.Listen.Should().BeTrue();
        parsed.Target.Should().BeNull();
    }

    [Fact]
    public async Task Missing_Port_And_Destination_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["-s", "M0LTE"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Missing_Destination_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["-s", "M0LTE", "--tcp", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Missing_Mycall_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["G7RUX", "--tcp", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Both_Serial_And_Tcp_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "--serial", "/dev/ttyUSB0", "--tcp", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Listen_With_Destination_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["-l", "G7RUX", "-s", "M0LTE", "--tcp", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task A_Third_Positional_Reports_The_Digipeater_Gap()
    {
        // "axcall ax0 gb7rdg gb7cip" and "axcall ax0 gb7rdg via gb7cip" are
        // both valid classic invocations. Neither is supported yet, and
        // dialling direct while ignoring the path would be worse than failing.
        var code = await Program.Main(["/dev/ttyUSB0", "GB7RDG", "GB7CIP", "-s", "M0LTE"]);
        code.Should().Be(2);
    }

    [Theory]
    // An empty -s, and a destination that is not a callsign.
    [InlineData("G7RUX", "")]
    [InlineData("not+a+call", "M0LTE")]
    public async Task Invalid_Callsign_Returns_Exit_Code_2(string destination, string mycall)
    {
        var code = await Program.Main([destination, "-s", mycall, "--tcp", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Unknown_Option_Returns_Exit_Code_2()
    {
        var code = await Program.Main(Line("--bogus"));
        code.Should().Be(2);
    }

    [Fact]
    public async Task Tcp_Connection_Refused_Returns_Exit_Code_3()
    {
        // Port 1 is almost certainly not listening
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "--tcp", "127.0.0.1:1"]);
        code.Should().Be(3);
    }

    // Classic short letters, back to their classic meanings.

    [Fact]
    public void Short_p_Is_Paclen_And_Short_w_Is_Window()
    {
        var parsed = Program.ParseArgs(Line("-p", "128", "-w", "2"));

        parsed.Should().NotBeNull();
        parsed!.Paclen.Should().Be(128);
        parsed.Window.Should().Be(2);
    }

    [Theory]
    [InlineData("s", false)]
    [InlineData("e", true)]
    [InlineData("E", true)]
    public void Short_m_Selects_The_Modulus(string value, bool expectMod128)
    {
        var parsed = Program.ParseArgs(Line("-m", value));

        parsed.Should().NotBeNull();
        parsed!.Mod128.Should().Be(expectMod128);
    }

    [Fact]
    public void Mod128_Is_Still_Accepted_As_A_Long_Spelling_Of_m_e()
    {
        Program.ParseArgs(Line("--mod128"))!.Mod128.Should().BeTrue();
    }

    [Theory]
    [InlineData("-m", "x")]
    [InlineData("-m", "9600")]
    [InlineData("-b", "x")]
    // The value is validated even though backoff is ignored, so "-b 9600" (the
    // baud rate this flag used to mean here) fails rather than passing quietly.
    [InlineData("-b", "9600")]
    public async Task Invalid_Choice_Values_Return_Exit_Code_2(string flag, string value)
    {
        var code = await Program.Main(Line(flag, value));
        code.Should().Be(2);
    }

    [Theory]
    [InlineData("-b")]
    [InlineData("-m")]
    [InlineData("-p")]
    [InlineData("-w")]
    [InlineData("-s")]
    [InlineData("--serial")]
    [InlineData("--tcp")]
    [InlineData("--baud")]
    [InlineData("--idle-timeout")]
    public async Task Missing_Value_Returns_Exit_Code_2(string flag)
    {
        var code = await Program.Main([.. Line(), flag]);
        code.Should().Be(2);
    }

    [Theory]
    // Backoff: the library adapts T1 from the measured round trip.
    [InlineData("-b", "l")]
    [InlineData("-b", "e")]
    public void Backoff_Is_Accepted_And_Ignored(string flag, string value)
    {
        Program.ParseArgs(Line(flag, value)).Should().NotBeNull();
    }

    [Theory]
    // Screen modes, remote commands and encoding: axcall is always raw, always
    // UTF-8, and has no remote commands to disable.
    [InlineData("-r")]
    [InlineData("-t")]
    [InlineData("-R")]
    [InlineData("-8")]
    public void Inapplicable_Classic_Flags_Are_Accepted_And_Ignored(string flag)
    {
        Program.ParseArgs(Line(flag)).Should().NotBeNull();
    }

    [Fact]
    public async Task Ibm850_Is_Refused_With_Its_Own_Reason()
    {
        // The one classic flag we cannot honour. Ignoring it would produce
        // mojibake rather than nothing, so it fails rather than being accepted.
        var code = await Program.Main(Line("-i"));
        code.Should().Be(2);
    }

    // The terminal behaviours: -T, -W, -S and -d.

    [Theory]
    [InlineData("-T", "60", 60)]
    [InlineData("--idle-timeout", "0.5", 0.5)]
    // The kernel version's floor, one millisecond.
    [InlineData("-T", "0.001", 0.001)]
    public void Idle_Timeout_Is_Parsed(string flag, string value, double expectedSeconds)
    {
        Program.ParseArgs(Line(flag, value))!.IdleTimeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0009")]
    [InlineData("-1")]
    [InlineData("notanumber")]
    [InlineData("NaN")]
    // One past the timer ceiling, same as --keepalive's.
    [InlineData("4294968")]
    public async Task Invalid_Idle_Timeout_Returns_Exit_Code_2(string value)
    {
        var code = await Program.Main(Line("-T", value));
        code.Should().Be(2);
    }

    [Fact]
    public async Task Missing_Idle_Timeout_Value_Returns_Exit_Code_2()
    {
        var code = await Program.Main(Line("-T"));
        code.Should().Be(2);
    }

    [Theory]
    [InlineData("-W")]
    [InlineData("--wait")]
    public void Wait_Is_Parsed(string flag)
    {
        Program.ParseArgs(Line(flag))!.Wait.Should().BeTrue();
    }

    [Theory]
    [InlineData("-S")]
    [InlineData("--silent")]
    public void Silent_Is_Parsed(string flag)
    {
        Program.ParseArgs(Line(flag))!.Silent.Should().BeTrue();
    }

    [Theory]
    [InlineData("-d")]
    [InlineData("--debug")]
    public void Debug_Is_Parsed(string flag)
    {
        Program.ParseArgs(Line(flag))!.TraceFrames.Should().BeTrue();
    }

    [Fact]
    public void The_Behaviour_Flags_Default_Off()
    {
        var parsed = Program.ParseArgs(Line());
        parsed.Should().NotBeNull();
        parsed!.IdleTimeout.Should().BeNull();
        parsed.Wait.Should().BeFalse();
        parsed.Silent.Should().BeFalse();
        parsed.TraceFrames.Should().BeFalse();
    }

    [Fact]
    public void The_Behaviour_Flags_Combine()
    {
        // -W with -T is the pairing worth having: -W alone against a peer that
        // never hangs up waits for ever.
        var parsed = Program.ParseArgs(Line("-W", "-T", "30", "-S", "-d"));
        parsed.Should().NotBeNull();
        parsed!.Wait.Should().BeTrue();
        parsed.IdleTimeout.Should().Be(TimeSpan.FromSeconds(30));
        parsed.Silent.Should().BeTrue();
        parsed.TraceFrames.Should().BeTrue();
    }

    [Theory]
    [InlineData("-W")]
    [InlineData("-S")]
    [InlineData("-d")]
    public async Task The_Boolean_Behaviour_Flags_Take_No_Value(string flag)
    {
        // The token after the flag is the destination, so a second one is a
        // second positional and therefore unexpected.
        var code = await Program.Main([flag, "G7RUX", "EXTRA", "-s", "M0LTE", "--tcp", "localhost:8001"]);
        code.Should().Be(2);
    }

    // Link parameters.

    [Fact]
    public void Defaults_Are_Mod8_Dial_And_300s_Keepalive()
    {
        var parsed = Program.ParseArgs(Line());
        parsed.Should().NotBeNull();
        parsed!.Mod128.Should().BeFalse();
        parsed.Keepalive.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void Keepalive_Is_Parsed()
    {
        Program.ParseArgs(Line("--keepalive", "60"))!.Keepalive.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Keepalive_Applies_In_Listen_Mode()
    {
        var parsed = Program.ParseArgs(["-l", "-s", "M0LTE", "--tcp", "localhost:8001", "--keepalive", "120"]);
        parsed.Should().NotBeNull();
        parsed!.Listen.Should().BeTrue();
        parsed.Keepalive.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Largest_Keepalive_Is_Accepted()
    {
        Program.ParseArgs(Line("--keepalive", "4294967"))!
            .Keepalive.Should().Be(TimeSpan.FromSeconds(Program.MaxKeepaliveSeconds));
    }

    [Theory]
    // Zero would poll continuously (the library has no "never poll" T3), so it is rejected.
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("notanumber")]
    [InlineData("1.5")]
    // One past the timer ceiling.
    [InlineData("4294968")]
    // Beyond what a 64-bit integer holds.
    [InlineData("99999999999999999999")]
    public async Task Invalid_Keepalive_Returns_Exit_Code_2(string value)
    {
        var code = await Program.Main(Line("--keepalive", value));
        code.Should().Be(2);
    }

    [Fact]
    public async Task Missing_Keepalive_Value_Returns_Exit_Code_2()
    {
        var code = await Program.Main(Line("--keepalive"));
        code.Should().Be(2);
    }

    [Fact]
    public void Defaults_Leave_Link_Parameters_To_The_Library()
    {
        // No flag given means null: the library's own default applies and
        // axcall never restates it.
        var parsed = Program.ParseArgs(Line());
        parsed.Should().NotBeNull();
        parsed!.Window.Should().BeNull();
        parsed.Paclen.Should().BeNull();
        parsed.Retries.Should().BeNull();
        parsed.Frack.Should().BeNull();
        parsed.AckDelay.Should().BeNull();
        parsed.NoXid.Should().BeFalse();
    }

    [Fact]
    public void Link_Parameter_Flags_Are_Parsed()
    {
        var parsed = Program.ParseArgs(Line(
            "--window", "2", "--paclen", "128", "--retries", "5",
            "--frack", "2.5", "--ack-delay", "0.5", "--no-xid"));
        parsed.Should().NotBeNull();
        parsed!.Window.Should().Be(2);
        parsed.Paclen.Should().Be(128);
        parsed.Retries.Should().Be(5);
        parsed.Frack.Should().Be(TimeSpan.FromSeconds(2.5));
        parsed.AckDelay.Should().Be(TimeSpan.FromSeconds(0.5));
        parsed.NoXid.Should().BeTrue();
    }

    [Fact]
    public void Link_Parameters_Apply_In_Listen_Mode()
    {
        var parsed = Program.ParseArgs([
            "-l", "-s", "M0LTE", "--tcp", "localhost:8001",
            "--window", "7", "--paclen", "64", "--retries", "3", "--frack", "10", "--ack-delay", "1"]);
        parsed.Should().NotBeNull();
        parsed!.Listen.Should().BeTrue();
        parsed.Window.Should().Be(7);
        parsed.Paclen.Should().Be(64);
        parsed.Retries.Should().Be(3);
        parsed.Frack.Should().Be(TimeSpan.FromSeconds(10));
        parsed.AckDelay.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    public void Window_Up_To_7_Is_Accepted_Without_Mod128(string value, int expected)
    {
        Program.ParseArgs(Line("-w", value))!.Window.Should().Be(expected);
    }

    [Fact]
    public async Task Window_8_Is_Rejected_Without_Mod128()
    {
        // Modulo 8 numbers frames 0..7, so at most 7 can be outstanding.
        var code = await Program.Main(Line("-w", "8"));
        code.Should().Be(2);
    }

    [Theory]
    [InlineData("8", 8)]
    [InlineData("127", 127)]
    public void Window_Up_To_127_Is_Accepted_With_Mod128(string value, int expected)
    {
        // -m e after -w still lifts the ceiling: every option is parsed before
        // any is validated.
        var parsed = Program.ParseArgs(Line("-w", value, "-m", "e"));
        parsed.Should().NotBeNull();
        parsed!.Mod128.Should().BeTrue();
        parsed.Window.Should().Be(expected);
    }

    [Fact]
    public async Task Window_128_Is_Rejected_Even_With_Mod128()
    {
        var code = await Program.Main(Line("-m", "e", "-w", "128"));
        code.Should().Be(2);
    }

    [Fact]
    public void Ack_Delay_Zero_Is_Accepted()
    {
        // Zero is a documented value: acknowledge every frame at once.
        Program.ParseArgs(Line("--ack-delay", "0"))!.AckDelay.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("--paclen", "1", 1)]
    // Classic axcall stopped at 500; this is a superset of what it took.
    [InlineData("--paclen", "1024", 1024)]
    [InlineData("--retries", "1", 1)]
    [InlineData("--retries", "255", 255)]
    public void Count_Bounds_Are_Inclusive(string flag, string value, int expected)
    {
        var parsed = Program.ParseArgs(Line(flag, value));
        parsed.Should().NotBeNull();
        (flag == "--paclen" ? parsed!.Paclen : parsed!.Retries).Should().Be(expected);
    }

    [Theory]
    [InlineData("--frack", "0.5", 0.5)]
    [InlineData("--frack", "60", 60)]
    [InlineData("--ack-delay", "30", 30)]
    public void Timer_Bounds_Are_Inclusive(string flag, string value, double expectedSeconds)
    {
        var parsed = Program.ParseArgs(Line(flag, value));
        parsed.Should().NotBeNull();
        (flag == "--frack" ? parsed!.Frack : parsed!.AckDelay).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData("--window", "0")]
    [InlineData("--window", "-1")]
    [InlineData("--window", "1.5")]
    [InlineData("--window", "notanumber")]
    [InlineData("--paclen", "0")]
    [InlineData("--paclen", "1025")]
    [InlineData("--paclen", "-256")]
    [InlineData("--paclen", "notanumber")]
    [InlineData("--retries", "0")]
    [InlineData("--retries", "256")]
    [InlineData("--retries", "notanumber")]
    // Below the half-second floor, above the minute ceiling, and the number
    // forms the parser deliberately refuses (sign, exponent, NaN, infinity).
    [InlineData("--frack", "0")]
    [InlineData("--frack", "0.4")]
    [InlineData("--frack", "61")]
    [InlineData("--frack", "-5")]
    [InlineData("--frack", "1e1")]
    [InlineData("--frack", "NaN")]
    [InlineData("--frack", "notanumber")]
    [InlineData("--ack-delay", "-0.1")]
    [InlineData("--ack-delay", "31")]
    [InlineData("--ack-delay", "Infinity")]
    [InlineData("--ack-delay", "notanumber")]
    [InlineData("--baud", "0")]
    [InlineData("--baud", "notanumber")]
    public async Task Invalid_Link_Parameter_Returns_Exit_Code_2(string flag, string value)
    {
        var code = await Program.Main(Line(flag, value));
        code.Should().Be(2);
    }

    [Theory]
    [InlineData("--window")]
    [InlineData("--paclen")]
    [InlineData("--retries")]
    [InlineData("--frack")]
    [InlineData("--ack-delay")]
    public async Task Missing_Link_Parameter_Value_Returns_Exit_Code_2(string flag)
    {
        var code = await Program.Main(Line(flag));
        code.Should().Be(2);
    }
}
