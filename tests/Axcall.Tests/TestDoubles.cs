using System.Text;
using System.Threading.Channels;
using Packet.Ax25.Transport;

namespace Axcall.Tests;

/// <summary>
/// Two transports wired back to back in memory: what one sends, the other
/// receives, immediately and without loss.
/// </summary>
/// <remarks>
/// Lets two <see cref="SessionRelay"/> instances hold a real AX.25 session in
/// one process, with the genuine state machine on both ends but no radio, no
/// container and no channel timing. That is what makes the terminal behaviours
/// (-T, -W, -S, -d) testable as unit tests: each is about what axcall does with
/// a session, not about what goes on the air.
/// </remarks>
internal sealed class LoopbackTransport : IAx25Transport
{
    private readonly Channel<Ax25InboundFrame> inbound =
        Channel.CreateUnbounded<Ax25InboundFrame>(new UnboundedChannelOptions { SingleReader = true });

    private LoopbackTransport? peer;

    private LoopbackTransport() { }

    /// <summary>A connected pair. Each end's sends arrive at the other end's receive stream.</summary>
    public static (LoopbackTransport A, LoopbackTransport B) CreatePair()
    {
        var a = new LoopbackTransport();
        var b = new LoopbackTransport();
        a.peer = b;
        b.peer = a;
        return (a, b);
    }

    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        // Copied, because the caller is free to reuse its buffer once this
        // returns and the frame is read later on the peer's pump thread.
        peer?.inbound.Writer.TryWrite(new Ax25InboundFrame(ax25.ToArray(), 0, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default)
        => inbound.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A <see cref="TextWriter"/> that keeps everything written to it.</summary>
internal sealed class CapturingWriter : TextWriter
{
    private readonly StringBuilder sb = new();
    private readonly Lock gate = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) { lock (gate) sb.Append(value); }
    public override void Write(string? value) { if (value is not null) lock (gate) sb.Append(value); }
    public override void Write(char[] buffer, int index, int count) { lock (gate) sb.Append(buffer, index, count); }

    public string Snapshot() { lock (gate) return sb.ToString(); }
}

/// <summary>
/// Feeds scripted lines, then either blocks until cancelled or reports end of
/// input. Blocking models a user sitting at a terminal; ending models a pipe
/// that has run dry, which is the case -W exists for.
/// </summary>
internal sealed class ScriptedReader(IReadOnlyList<string> lines, bool blockAtEnd = true) : TextReader
{
    private int index;

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (index < lines.Count) return lines[index++];
        if (blockAtEnd)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        return null;
    }

    public override string? ReadLine() => index < lines.Count ? lines[index++] : null;
}
