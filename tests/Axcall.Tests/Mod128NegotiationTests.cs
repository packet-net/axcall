using AwesomeAssertions;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// What each end reports about a modulo-128 link, and when.
/// </summary>
/// <remarks>
/// A dial negotiates before the connection on either modulus (section 6.3.2: "Parameter
/// negotiation occurs only before the connection is made"), so both ends print the agreed
/// link rather than their own offer. k and N1 are notifications of receive capacity
/// (section 4.3.3.7), so the agreed value is the lesser of the two: a wide window asked for
/// at one end only comes straight back down to what the other end advertised. The second
/// "negotiated with" line is for the case where the parameters settle after the link is up
/// instead, which is what `--no-xid` leaves an extended dial doing.
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

        await WaitForText(connectorStatus, "connected to AXLSTN-1", cts.Token);
        await WaitForText(listenerStatus, "connection from AXCONN-2", cts.Token);
        await Task.Delay(1000, cts.Token);   // give a late second line every chance to appear

        var caller = connectorStatus.Snapshot();
        var answerer = listenerStatus.Snapshot();

        // Settled before either end connected: the caller's 100 came down to the 7 the
        // answerer advertised, the answerer's 256 paclen down to the caller's 128, and
        // both ends say the same thing first time.
        caller.Should().Contain("connected to AXLSTN-1 (mod-128, window 7, paclen 128, SREJ on)");
        answerer.Should().Contain("connection from AXCONN-2 (mod-128, window 7, paclen 128, SREJ on)");

        caller.Should().NotContain("negotiated with", "nothing settled after the connect");
        answerer.Should().NotContain("negotiated with");

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

    [Fact]
    public async Task With_No_Xid_A_Mod128_Dial_Reports_The_Late_Negotiation()
    {
        // --no-xid skips the exchange that would run before the SABME, so the link comes
        // up on this end's offer and the library's post-UA negotiation settles it a round
        // trip later. That is the case the second status line exists for.
        using var cts = new CancellationTokenSource(Budget);
        var (a, b) = LoopbackTransport.CreatePair(TimeSpan.FromMilliseconds(250));

        var connectorStatus = new CapturingWriter();

        var listenerRelay = new SessionRelay(
            a, Listener, new ScriptedReader([]), new CapturingWriter(),
            new SessionRelayOptions { Window = 7 }, new CapturingWriter());
        var connectorRelay = new SessionRelay(
            b, Connector, new ScriptedReader([]), new CapturingWriter(),
            new SessionRelayOptions { Mod128 = true, Window = 100, Paclen = 128, NoXid = true }, connectorStatus);

        var listen = Task.Run(() => listenerRelay.ListenAndRelayAsync(cts.Token), CancellationToken.None);
        await Task.Delay(200, cts.Token);
        var connect = Task.Run(() => connectorRelay.ConnectAndRelayAsync(Listener, cts.Token), CancellationToken.None);

        await WaitForText(connectorStatus, "negotiated with", cts.Token);
        var caller = connectorStatus.Snapshot();

        // The connect line is this end's offer, because nothing had been agreed yet. With
        // SREJ in effect the window is held to half the modulus, so 100 runs at 64.
        caller.Should().Contain("connected to AXLSTN-1 (mod-128, window 100 (64 in effect), paclen 128, SREJ on)");
        caller.Should().Contain("negotiated with AXLSTN-1 (mod-128, window 7, paclen 128, SREJ on)");

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
