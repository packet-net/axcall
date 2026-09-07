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
}
