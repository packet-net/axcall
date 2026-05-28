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
}
