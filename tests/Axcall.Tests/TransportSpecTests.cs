using AwesomeAssertions;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// A port spec is a device path, a TCP endpoint, or a name for the ports file
/// to resolve. The three are told apart by shape alone, before anything is
/// opened, so an unplugged /dev/ttyUSB0 reports "failed to open modem" rather
/// than "unknown port".
/// </summary>
public sealed class TransportSpecTests
{
    [Theory]
    [InlineData("/dev/ttyUSB0")]
    [InlineData("./tty")]
    [InlineData("../tty")]
    public void Paths_Are_Recognised(string spec)
    {
        TransportSpec.LooksLikePath(spec).Should().BeTrue();
        TransportSpec.LooksLikeName(spec).Should().BeFalse();
    }

    [Theory]
    [InlineData("radio")]
    [InlineData("ax0")]
    [InlineData("node-1")]
    public void Bare_Words_Are_Names(string spec)
    {
        TransportSpec.LooksLikeName(spec).Should().BeTrue();
        TransportSpec.LooksLikePath(spec).Should().BeFalse();
    }

    [Theory]
    [InlineData("localhost:8001")]
    [InlineData("10.45.0.66:8001")]
    [InlineData("[::1]:8001")]
    public void Endpoints_Are_Neither_Path_Nor_Name(string spec)
    {
        TransportSpec.LooksLikePath(spec).Should().BeFalse();
        TransportSpec.LooksLikeName(spec).Should().BeFalse();
    }

    [Fact]
    public void Device_Without_Baud_Leaves_It_Unset()
    {
        TransportSpec.TryParse("/dev/ttyUSB0", out var spec, out _).Should().BeTrue();
        spec!.IsSerial.Should().BeTrue();
        spec.Device.Should().Be("/dev/ttyUSB0");
        spec.Baud.Should().BeNull();
    }

    [Fact]
    public void Device_With_Baud_Suffix_Is_Split()
    {
        TransportSpec.TryParse("/dev/ttyUSB0:57600", out var spec, out _).Should().BeTrue();
        spec!.Device.Should().Be("/dev/ttyUSB0");
        spec.Baud.Should().Be(57600);
    }

    [Fact]
    public void Colon_Before_The_Last_Slash_Is_Part_Of_The_Path()
    {
        // Only a trailing ":<digits>" after the last '/' counts as a baud
        // suffix, so a by-id path is not mangled.
        TransportSpec.TryParse("/dev/serial/by-id/usb-FTDI_A50285BI-if00-port0", out var spec, out _).Should().BeTrue();
        spec!.Device.Should().Be("/dev/serial/by-id/usb-FTDI_A50285BI-if00-port0");
        spec.Baud.Should().BeNull();
    }

    [Fact]
    public void Zero_Baud_Is_Rejected()
    {
        TransportSpec.TryParse("/dev/ttyUSB0:0", out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("localhost:8001", "localhost", 8001)]
    [InlineData("10.45.0.66:8001", "10.45.0.66", 8001)]
    // The brackets disambiguate an IPv6 literal's own colons and are stripped,
    // because Socket wants the bare address.
    [InlineData("[::1]:8001", "::1", 8001)]
    [InlineData("[fe80::1]:65535", "fe80::1", 65535)]
    public void Endpoints_Are_Split(string spec, string host, int port)
    {
        TransportSpec.TryParse(spec, out var parsed, out _).Should().BeTrue();
        parsed!.IsSerial.Should().BeFalse();
        parsed.Host.Should().Be(host);
        parsed.TcpPort.Should().Be(port);
    }

    [Theory]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("localhost:notanumber")]
    [InlineData("localhost:-1")]
    [InlineData(":8001")]
    [InlineData("[::1]8001")]
    [InlineData("[::1]")]
    [InlineData("radio")]
    [InlineData("")]
    public void Malformed_Specs_Are_Rejected(string spec)
    {
        TransportSpec.TryParse(spec, out var parsed, out var error).Should().BeFalse();
        parsed.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ToString_Round_Trips_What_Was_Written()
    {
        TransportSpec.TryParse("/dev/ttyUSB0:57600", out var serial, out _);
        serial!.ToString().Should().Be("/dev/ttyUSB0:57600");

        TransportSpec.TryParse("/dev/ttyUSB0", out var bare, out _);
        bare!.ToString().Should().Be("/dev/ttyUSB0");

        TransportSpec.TryParse("10.45.0.66:8001", out var tcp, out _);
        tcp!.ToString().Should().Be("10.45.0.66:8001");

        TransportSpec.TryParse("[::1]:8001", out var v6, out _);
        v6!.ToString().Should().Be("[::1]:8001");
    }
}
