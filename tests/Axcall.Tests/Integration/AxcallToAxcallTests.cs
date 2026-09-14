using System.Text;
using AwesomeAssertions;
using Packet.Core;
using Packet.Kiss;
using Xunit;
using Xunit.Abstractions;

namespace Axcall.Tests.Integration;

/// <summary>
/// End-to-end test where one axcall instance listens and another connects to
/// it, exchanging I-frame data both ways across the net-sim AFSK1200 RF
/// simulator. Both ends are our own <see cref="SessionRelay"/> - the connector
/// attaches to net-sim node a (KISS 8100) and the listener to node b (8101);
/// the simulator bridges them over the a&lt;-&gt;b link.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AxcallToAxcallTests
{
    private static readonly Callsign ListenerCall = new("AXLSTN", 1);
    private static readonly Callsign ConnectorCall = new("AXCONN", 2);

    private readonly InteropFixture fixture;
    private readonly ITestOutputHelper output;

    public AxcallToAxcallTests(InteropFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact]
    public async Task Two_Axcall_Instances_Exchange_Data_Over_Netsim()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        await using var kissListener = await KissTcpClient.ConnectAsync(InteropFixture.NetsimHost, fixture.NetsimKissPortB, cts.Token);
        await using var kissConnector = await KissTcpClient.ConnectAsync(InteropFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);

        var listenerOut = new CapturingWriter();
        var connectorOut = new CapturingWriter();

        // Each side sends one line as soon as its link comes up, then blocks
        // (so the session stays open long enough for both directions to land).
        await using var listenerRelay = new SessionRelay(
            kissListener, ListenerCall, new ScriptedReader(["roger from listener"]), listenerOut);
        await using var connectorRelay = new SessionRelay(
            kissConnector, ConnectorCall, new ScriptedReader(["hello from connector"]), connectorOut);

        // Both relays report their link on stderr; capture it so the test can
        // check what the live sessions settled on.
        var stderr = new CapturingWriter();
        var originalErr = Console.Error;
        Console.SetError(stderr);
        try
        {
            var listenTask = Task.Run(() => listenerRelay.ListenAndRelayAsync(cts.Token), cts.Token);

            // Give the listener's inbound pump a moment to come up before we dial.
            await Task.Delay(500, cts.Token);

            var connectTask = Task.Run(() => connectorRelay.ConnectAndRelayAsync(ListenerCall, cts.Token), cts.Token);

            // The receive path translates the CR line terminator to LF, so each
            // received line ends in "\n" - asserting that guards the CR->LF fix.
            var exchanged = await WaitUntil(
                () => listenerOut.Snapshot().Contains("hello from connector\n", StringComparison.Ordinal)
                   && connectorOut.Snapshot().Contains("roger from listener\n", StringComparison.Ordinal),
                TimeSpan.FromSeconds(70), cts.Token);

            if (!exchanged)
            {
                output.WriteLine($"listener stdout: {listenerOut.Snapshot()}");
                output.WriteLine($"connector stdout: {connectorOut.Snapshot()}");
                output.WriteLine("=== netsim logs ===");
                output.WriteLine(await fixture.GetNetsimLogsAsync());
            }

            listenerOut.Snapshot().Should().Contain("hello from connector\n",
                "the listening axcall instance should receive the connector's data with the CR rendered as a line break");
            connectorOut.Snapshot().Should().Contain("roger from listener\n",
                "the connecting axcall instance should receive the listener's data with the CR rendered as a line break");

            // Each end reports the parameters its live session settled on. With no
            // flags given, two axcall instances keep the library's window of 4
            // (had the SDL's k := 8 verb fired on connect, XID would have settled
            // on 7) and the pre-SABM XID negotiates SREJ on for both.
            var status = stderr.Snapshot();
            output.WriteLine($"stderr: {status}");
            status.Should().Contain("axcall: connected to AXLSTN-1 (mod-8, window 4, paclen 256, SREJ on)");
            status.Should().Contain("axcall: connection from AXCONN-2 (mod-8, window 4, paclen 256, SREJ on)");

            // Tear both relays down: cancelling unblocks the scripted readers,
            // each relay then sends DISC and exits.
            await cts.CancelAsync();
            await Task.WhenAll(Swallow(listenTask), Swallow(connectTask));
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return true;
            try { await Task.Delay(100, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return condition();
    }

    private static async Task Swallow(Task t)
    {
        try { await t; } catch { /* shutdown races are expected on cancel */ }
    }
}
