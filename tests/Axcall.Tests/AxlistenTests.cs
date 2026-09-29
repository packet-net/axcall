using System.Threading.Channels;
using AwesomeAssertions;
using Axlisten;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Xunit;
using static Axlisten.Program;

namespace Axcall.Tests;

/// <summary>
/// axlisten's command line, its filter, and the loop that listens on every
/// port and outlives the TNCs going away.
/// </summary>
[Collection(ConfigFilesCollection.Name)]
public sealed class AxlistenTests
{
    private sealed class PortsScope : IDisposable
    {
        private readonly string? previous;
        private readonly string path;

        public PortsScope(string? ports)
        {
            path = Path.Combine(Path.GetTempPath(), $"axlisten-ports-{Guid.NewGuid():N}");
            if (ports is not null) File.WriteAllText(path, ports);
            previous = Environment.GetEnvironmentVariable(PortsFile.PathEnvVar);
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PortsFile.PathEnvVar, previous);
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private const string TwoPorts =
        "radio  M0LTE-7  /dev/ttyUSB0:57600  256  4  144.800\n"
        + "node   -        10.45.0.66:8001     -    -  LinBPQ\n"
        + "again  M0LTE-8  /dev/ttyUSB0        -    -  same TNC, another call\n";

    // --- command line --------------------------------------------------------

    [Fact]
    public void With_No_Port_Every_Configured_Port_Is_Heard_Once()
    {
        using var _ = new PortsScope(TwoPorts);
        var parsed = ParseArgs([])!;

        parsed.Ports.Select(p => p.Name).Should().Equal("radio", "node");
        parsed.Ports.Should().OnlyContain(p => !p.Required);
    }

    [Fact]
    public void With_No_Port_And_None_Configured_It_Says_So()
    {
        using var _ = new PortsScope(null);
        ParseArgs([]).Should().BeNull();
    }

    [Fact]
    public void Named_Ports_Are_Required_And_Need_No_Callsign()
    {
        using var _ = new PortsScope(TwoPorts);
        var parsed = ParseArgs(["node", "--serial", "/dev/ttyACM0", "--tcp", "127.0.0.1:8001"])!;

        parsed.Ports.Select(p => p.Name).Should().Equal("node", "/dev/ttyACM0", "127.0.0.1:8001");
        parsed.Ports.Should().OnlyContain(p => p.Required);
        parsed.Mode.Should().Be(OutputMode.Friendly);
    }

    [Fact]
    public void An_Unknown_Port_Name_Is_An_Error()
    {
        using var _ = new PortsScope(TwoPorts);
        ParseArgs(["nosuch"]).Should().BeNull();
    }

    [Fact]
    public void Modern_Options()
    {
        var parsed = ParseArgs(["--tcp", "h:1", "--json", "-x", "--utc", "--color", "never", "-q"])!;
        parsed.Mode.Should().Be(OutputMode.Json);
        parsed.Hex.Should().BeTrue();
        parsed.Time.Should().Be(TimeStyle.Utc);
        parsed.Color.Should().Be(ColorChoice.Never);
        parsed.Quiet.Should().BeTrue();
    }

    [Fact]
    public void Colour_Choices_Are_Checked()
    {
        ParseArgs(["--tcp", "h:1", "--color", "sometimes"]).Should().BeNull();
    }

    [Fact]
    public void Classic_Letters_Are_Refused_Without_Classic()
    {
        ParseArgs(["--tcp", "h:1", "-r"]).Should().BeNull();
    }

    [Fact]
    public void Classic_Takes_The_Old_Options_Clustered_As_Getopt_Did()
    {
        using var _ = new PortsScope(TwoPorts);
        var parsed = ParseArgs(["--classic", "-a8ch", "-tt", "-pradio"])!;

        parsed.Mode.Should().Be(OutputMode.Classic);
        parsed.Dump.Should().Be(ClassicDump.Hex);
        parsed.EightBit.Should().BeTrue();
        parsed.Color.Should().Be(ColorChoice.Always);
        parsed.Timestamps.Should().Be(2);
        parsed.Ports.Select(p => p.Name).Should().Equal("radio");
    }

    [Fact]
    public void Classic_P_Takes_Its_Value_From_The_Next_Argument_Too()
    {
        using var _ = new PortsScope(TwoPorts);
        var parsed = ParseArgs(["--classic", "-r", "-p", "node", "-t"])!;
        parsed.Dump.Should().Be(ClassicDump.Readable);
        parsed.Timestamps.Should().Be(1);
        parsed.Ports.Select(p => p.Name).Should().Equal("node");
    }

    [Fact]
    public void Classic_Refuses_The_Modern_Output_Options()
    {
        ParseArgs(["--classic", "--tcp", "h:1", "--json"]).Should().BeNull();
    }

    [Fact]
    public void Classic_Has_Five_Timestamp_Styles_And_No_More()
    {
        ParseArgs(["--classic", "--tcp", "h:1", "-tttttt"]).Should().BeNull();
    }

    [Fact]
    public void Classic_Output_Is_Chosen_By_The_Flag()
    {
        var parsed = ParseArgs(["--classic", "--tcp", "h:1"])!;
        CreateFormatter(parsed, outputRedirected: true).Should().BeOfType<ClassicFormatter>();
        CreateFormatter(ParseArgs(["--tcp", "h:1"])!, outputRedirected: true).Should().BeOfType<FriendlyFormatter>();
    }

    // --- filter --------------------------------------------------------------

    private static MonitoredFrame Heard(Ax25Frame frame) => new("radio", 0, DateTimeOffset.UtcNow, frame.ToBytes());

    [Fact]
    public void A_Callsign_Without_An_Ssid_Matches_Every_Ssid()
    {
        var filter = new CallFilter();
        filter.TryAdd("gb7rdg").Should().BeTrue();

        filter.Matches(Heard(Ax25Frame.Sabm(new Callsign("GB7RDG", 3), new Callsign("M0LTE", 7)))).Should().BeTrue();
        filter.Matches(Heard(Ax25Frame.Sabm(new Callsign("GB7CIP", 0), new Callsign("M0LTE", 7)))).Should().BeFalse();
    }

    [Fact]
    public void A_Callsign_With_An_Ssid_Matches_Only_That_One()
    {
        var filter = new CallFilter();
        filter.TryAdd("M0LTE-7").Should().BeTrue();

        filter.Matches(Heard(Ax25Frame.Sabm(new Callsign("GB7RDG", 0), new Callsign("M0LTE", 7)))).Should().BeTrue();
        filter.Matches(Heard(Ax25Frame.Sabm(new Callsign("GB7RDG", 0), new Callsign("M0LTE", 8)))).Should().BeFalse();
    }

    [Fact]
    public void A_Digipeater_Counts()
    {
        var filter = new CallFilter();
        filter.TryAdd("WIDE1");
        var frame = Ax25Frame.Ui(new Callsign("APRS", 0), new Callsign("M0LTE", 7), "x"u8, digipeaters: [new Callsign("WIDE1", 1)]);
        filter.Matches(Heard(frame)).Should().BeTrue();
    }

    // --- the loop ------------------------------------------------------------

    private sealed class FakeTnc : IAx25Transport, IAsyncDisposable
    {
        private readonly Channel<Ax25InboundFrame> inbound = Channel.CreateUnbounded<Ax25InboundFrame>();

        public bool Disposed { get; private set; }

        public void Hear(Ax25Frame frame, byte port = 0)
            => inbound.Writer.TryWrite(new Ax25InboundFrame(frame.ToBytes(), port, DateTimeOffset.UtcNow));

        public void Fail(Exception ex) => inbound.Writer.TryComplete(ex);

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("axlisten must never transmit");

        public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default)
            => inbound.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A StringWriter safe to read while the monitor writes to it.</summary>
    private sealed class Sink : StringWriter
    {
        private readonly Lock gate = new();
        public override void Write(char value) { lock (gate) base.Write(value); }
        public override void Write(string? value) { lock (gate) base.Write(value); }
        public override Task WriteAsync(string? value) { Write(value); return Task.CompletedTask; }
        public override void WriteLine(string? value) { lock (gate) base.WriteLine(value); }
        public string Text { get { lock (gate) return ToString(); } }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    private static readonly IFrameFormatter Plain = new FriendlyFormatter(false, TimeStyle.None, false, 5);

    [Fact]
    public async Task Frames_From_Every_Port_Are_Written()
    {
        var a = new FakeTnc();
        var b = new FakeTnc();
        var output = new Sink();
        var log = new Sink();
        using var cts = new CancellationTokenSource();

        var monitor = new ChannelMonitor(
            [new MonitorPort("vhf", "a", _ => Task.FromResult<IAx25Transport>(a)),
             new MonitorPort("uhf", "b", _ => Task.FromResult<IAx25Transport>(b))],
            Plain, new CallFilter(), output, log, quiet: false);
        var run = monitor.RunAsync(cts.Token);

        a.Hear(Ax25Frame.Sabm(new Callsign("GB7RDG", 0), new Callsign("M0LTE", 7)));
        b.Hear(Ax25Frame.Ua(new Callsign("M0LTE", 7), new Callsign("GB7RDG", 0), finalBit: true), port: 2);

        await Eventually(() => output.Text.Contains("SABM") && output.Text.Contains("UA"));
        // The two ports deliver concurrently, and the port column widens when
        // uhf[2] is first seen, so the padding depends on which came first.
        output.Text.Should().MatchRegex(@"vhf +M0LTE-7>GB7RDG SABM").And.MatchRegex(@"uhf\[2\] +GB7RDG>M0LTE-7 UA");
        log.Text.Should().Contain("listening on vhf (a), uhf (b)");

        await cts.CancelAsync();
        (await run).Should().Be(0);
        a.Disposed.Should().BeTrue();
        b.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task A_Named_Port_That_Will_Not_Open_Is_Fatal()
    {
        var log = new Sink();
        var monitor = new ChannelMonitor(
            [new MonitorPort("radio", "/dev/ttyUSB0:57600", _ => throw new IOException("no such device"))],
            Plain, new CallFilter(), new Sink(), log, quiet: true);

        (await monitor.RunAsync(CancellationToken.None)).Should().Be(3);
        log.Text.Should().Contain("radio: failed to open /dev/ttyUSB0:57600: no such device");
    }

    [Fact]
    public async Task A_Configured_Port_That_Will_Not_Open_Is_Skipped()
    {
        var good = new FakeTnc();
        var log = new Sink();
        using var cts = new CancellationTokenSource();
        var monitor = new ChannelMonitor(
            [new MonitorPort("busy", "/dev/ttyUSB0", _ => throw new IOException("in use"), Required: false),
             new MonitorPort("node", "h:1", _ => Task.FromResult<IAx25Transport>(good), Required: false)],
            Plain, new CallFilter(), new Sink(), log, quiet: false);

        var run = monitor.RunAsync(cts.Token);
        await Eventually(() => log.Text.Contains("listening on"));
        log.Text.Should().Contain("busy: skipped").And.Contain("listening on node (h:1)");

        await cts.CancelAsync();
        (await run).Should().Be(0);
    }

    [Fact]
    public async Task No_Configured_Port_Opening_Is_Fatal()
    {
        var monitor = new ChannelMonitor(
            [new MonitorPort("busy", "x", _ => throw new IOException("in use"), Required: false)],
            Plain, new CallFilter(), new Sink(), new Sink(), quiet: true);
        (await monitor.RunAsync(CancellationToken.None)).Should().Be(3);
    }

    [Fact]
    public async Task A_Port_That_Fails_Is_Reopened()
    {
        var first = new FakeTnc();
        var second = new FakeTnc();
        int opens = 0;
        var output = new Sink();
        var log = new Sink();
        using var cts = new CancellationTokenSource();

        var monitor = new ChannelMonitor(
            [new MonitorPort("radio", "h:1", _ =>
            {
                opens++;
                return opens switch
                {
                    1 => Task.FromResult<IAx25Transport>(first),
                    2 => throw new IOException("connection refused"),
                    _ => Task.FromResult<IAx25Transport>(second),
                };
            })],
            Plain, new CallFilter(), output, log, quiet: true, firstRetry: TimeSpan.FromMilliseconds(5));
        var run = monitor.RunAsync(cts.Token);

        first.Fail(new IOException("connection reset"));
        await Eventually(() => log.Text.Contains("reopened"));
        log.Text.Should().Contain("radio: lost h:1: connection reset; retrying");
        first.Disposed.Should().BeTrue();

        second.Hear(Ax25Frame.Disc(new Callsign("GB7RDG", 0), new Callsign("M0LTE", 7)));
        await Eventually(() => output.Text.Contains("DISC"));

        await cts.CancelAsync();
        (await run).Should().Be(0);
    }

    [Fact]
    public async Task The_Filter_Applies()
    {
        var tnc = new FakeTnc();
        var output = new Sink();
        var filter = new CallFilter();
        filter.TryAdd("GB7CIP");
        using var cts = new CancellationTokenSource();

        var run = new ChannelMonitor(
            [new MonitorPort("radio", "h:1", _ => Task.FromResult<IAx25Transport>(tnc))],
            Plain, filter, output, new Sink(), quiet: true).RunAsync(cts.Token);

        tnc.Hear(Ax25Frame.Sabm(new Callsign("GB7RDG", 0), new Callsign("M0LTE", 7)));
        tnc.Hear(Ax25Frame.Sabm(new Callsign("GB7CIP", 0), new Callsign("M0LTE", 7)));
        await Eventually(() => output.Text.Contains("GB7CIP"));
        output.Text.Should().NotContain("GB7RDG");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_Closed_Pipe_Stops_The_Monitor_Quietly()
    {
        var tnc = new FakeTnc();
        var monitor = new ChannelMonitor(
            [new MonitorPort("radio", "h:1", _ => Task.FromResult<IAx25Transport>(tnc))],
            Plain, new CallFilter(), new BrokenPipe(), new Sink(), quiet: true);
        var run = monitor.RunAsync(CancellationToken.None);

        tnc.Hear(Ax25Frame.Sabm(new Callsign("GB7RDG", 0), new Callsign("M0LTE", 7)));
        (await run.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(0);
        tnc.Disposed.Should().BeTrue();
    }

    private sealed class BrokenPipe : StringWriter
    {
        public override Task WriteAsync(string? value) => throw new IOException("Broken pipe");
    }
}
