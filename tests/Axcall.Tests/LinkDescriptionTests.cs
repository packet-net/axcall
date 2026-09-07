using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// The "connected to" / "connection from" line reports the parameters the live
/// session settled on, so the user sees what the flags and XID negotiation
/// ended up with.
/// </summary>
public sealed class LinkDescriptionTests
{
    private static Ax25SessionContext FreshContext()
        => new() { Local = new Callsign("M0LTE", 0), Remote = new Callsign("GB7RDG", 0) };

    [Fact]
    public void Library_Defaults_Read_As_Mod8_Window4_Paclen256_Srej_Off()
    {
        SessionRelay.DescribeLink(FreshContext()).Should().Be("mod-8, window 4, paclen 256, SREJ off");
    }

    [Fact]
    public void Negotiated_Values_Are_Reported()
    {
        var ctx = FreshContext();
        ctx.IsExtended = true;
        ctx.K = 32;
        ctx.N1 = 512;
        ctx.SrejEnabled = true;

        SessionRelay.DescribeLink(ctx).Should().Be("mod-128, window 32, paclen 512, SREJ on");
    }

    [Fact]
    public void Window_The_Library_Holds_Down_Shows_The_Enforced_Figure()
    {
        // SREJ on a modulo-8 link: the library holds the window to 4 whatever
        // k was negotiated, so say so rather than print a window that is not used.
        var ctx = FreshContext();
        ctx.K = 7;
        ctx.SrejEnabled = true;

        SessionRelay.DescribeLink(ctx).Should().Be("mod-8, window 7 (4 in effect), paclen 256, SREJ on");
    }
}
