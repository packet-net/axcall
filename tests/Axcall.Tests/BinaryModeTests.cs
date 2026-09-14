using System.Text;
using AwesomeAssertions;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// -r, byte transparency. What goes in one end comes out the other unchanged,
/// which is what the kernel version's raw mode did and what line mode, rightly,
/// does not.
/// </summary>
public sealed class BinaryModeTests
{
    private static readonly Callsign Listener = new("AXLSTN", 1);
    private static readonly Callsign Connector = new("AXCONN", 2);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sends <paramref name="payload"/> from the connector in binary mode and
    /// returns exactly what the listener wrote to its stdout.
    /// </summary>
    private static async Task<byte[]> RoundTripAsync(byte[] payload)
    {
        using var cts = new CancellationTokenSource(Budget);
        var (a, b) = LoopbackTransport.CreatePair();

        var received = new MemoryStream();

        // The listener is in binary mode too, so its stdout is the bytes off
        // the link and nothing else. No idle timeout on either end: the
        // connector hangs up when its payload runs out, and that is what ends
        // the listener. An idle timeout here would race the ack delay below.
        //
        // AckDelay zero acknowledges every frame at once. The 3 s default is
        // right on the air and wrong here: with a window of 4 and N1 of 256 it
        // would meter a multi-frame payload at 1 kB per 3 s, so this test would
        // spend half a minute proving something about timers rather than about
        // bytes.
        var linkOptions = new SessionRelayOptions
        {
            Binary = true,
            Silent = true,
            AckDelay = TimeSpan.Zero,
        };

        await using var listenerRelay = new SessionRelay(
            a, Listener, null, null, linkOptions, new CapturingWriter(),
            binaryInput: new BlockingStream(),
            binaryOutput: received);

        await using var connectorRelay = new SessionRelay(
            b, Connector, null, null, linkOptions, new CapturingWriter(),
            binaryInput: new MemoryStream(payload),
            binaryOutput: Stream.Null);

        var listen = Task.Run(() => listenerRelay.ListenAndRelayAsync(cts.Token), CancellationToken.None);
        await Task.Delay(200, cts.Token);
        var connect = Task.Run(() => connectorRelay.ConnectAndRelayAsync(Listener, cts.Token), CancellationToken.None);

        // The connector's input is a MemoryStream, so it hits end of input as
        // soon as the payload is away and hangs up, which ends the listener too.
        await connect.WaitAsync(Budget);
        await listen.WaitAsync(Budget);

        return received.ToArray();
    }

    [Fact]
    public async Task Bare_Cr_Survives()
    {
        // Line mode turns this into LF. That is right for a terminal and wrong
        // for a pipe, and it is the specific corruption -r exists to avoid.
        var payload = Encoding.ASCII.GetBytes("one\rtwo\rthree");
        (await RoundTripAsync(payload)).Should().Equal(payload);
    }

    [Fact]
    public async Task Crlf_Is_Not_Collapsed()
    {
        // Line mode collapses CRLF to a single LF, losing a byte.
        var payload = Encoding.ASCII.GetBytes("a\r\nb\r\n");
        (await RoundTripAsync(payload)).Should().Equal(payload);
    }

    [Fact]
    public async Task No_Cr_Is_Appended()
    {
        // Line mode appends a CR to everything it sends.
        var payload = Encoding.ASCII.GetBytes("no terminator");
        (await RoundTripAsync(payload)).Should().Equal(payload);
    }

    [Fact]
    public async Task Nulls_And_High_Bytes_Survive()
    {
        var payload = new byte[] { 0x00, 0x01, 0x0D, 0x0A, 0x1A, 0x7F, 0x80, 0xFE, 0xFF, 0x00 };
        (await RoundTripAsync(payload)).Should().Equal(payload);
    }

    [Fact]
    public async Task Every_Byte_Value_Survives()
    {
        // The whole 0..255 range, which is the real claim -r makes.
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        (await RoundTripAsync(payload)).Should().Equal(payload);
    }

    [Fact]
    public async Task A_Payload_Larger_Than_One_Frame_Survives_Intact()
    {
        // Longer than the default N1 of 256, so it crosses several I-frames and
        // has to reassemble in order with nothing added at the seams.
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        (await RoundTripAsync(payload)).Should().Equal(payload);
    }

    [Fact]
    public async Task Invalid_Utf8_Survives()
    {
        // A lone continuation byte and a truncated sequence. Anything that went
        // through a TextWriter would come back as replacement characters.
        var payload = new byte[] { 0x48, 0x80, 0xC3, 0x49, 0xE2, 0x82 };
        (await RoundTripAsync(payload)).Should().Equal(payload);
    }

    /// <summary>A stream that never yields and never ends, for the end that only listens.</summary>
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
