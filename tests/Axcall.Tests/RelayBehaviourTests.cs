using AwesomeAssertions;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// The four terminal behaviours the kernel version had: -T, -W, -S and -d.
/// </summary>
/// <remarks>
/// Each is about what axcall does with a session rather than what goes on the
/// air, so they run over <see cref="LoopbackTransport"/>: two real
/// <see cref="SessionRelay"/> instances with the genuine state machine on both
/// ends, in one process, with no radio and no container.
/// </remarks>
public sealed class RelayBehaviourTests
{
    private static readonly Callsign Listener = new("AXLSTN", 1);
    private static readonly Callsign Connector = new("AXCONN", 2);

    /// <summary>Long enough to absorb a loaded CI runner, short enough to fail fast.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Stands up a connected pair and hands back the connector's relay task.
    /// The listener always blocks on input, so it never hangs up first: every
    /// test here is about what the connector decides to do.
    /// </summary>
    private static async Task<(Task<int> Connect, Task<int> Listen, CapturingWriter Status, CancellationTokenSource Cts)>
        ConnectedPairAsync(
            SessionRelayOptions connectorOptions,
            TextReader connectorInput,
            SessionRelayOptions? listenerOptions = null,
            TextReader? listenerInput = null)
    {
        var cts = new CancellationTokenSource(Budget);
        var (a, b) = LoopbackTransport.CreatePair();

        var status = new CapturingWriter();

        var listenerRelay = new SessionRelay(
            a, Listener, listenerInput ?? new ScriptedReader([]), new CapturingWriter(),
            listenerOptions ?? new SessionRelayOptions(), new CapturingWriter());
        var connectorRelay = new SessionRelay(
            b, Connector, connectorInput, new CapturingWriter(), connectorOptions, status);

        var listen = Task.Run(() => listenerRelay.ListenAndRelayAsync(cts.Token), CancellationToken.None);

        // Let the listener's inbound pump come up before dialling, or the SABM
        // arrives before anything is listening for the UA.
        await Task.Delay(200, cts.Token);

        var connect = Task.Run(() => connectorRelay.ConnectAndRelayAsync(Listener, cts.Token), CancellationToken.None);
        return (connect, listen, status, cts);
    }

    [Fact]
    public async Task Without_Wait_End_Of_Input_Closes_The_Link()
    {
        // The default, and the kernel version's default: stdin runs dry and
        // axcall hangs up without waiting for anything further.
        var (connect, _, _, cts) = await ConnectedPairAsync(
            new SessionRelayOptions(),
            new ScriptedReader(["hello"], blockAtEnd: false));

        using var _cts = cts;
        var code = await connect.WaitAsync(Budget);
        code.Should().Be(0);
    }

    [Fact]
    public async Task With_Wait_End_Of_Input_Leaves_The_Link_Up()
    {
        // -W. "echo q | axcall ax0 db0fhn" must not close before the reply
        // arrives, so end of input alone must not end the session.
        var (connect, _, status, cts) = await ConnectedPairAsync(
            new SessionRelayOptions { WaitForRemoteDisconnect = true },
            new ScriptedReader(["hello"], blockAtEnd: false));

        using var _cts = cts;

        var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(3)));
        finished.Should().NotBe(connect, "-W must keep the link up after end of input");

        status.Snapshot().Should().Contain("waiting for the remote to disconnect");

        // Tidy up: cancelling is the only way out, since the listener never hangs up.
        await cts.CancelAsync();
        await Swallow(connect);
    }

    [Fact]
    public async Task Idle_Timeout_Closes_The_Link_And_Exits_5()
    {
        // -T. Nothing is ever typed and the peer never speaks, so the only
        // thing that can end this session is the idle timer.
        var (connect, _, status, cts) = await ConnectedPairAsync(
            new SessionRelayOptions { IdleTimeout = TimeSpan.FromSeconds(1) },
            new ScriptedReader([]));

        using var _cts = cts;
        var code = await connect.WaitAsync(Budget);

        code.Should().Be(SessionRelay.IdleTimeoutExitCode);
        status.Snapshot().Should().Contain("idle for");
    }

    [Fact]
    public async Task Idle_Timeout_Is_Pushed_Out_By_Traffic()
    {
        // Data in either direction resets the timer, so a session that keeps
        // talking must outlive an idle timeout shorter than the conversation.
        var lines = Enumerable.Range(0, 6).Select(i => $"line {i}").ToArray();
        var (connect, _, _, cts) = await ConnectedPairAsync(
            new SessionRelayOptions { IdleTimeout = TimeSpan.FromSeconds(2) },
            new DrippingReader(lines, TimeSpan.FromMilliseconds(400)));

        using var _cts = cts;

        // 6 lines at 400 ms is ~2.4 s of traffic, comfortably past a 2 s idle
        // timeout that was never reset, and comfortably short of one that is.
        var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(3)));
        finished.Should().NotBe(connect, "traffic should have kept pushing the idle deadline out");

        await cts.CancelAsync();
        await Swallow(connect);
    }

    [Fact]
    public async Task Silent_Suppresses_Status_But_Not_The_Trace()
    {
        // -S silences progress. -d is independent of it, so -d -S gives a bare
        // frame trace with no status lines around it.
        var (connect, _, status, cts) = await ConnectedPairAsync(
            new SessionRelayOptions { Silent = true, TraceFrames = true, IdleTimeout = TimeSpan.FromSeconds(1) },
            new ScriptedReader([]));

        using var _cts = cts;
        await connect.WaitAsync(Budget);

        var text = status.Snapshot();
        text.Should().NotContain("axcall:", "-S suppresses every status line");
        text.Should().Contain("SABM", "-d is independent of -S");
    }

    [Fact]
    public async Task Status_Is_Written_When_Not_Silent()
    {
        var (connect, _, status, cts) = await ConnectedPairAsync(
            new SessionRelayOptions { IdleTimeout = TimeSpan.FromSeconds(1) },
            new ScriptedReader([]));

        using var _cts = cts;
        await connect.WaitAsync(Budget);

        var text = status.Snapshot();
        text.Should().Contain("axcall: connecting to AXLSTN-1");
        text.Should().Contain("axcall: connected to AXLSTN-1 (mod-8");
        text.Should().Contain("axcall: disconnected");
    }

    [Fact]
    public async Task Debug_Traces_Both_Directions()
    {
        var (connect, _, status, cts) = await ConnectedPairAsync(
            new SessionRelayOptions { TraceFrames = true, IdleTimeout = TimeSpan.FromSeconds(1) },
            new ScriptedReader([]));

        using var _cts = cts;
        await connect.WaitAsync(Budget);

        var text = status.Snapshot();
        // The dial we sent and the acceptance that came back.
        text.Should().Contain("> AXCONN-2>AXLSTN-1 SABM");
        text.Should().Contain("< AXLSTN-1>AXCONN-2 UA");
        // And the hang-up the idle timeout drove.
        text.Should().Contain("DISC");
    }

    [Fact]
    public async Task No_Trace_Without_Debug()
    {
        var (connect, _, status, cts) = await ConnectedPairAsync(
            new SessionRelayOptions { IdleTimeout = TimeSpan.FromSeconds(1) },
            new ScriptedReader([]));

        using var _cts = cts;
        await connect.WaitAsync(Budget);

        status.Snapshot().Should().NotContain("SABM");
    }

    [Fact]
    public async Task Wait_Still_Gives_Up_When_Cancelled()
    {
        // -W with no -T waits on a peer that may never hang up, so Ctrl-C has
        // to be able to end it. Without a cancellation arm on that wait, this
        // hangs until the test budget expires.
        var (connect, _, _, cts) = await ConnectedPairAsync(
            new SessionRelayOptions { WaitForRemoteDisconnect = true },
            new ScriptedReader(["hello"], blockAtEnd: false));

        using var _cts = cts;

        await Task.Delay(500);
        connect.IsCompleted.Should().BeFalse("-W should still be waiting at this point");

        await cts.CancelAsync();
        var code = await connect.WaitAsync(TimeSpan.FromSeconds(10));
        code.Should().Be(0);
    }

    private static async Task Swallow(Task t)
    {
        try { await t.ConfigureAwait(false); } catch { /* cancellation races are expected */ }
    }

    /// <summary>Feeds a line every interval, then blocks: a user typing steadily.</summary>
    private sealed class DrippingReader(IReadOnlyList<string> lines, TimeSpan interval) : TextReader
    {
        private int index;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (index < lines.Count) return lines[index++];
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            return null;
        }

        public override string? ReadLine() => index < lines.Count ? lines[index++] : null;
    }
}
