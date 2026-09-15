using AwesomeAssertions;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// What each end reports about a modulo-128 link, and when.
/// </summary>
/// <remarks>
/// A modulo-8 dial negotiates before the SABM, so the connect line is already the
/// truth. A modulo-128 dial sends the SABME first and the XID after it, and the
/// answering end has sent its UA before the caller's XID command arrives - so the
/// first line either end can print is its own offer, not the agreed values. k and
/// N1 are notifications of receive capacity (section 4.3.3.7), so the agreed value
/// is the lesser of the two offers: a wide window asked for at one end only is
/// negotiated straight back down to what the other end advertised.
/// </remarks>
public sealed class Mod128NegotiationTests
{
    private static readonly Callsign Listener = new("AXLSTN", 1);
    private static readonly Callsign Connector = new("AXCONN", 2);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Both_Ends_Report_The_Negotiated_Window_And_Paclen()
    {
        using var cts = new CancellationTokenSource(Budget);

        // A quarter-second each way, so the XID exchange that follows the SABME is
        // still in flight when the caller is connected - as it is on a radio, and as
        // an instant loopback would hide.
        var (a, b) = LoopbackTransport.CreatePair(TimeSpan.FromMilliseconds(250));

        var listenerStatus = new CapturingWriter();
        var connectorStatus = new CapturingWriter();

        // The answering end offers a window of 7 and the library's 256-byte paclen.
        var listenerRelay = new SessionRelay(
            a, Listener, new ScriptedReader([]), new CapturingWriter(),
            new SessionRelayOptions { Window = 7 }, listenerStatus);

        // The caller dials mod-128 asking for a window of 100 and a 128-byte paclen.
        var connectorRelay = new SessionRelay(
            b, Connector, new ScriptedReader([]), new CapturingWriter(),
            new SessionRelayOptions { Mod128 = true, Window = 100, Paclen = 128 }, connectorStatus);

        var listen = Task.Run(() => listenerRelay.ListenAndRelayAsync(cts.Token), CancellationToken.None);
        await Task.Delay(200, cts.Token);
        var connect = Task.Run(() => connectorRelay.ConnectAndRelayAsync(Listener, cts.Token), CancellationToken.None);

        await WaitForText(connectorStatus, "negotiated with", cts.Token);
        await WaitForText(listenerStatus, "negotiated with", cts.Token);

        var caller = connectorStatus.Snapshot();
        var answerer = listenerStatus.Snapshot();

        // What each end offered, reported the moment the link came up. A v2.2 link
        // selects selective reject at establishment, and with SREJ in effect the window
        // is held to half the modulus, so the caller's 100 runs at 64 and says so.
        caller.Should().Contain("connected to AXLSTN-1 (mod-128, window 100 (64 in effect), paclen 128, SREJ on)");
        answerer.Should().Contain("connection from AXCONN-2 (mod-128, window 7, paclen 256, SREJ on)");

        // What they agreed a round trip later: the lesser of each.
        caller.Should().Contain("negotiated with AXLSTN-1 (mod-128, window 7, paclen 128, SREJ on)");
        answerer.Should().Contain("negotiated with AXCONN-2 (mod-128, window 7, paclen 128, SREJ on)");

        await cts.CancelAsync();
        await Swallow(connect);
        await Swallow(listen);
    }

    [Fact]
    public async Task A_Mod8_Link_That_Settles_On_Its_Offer_Says_Nothing_Further()
    {
        // The negotiated line is only printed when the negotiation actually changes
        // something: a modulo-8 dial to a peer that agrees must not gain a second
        // line saying the same thing twice.
        using var cts = new CancellationTokenSource(Budget);
        var (a, b) = LoopbackTransport.CreatePair();

        var connectorStatus = new CapturingWriter();

        var listenerRelay = new SessionRelay(
            a, Listener, new ScriptedReader([]), new CapturingWriter(),
            new SessionRelayOptions(), new CapturingWriter());
        var connectorRelay = new SessionRelay(
            b, Connector, new ScriptedReader([]), new CapturingWriter(),
            new SessionRelayOptions(), connectorStatus);

        var listen = Task.Run(() => listenerRelay.ListenAndRelayAsync(cts.Token), CancellationToken.None);
        await Task.Delay(200, cts.Token);
        var connect = Task.Run(() => connectorRelay.ConnectAndRelayAsync(Listener, cts.Token), CancellationToken.None);

        await WaitForText(connectorStatus, "connected to AXLSTN-1", cts.Token);
        await Task.Delay(1500, cts.Token);

        connectorStatus.Snapshot().Should().NotContain("negotiated with");

        await cts.CancelAsync();
        await Swallow(connect);
        await Swallow(listen);
    }

    private static async Task WaitForText(CapturingWriter writer, string needle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (writer.Snapshot().Contains(needle, StringComparison.Ordinal))
            {
                return;
            }

            try { await Task.Delay(50, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        throw new TimeoutException($"status never contained '{needle}'; it said:\n{writer.Snapshot()}");
    }

    private static async Task Swallow(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
