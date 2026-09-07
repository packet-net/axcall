using AwesomeAssertions;
using Xunit;

namespace Axcall.Tests;

public sealed class ArgumentParsingTests
{
    [Fact]
    public async Task No_Args_Returns_Exit_Code_2()
    {
        var code = await Program.Main([]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Help_Flag_Returns_Exit_Code_0()
    {
        var code = await Program.Main(["--help"]);
        code.Should().Be(0);
    }

    [Fact]
    public async Task Short_Help_Flag_Returns_Exit_Code_0()
    {
        var code = await Program.Main(["-h"]);
        code.Should().Be(0);
    }

    [Fact]
    public async Task Version_Flag_Returns_Exit_Code_0()
    {
        var code = await Program.Main(["--version"]);
        code.Should().Be(0);
    }

    [Fact]
    public async Task Short_Version_Flag_Returns_Exit_Code_0()
    {
        var code = await Program.Main(["-V"]);
        code.Should().Be(0);
    }

    [Fact]
    public async Task Missing_Mycall_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["G7RUX", "-t", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Missing_Transport_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["G7RUX", "-s", "M0LTE"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Both_Port_And_Tcp_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-p", "/dev/ttyUSB0", "-t", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Listen_With_Destination_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["-l", "G7RUX", "-s", "M0LTE", "-t", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Missing_Destination_Without_Listen_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["-s", "M0LTE", "-t", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Invalid_Callsign_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["G7RUX", "-s", "", "-t", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Unknown_Option_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--bogus"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Invalid_Baud_Rate_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-p", "/dev/ttyUSB0", "-b", "notanumber"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Tcp_Connection_Refused_Returns_Exit_Code_3()
    {
        // Port 1 is almost certainly not listening
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-t", "127.0.0.1:1"]);
        code.Should().Be(3);
    }

    [Fact]
    public void Defaults_Are_Mod8_Dial_And_300s_Keepalive()
    {
        var parsed = Program.ParseArgs(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001"]);
        parsed.Should().NotBeNull();
        parsed!.Mod128.Should().BeFalse();
        parsed.Keepalive.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void Mod128_And_Keepalive_Flags_Are_Parsed()
    {
        var parsed = Program.ParseArgs(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--mod128", "--keepalive", "60"]);
        parsed.Should().NotBeNull();
        parsed!.Mod128.Should().BeTrue();
        parsed.Keepalive.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Keepalive_Applies_In_Listen_Mode()
    {
        var parsed = Program.ParseArgs(["-l", "-s", "M0LTE", "-t", "localhost:8001", "--keepalive", "120"]);
        parsed.Should().NotBeNull();
        parsed!.Listen.Should().BeTrue();
        parsed.Keepalive.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Largest_Keepalive_Is_Accepted()
    {
        var parsed = Program.ParseArgs(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--keepalive", "4294967"]);
        parsed.Should().NotBeNull();
        parsed!.Keepalive.Should().Be(TimeSpan.FromSeconds(Program.MaxKeepaliveSeconds));
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
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--keepalive", value]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Missing_Keepalive_Value_Returns_Exit_Code_2()
    {
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--keepalive"]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task Mod128_Does_Not_Take_A_Value()
    {
        // The token after --mod128 is the destination, so a second one is unexpected.
        var code = await Program.Main(["--mod128", "G7RUX", "EXTRA", "-s", "M0LTE", "-t", "localhost:8001"]);
        code.Should().Be(2);
    }

    [Fact]
    public void Defaults_Leave_Link_Parameters_To_The_Library()
    {
        // No flag given means null: the library's own default applies and
        // axcall never restates it.
        var parsed = Program.ParseArgs(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001"]);
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
        var parsed = Program.ParseArgs([
            "G7RUX", "-s", "M0LTE", "-t", "localhost:8001",
            "--window", "2", "--paclen", "128", "--retries", "5",
            "--frack", "2.5", "--ack-delay", "0.5", "--no-xid"]);
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
            "-l", "-s", "M0LTE", "-t", "localhost:8001",
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
        var parsed = Program.ParseArgs(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--window", value]);
        parsed.Should().NotBeNull();
        parsed!.Window.Should().Be(expected);
    }

    [Fact]
    public async Task Window_8_Is_Rejected_Without_Mod128()
    {
        // Modulo 8 numbers frames 0..7, so at most 7 can be outstanding.
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--window", "8"]);
        code.Should().Be(2);
    }

    [Theory]
    [InlineData("8", 8)]
    [InlineData("127", 127)]
    public void Window_Up_To_127_Is_Accepted_With_Mod128(string value, int expected)
    {
        // --mod128 after --window still lifts the ceiling: every option is
        // parsed before any is validated.
        var parsed = Program.ParseArgs(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--window", value, "--mod128"]);
        parsed.Should().NotBeNull();
        parsed!.Mod128.Should().BeTrue();
        parsed.Window.Should().Be(expected);
    }

    [Fact]
    public async Task Window_128_Is_Rejected_Even_With_Mod128()
    {
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--mod128", "--window", "128"]);
        code.Should().Be(2);
    }

    [Fact]
    public void Ack_Delay_Zero_Is_Accepted()
    {
        // Zero is a documented value: acknowledge every frame at once.
        var parsed = Program.ParseArgs(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", "--ack-delay", "0"]);
        parsed.Should().NotBeNull();
        parsed!.AckDelay.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("--paclen", "1", 1)]
    [InlineData("--paclen", "1024", 1024)]
    [InlineData("--retries", "1", 1)]
    [InlineData("--retries", "255", 255)]
    public void Count_Bounds_Are_Inclusive(string flag, string value, int expected)
    {
        var parsed = Program.ParseArgs(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", flag, value]);
        parsed.Should().NotBeNull();
        (flag == "--paclen" ? parsed!.Paclen : parsed!.Retries).Should().Be(expected);
    }

    [Theory]
    [InlineData("--frack", "0.5", 0.5)]
    [InlineData("--frack", "60", 60)]
    [InlineData("--ack-delay", "30", 30)]
    public void Timer_Bounds_Are_Inclusive(string flag, string value, double expectedSeconds)
    {
        var parsed = Program.ParseArgs(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", flag, value]);
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
    public async Task Invalid_Link_Parameter_Returns_Exit_Code_2(string flag, string value)
    {
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", flag, value]);
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
        var code = await Program.Main(["G7RUX", "-s", "M0LTE", "-t", "localhost:8001", flag]);
        code.Should().Be(2);
    }

    [Fact]
    public async Task No_Xid_Does_Not_Take_A_Value()
    {
        // The token after --no-xid is the destination, so a second one is unexpected.
        var code = await Program.Main(["--no-xid", "G7RUX", "EXTRA", "-s", "M0LTE", "-t", "localhost:8001"]);
        code.Should().Be(2);
    }
}
