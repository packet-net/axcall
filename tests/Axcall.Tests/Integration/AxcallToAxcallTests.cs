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
/// simulator. Both ends are our own <see cref="SessionRelay"/> — the connector
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

        var listenTask = Task.Run(() => listenerRelay.ListenAndRelayAsync(cts.Token), cts.Token);

        // Give the listener's inbound pump a moment to come up before we dial.
        await Task.Delay(500, cts.Token);

        var connectTask = Task.Run(() => connectorRelay.ConnectAndRelayAsync(ListenerCall, cts.Token), cts.Token);

        var exchanged = await WaitUntil(
            () => listenerOut.Snapshot().Contains("hello from connector", StringComparison.Ordinal)
               && connectorOut.Snapshot().Contains("roger from listener", StringComparison.Ordinal),
            TimeSpan.FromSeconds(70), cts.Token);

        if (!exchanged)
        {
            output.WriteLine($"listener stdout: {listenerOut.Snapshot()}");
            output.WriteLine($"connector stdout: {connectorOut.Snapshot()}");
            output.WriteLine("=== netsim logs ===");
            output.WriteLine(await fixture.GetNetsimLogsAsync());
        }

        listenerOut.Snapshot().Should().Contain("hello from connector",
            "the listening axcall instance should receive the connector's I-frame data");
        connectorOut.Snapshot().Should().Contain("roger from listener",
            "the connecting axcall instance should receive the listener's I-frame data");

        // Tear both relays down: cancelling unblocks the scripted readers,
        // each relay then sends DISC and exits.
        await cts.CancelAsync();
        await Task.WhenAll(Swallow(listenTask), Swallow(connectTask));
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

    /// <summary>Thread-safe sink: the relay writes on its pump thread, the test reads on its own.</summary>
    private sealed class CapturingWriter : TextWriter
    {
        private readonly StringBuilder sb = new();
        private readonly Lock gate = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) { lock (gate) sb.Append(value); }
        public override void Write(string? value) { if (value is not null) lock (gate) sb.Append(value); }
        public override void Write(char[] buffer, int index, int count) { lock (gate) sb.Append(buffer, index, count); }

        public string Snapshot() { lock (gate) return sb.ToString(); }
    }

    /// <summary>Yields the scripted lines once, then blocks until cancelled (returning EOF).</summary>
    private sealed class ScriptedReader(IReadOnlyList<string> lines) : TextReader
    {
        private int index;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (index < lines.Count) return lines[index++];
            try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return null;
        }

        public override string? ReadLine() => index < lines.Count ? lines[index++] : null;
    }
}
