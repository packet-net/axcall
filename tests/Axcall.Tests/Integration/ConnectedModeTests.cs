using System.Text;
using AwesomeAssertions;
using Packet.Core;
using Packet.Kiss;
using Xunit;
using Xunit.Abstractions;

namespace Axcall.Tests.Integration;

[Collection(InteropCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConnectedModeTests
{
    private static readonly Callsign OurCall = new("AXTEST", 0);
    private static readonly Callsign LinbpqCall = new("PN0TST", 0);

    private readonly InteropFixture fixture;
    private readonly ITestOutputHelper output;

    public ConnectedModeTests(InteropFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact]
    public async Task Connect_To_Linbpq_Receives_Welcome_Banner()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var kiss = await KissTcpClient.ConnectAsync(InteropFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);
        await using var relay = new SessionRelay(kiss, OurCall);

        var received = new StringBuilder();
        var bannerReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var originalOut = Console.Out;
        var originalIn = Console.In;
        try
        {
            Console.SetOut(new CallbackTextWriter(text =>
            {
                received.Append(text);
                if (received.ToString().Contains("***"))
                    bannerReceived.TrySetResult(true);
            }));

            // stdin must block until the banner arrives — if it returns EOF
            // immediately, the relay sends DISC before the I-frame arrives
            Console.SetIn(new BlockingReader(cts.Token));

            var relayTask = Task.Run(() => relay.ConnectAndRelayAsync(LinbpqCall, cts.Token), cts.Token);

            var completed = await Task.WhenAny(bannerReceived.Task, Task.Delay(TimeSpan.FromSeconds(50), cts.Token));

            if (!bannerReceived.Task.IsCompleted)
            {
                await DumpLogsAsync();
            }

            var text = received.ToString();
            text.Should().Contain("PN0TST", "LinBPQ should send its welcome banner after connect");

            // Cancel to unblock stdin and let the relay exit
            await cts.CancelAsync();
            try { await relayTask; } catch (OperationCanceledException) { /* expected */ }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task SessionRelay_Reports_Clean_Disconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var kiss = await KissTcpClient.ConnectAsync(InteropFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);
        await using var relay = new SessionRelay(kiss, OurCall);

        var originalOut = Console.Out;
        var originalIn = Console.In;
        var stderr = new StringWriter();
        var originalErr = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(stderr);
            Console.SetIn(new StringReader(""));

            int exitCode;
            try
            {
                exitCode = await relay.ConnectAndRelayAsync(LinbpqCall, cts.Token);
            }
            catch (OperationCanceledException)
            {
                await DumpLogsAsync();
                throw;
            }

            exitCode.Should().Be(0);

            var status = stderr.ToString();
            output.WriteLine($"stderr: {status}");
            status.Should().Contain("connecting");
            status.Should().Contain("connected");
            status.Should().Contain("disconnected");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
            Console.SetError(originalErr);
        }
    }

    private async Task DumpLogsAsync()
    {
        output.WriteLine("=== netsim logs ===");
        output.WriteLine(await fixture.GetNetsimLogsAsync());
        output.WriteLine("=== linbpq logs ===");
        output.WriteLine(await fixture.GetLinbpqLogsAsync());
    }

    private sealed class BlockingReader(CancellationToken ct) : TextReader
    {
        public override string? ReadLine()
        {
            try { ct.WaitHandle.WaitOne(); } catch (ObjectDisposedException) { }
            return null;
        }

        public override Task<string?> ReadLineAsync()
            => ReadLineAsync(CancellationToken.None).AsTask();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
            try { await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return null;
        }
    }

    private sealed class CallbackTextWriter(Action<string> onWrite) : StringWriter(System.Globalization.CultureInfo.InvariantCulture)
    {
        public override void Write(string? value)
        {
            if (value is not null) onWrite(value);
            base.Write(value);
        }

        public override void Write(char value)
        {
            onWrite(value.ToString());
            base.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            onWrite(new string(buffer, index, count));
            base.Write(buffer, index, count);
        }

        public override Task WriteAsync(string? value)
        {
            if (value is not null) onWrite(value);
            return base.WriteAsync(value);
        }
    }
}
