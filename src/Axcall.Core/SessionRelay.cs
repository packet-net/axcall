using System.Text;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Ax25.Transport;

namespace Axcall;

/// <summary>
/// The link parameters the command line chooses. Both are set on the listener
/// explicitly so axcall's on-air behaviour is its own, and does not drift with
/// the defaults of whichever Packet.Ax25 version it happens to be pinned to.
/// </summary>
public sealed record SessionRelayOptions
{
    /// <summary>Default idle-link poll interval (T3), in seconds.</summary>
    public const int DefaultKeepaliveSeconds = 300;

    /// <summary>
    /// Dial with SABME (AX.25 v2.2, modulo 128) instead of SABM. The library
    /// falls back to a v2.0/SABM dial when the peer answers FRMR (LinBPQ) or DM
    /// (XRouter), so the call still goes through; it just costs a round trip
    /// against a modulo-8-only node. Outbound only: an inbound session adopts
    /// whatever the caller's SABM or SABME asks for.
    /// </summary>
    public bool Mod128 { get; init; }

    /// <summary>
    /// T3, the inactive-link timer: with no traffic for this long the link sends
    /// an RR poll to check the peer is still there. Applies to inbound and
    /// outbound sessions. Must be positive: the library arms T3 as a plain timer
    /// with no "never" value, so zero would poll continuously.
    /// </summary>
    public TimeSpan Keepalive { get; init; } = TimeSpan.FromSeconds(DefaultKeepaliveSeconds);

    // The link parameters below are null when the command line did not give
    // them: the library's own default then applies and axcall does not restate
    // it. They apply to inbound and outbound sessions alike (all but NoXid,
    // which gates the outbound dial only).

    /// <summary>
    /// k, the send window (MAXFRAME): I-frames in flight before an ack is
    /// needed. null leaves the library's 4, on either modulus; XID then takes the
    /// lower of what each end offers. With SREJ negotiated the library holds the
    /// window to half the modulus (4 on modulo 8), whatever was asked for.
    /// </summary>
    public int? Window { get; init; }

    /// <summary>
    /// N1, the largest info field per frame in bytes (PACLEN). null leaves the
    /// library's 256. What axcall offers in XID and the most it sends per
    /// frame; XID may lower it to the peer's advertised value.
    /// </summary>
    public int? Paclen { get; init; }

    /// <summary>N2, retries before the link is dropped (RETRY). null leaves the library's 10.</summary>
    public int? Retries { get; init; }

    /// <summary>
    /// The initial T1, the ack timeout (FRACK). null leaves the library's 6 s.
    /// Only the starting point: the library smooths T1 from the measured round
    /// trip once frames flow.
    /// </summary>
    public TimeSpan? Frack { get; init; }

    /// <summary>
    /// T2, the ack delay (RESPTIME): received frames are acknowledged together
    /// once this has elapsed. null leaves the library's 3 s. Zero acknowledges
    /// every frame at once.
    /// </summary>
    public TimeSpan? AckDelay { get; init; }

    /// <summary>
    /// Skip the XID exchange the library runs before the SABM on a modulo-8
    /// dial. Off by default: the dial offers SREJ and its window, and the peer
    /// that answers gets selective retransmit. On, the link is plain go-back-N.
    /// Outbound only; the inbound answerer is untouched.
    /// </summary>
    public bool NoXid { get; init; }

    // The four below are terminal behaviour rather than link parameters: they
    // change what axcall does with the session, not what goes on the air.

    /// <summary>
    /// -T. Close the link when no data has moved in either direction for this
    /// long. null means never. Distinct from <see cref="Keepalive"/>, which is
    /// T3 and does the opposite: T3 polls to keep an idle link up, this drops
    /// one. Only data counts; a T3 keepalive poll does not reset it, matching
    /// the kernel version, where an RR never woke the socket.
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>
    /// -W. On end of input, leave the link up and keep printing until the peer
    /// disconnects, instead of closing straight away. The case the kernel
    /// version's man page gives: "echo q | axcall ax0 db0fhn" otherwise closes
    /// the moment echo finishes, so the reply is never seen. Usually wanted
    /// with <see cref="IdleTimeout"/>, or a peer that never drops the link
    /// leaves axcall waiting for ever.
    /// </summary>
    public bool WaitForRemoteDisconnect { get; init; }

    /// <summary>
    /// -S. Suppress the status lines. Errors are still reported: this silences
    /// progress, not diagnostics. Frame tracing is independent of it, so -d -S
    /// gives a trace with no status around it.
    /// </summary>
    public bool Silent { get; init; }

    /// <summary>
    /// -d. Write one line per frame, in both directions, to the status stream.
    /// The kernel version's -d set SO_DEBUG on the socket; this is the same
    /// intent served by <see cref="Ax25Listener.FrameTraced"/>.
    /// </summary>
    public bool TraceFrames { get; init; }

    /// <summary>
    /// -r. Byte transparency: stdin goes to the link exactly as it arrives, and
    /// the link goes to stdout exactly as it arrives. No line framing, no CR
    /// appended on send, no CR-to-LF translation on receive.
    /// </summary>
    /// <remarks>
    /// Off by default, which is -t, line mode, and right for a terminal. On, it
    /// is what the kernel version's -r meant, and what its man page promised
    /// with "-r together with -S in order to be really transparent": stdout
    /// carries the peer's bytes and nothing else, so axcall can sit in a
    /// pipeline or under an ssh ProxyCommand.
    /// </remarks>
    public bool Binary { get; init; }
}

public sealed class SessionRelay : IAsyncDisposable
{
    /// <summary>Exit code when <see cref="SessionRelayOptions.IdleTimeout"/> closed the link.</summary>
    public const int IdleTimeoutExitCode = 5;

    /// <summary>How long to wait for the disconnect handshake before giving up and exiting anyway.</summary>
    private static readonly TimeSpan DisconnectHandshakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Stands in for the idle watcher when no idle timeout was asked for.</summary>
    private static readonly Task<bool> NeverIdle = new TaskCompletionSource<bool>().Task;

    private readonly Ax25Listener listener;
    private readonly TextReader? input;
    private readonly TextWriter? output;
    private readonly Stream? binaryInput;
    private readonly Stream? binaryOutput;
    private readonly TextWriter? status;
    private readonly SessionRelayOptions options;
    private readonly TaskCompletionSource<Ax25Session> inboundSessionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Status and trace lines interleave from the pump thread and the relay
    // task, so they are serialised to keep a line from landing inside another.
    private readonly Lock statusLock = new();

    // Environment.TickCount64 at the last data in either direction, for -T.
    // Monotonic, so a clock step cannot make the link look idle.
    private long lastActivityTicks;

    // input/output/status default to the process console; tests inject their own
    // so multiple relays can run in one process without fighting over Console.
    // options defaults to a modulo-8 dial with the default keepalive.
    // In binary mode (-r) the text reader and writer are unused: bytes cannot
    // survive a TextWriter's encoding, so that path runs on streams instead.
    // Tests inject their own; everything else falls back to the console's.
    public SessionRelay(
        IAx25Transport modem,
        Callsign myCall,
        TextReader? input = null,
        TextWriter? output = null,
        SessionRelayOptions? options = null,
        TextWriter? status = null,
        Stream? binaryInput = null,
        Stream? binaryOutput = null)
    {
        this.input = input;
        this.output = output;
        this.binaryInput = binaryInput;
        this.binaryOutput = binaryOutput;
        this.status = status;
        this.options = options ??= new SessionRelayOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Keepalive, TimeSpan.Zero);
        if (options.IdleTimeout is { } idle)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idle, TimeSpan.Zero);

        listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = myCall,
            // Always set, never inherited: Packet.Ax25 0.35 defaults to a 30 s
            // T3 and a SABME-first dial, neither of which suits a terminal
            // talking to modulo-8 nodes over a shared channel.
            T3 = options.Keepalive,
            PreferExtendedConnect = options.Mod128,
            // Only set when the command line gave them (null otherwise), so
            // the library's spec defaults apply untouched.
            K = options.Window,
            N2 = options.Retries,
            T1V = options.Frack,
            T2 = options.AckDelay,
            PreConnectXidNegotiatesSrej = !options.NoXid,
            ConfigureSession = session =>
            {
                session.DataLinkSignalEmitted += OnSignal;
            },
        });
        if (options.Paclen is { } paclen)
        {
            // N1 is not on Ax25ListenerOptions; it lives only on the live-reseed
            // record, so seed it the way the node host does: a post-construction
            // reseed that carries everything else across unchanged.
            listener.UpdateSessionParameters(listener.CurrentSessionParameters with { N1 = paclen });
        }
        listener.SessionAccepted += OnSessionAccepted;
        if (options.TraceFrames)
        {
            listener.FrameTraced += OnFrameTraced;
        }
    }

    public async Task<int> ConnectAndRelayAsync(Callsign target, CancellationToken ct)
    {
        await listener.StartAsync(ct).ConfigureAwait(false);
        listener.AcceptIncoming = false;

        WriteStatus($"connecting to {FormatCallsign(target)}...");

        Ax25Session session;
        try
        {
            session = await listener.ConnectAsync(target, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            WriteError("connect timed out");
            return 4;
        }
        catch (InvalidOperationException)
        {
            WriteError("connect refused");
            return 4;
        }

        WriteStatus($"connected to {FormatCallsign(target)} ({DescribeLink(session.Context)})");
        return await RelayAsync(session, ct).ConfigureAwait(false);
    }

    public async Task<int> ListenAndRelayAsync(CancellationToken ct)
    {
        await listener.StartAsync(ct).ConfigureAwait(false);
        listener.AcceptIncoming = true;

        WriteStatus("listening...");

        Ax25Session session;
        try
        {
            session = await inboundSessionTcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        var peer = session.Context.Remote;
        WriteStatus($"connection from {FormatCallsign(peer)} ({DescribeLink(session.Context)})");
        return await RelayAsync(session, ct).ConfigureAwait(false);
    }

    /// <summary>Why the session stopped, which decides both the exit code and whether we hang up.</summary>
    private enum StopReason
    {
        /// <summary>The peer dropped the link; nothing left to do.</summary>
        RemoteDisconnected,

        /// <summary>End of input: we flush what is queued, then hang up.</summary>
        InputEnded,

        /// <summary>Ctrl-C: we hang up at once, without waiting to flush.</summary>
        Cancelled,

        /// <summary>-T elapsed with no data either way: we hang up, and exit 5.</summary>
        IdleTimeout,
    }

    private async Task<int> RelayAsync(Ax25Session session, CancellationToken ct)
    {
        var disconnectTcs = new TaskCompletionSource<DataLinkSignal>(TaskCreationOptions.RunContinuationsAsynchronously);

        void DisconnectHandler(object? sender, DataLinkSignal sig)
        {
            if (sig is DataLinkDisconnectIndication or DataLinkDisconnectConfirm)
            {
                disconnectTcs.TrySetResult(sig);
            }
        }

        session.DataLinkSignalEmitted += DisconnectHandler;
        try
        {
            using var stdinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            MarkActivity();
            var stdinTask = Task.Run(
                () => options.Binary
                    ? PumpBinaryInputAsync(session, stdinCts.Token)
                    : ReadStdinAsync(session, stdinCts.Token),
                CancellationToken.None);
            var idleTask = options.IdleTimeout is { } idle
                ? WatchIdleAsync(idle, idleCts.Token)
                : NeverIdle;

            // Completes on Ctrl-C. Needed because the -W wait below is not
            // watching stdin any more, so without it a cancelled token would
            // leave axcall waiting on a peer that may never hang up.
            var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelReg = ct.Register(() => cancelTcs.TrySetResult());

            var reason = await WaitForStopAsync(stdinTask, disconnectTcs.Task, idleTask, cancelTcs.Task).ConfigureAwait(false);

            await stdinCts.CancelAsync().ConfigureAwait(false);
            await idleCts.CancelAsync().ConfigureAwait(false);

            if (reason is StopReason.InputEnded or StopReason.IdleTimeout or StopReason.Cancelled)
            {
                if (reason is StopReason.IdleTimeout)
                {
                    WriteStatus($"idle for {FormatSeconds(options.IdleTimeout!.Value)}, closing the link");
                }

                // End of input means "I have said everything", not "drop it
                // now": anything still queued or unacknowledged has to go out
                // first, or piping a file through axcall would truncate it at
                // one window. Ctrl-C is the opposite and hangs up at once.
                if (reason is StopReason.InputEnded)
                {
                    await DrainAsync(session, disconnectTcs.Task, cancelTcs.Task).ConfigureAwait(false);
                }

                // Hang up properly and wait for the handshake, rather than
                // dropping the socket and leaving the peer to time out, which
                // is what the kernel version did on its idle timeout.
                session.PostEvent(new DlDisconnectRequest());
                using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                discCts.CancelAfter(DisconnectHandshakeTimeout);
                try
                {
                    await disconnectTcs.Task.WaitAsync(discCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            WriteStatus("disconnected");
            return reason is StopReason.IdleTimeout ? IdleTimeoutExitCode : 0;
        }
        finally
        {
            session.DataLinkSignalEmitted -= DisconnectHandler;
        }
    }

    /// <summary>
    /// Wait for the first thing that ends the session. With -W, end of input is
    /// not one of them: the link stays up, and received data keeps printing,
    /// until the peer hangs up or the idle timeout fires.
    /// </summary>
    private async Task<StopReason> WaitForStopAsync(Task stdinTask, Task disconnectTask, Task<bool> idleTask, Task cancelTask)
    {
        var first = await Task.WhenAny(stdinTask, disconnectTask, idleTask, cancelTask).ConfigureAwait(false);

        if (first == disconnectTask) return StopReason.RemoteDisconnected;
        if (first == idleTask) return StopReason.IdleTimeout;
        if (first == cancelTask) return StopReason.Cancelled;

        if (!options.WaitForRemoteDisconnect) return StopReason.InputEnded;

        WriteStatus("end of input; waiting for the remote to disconnect");
        var second = await Task.WhenAny(disconnectTask, idleTask, cancelTask).ConfigureAwait(false);
        if (second == idleTask) return StopReason.IdleTimeout;
        // Cancelled, or the peer hung up: either way we are finished, and a
        // cancelled relay still hangs up rather than dropping the link.
        return second == cancelTask ? StopReason.Cancelled : StopReason.RemoteDisconnected;
    }

    /// <summary>
    /// Wait until everything we have been given has actually been sent and
    /// acknowledged: nothing queued, and every I-frame sent is acked (V(A) has
    /// caught up with V(S)).
    /// </summary>
    /// <remarks>
    /// No timer bounds this, deliberately. A slow link is slow, and 4 kB at
    /// 1200 baud is a legitimate half-minute; any fixed deadline would be wrong
    /// for some real link. What bounds it instead is the link itself: if the
    /// peer stops acknowledging, the session exhausts its N2 retry budget and
    /// reports a disconnect, which is one of the things this waits on.
    /// </remarks>
    private static async Task DrainAsync(Ax25Session session, Task disconnectTask, Task cancelTask)
    {
        while (true)
        {
            bool queued;
            bool unacknowledged;
            lock (session.Context.IFrameQueue)
            {
                queued = session.Context.IFrameQueue.Count > 0;
                unacknowledged = session.Context.VA != session.Context.VS;
            }

            if (!queued && !unacknowledged)
                return;

            if (disconnectTask.IsCompleted || cancelTask.IsCompleted)
                return;

            await Task.WhenAny(
                Task.Delay(TimeSpan.FromMilliseconds(50)),
                disconnectTask,
                cancelTask).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Completes with true once no data has moved either way for the whole
    /// timeout, or false if cancelled first. Re-checks rather than arming a
    /// single timer, because every byte in either direction pushes the deadline
    /// out.
    /// </summary>
    private async Task<bool> WatchIdleAsync(TimeSpan timeout, CancellationToken ct)
    {
        var totalMs = (long)timeout.TotalMilliseconds;
        while (true)
        {
            var remaining = totalMs - (Environment.TickCount64 - Interlocked.Read(ref lastActivityTicks));
            if (remaining <= 0) return true;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(remaining), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private void MarkActivity() => Interlocked.Exchange(ref lastActivityTicks, Environment.TickCount64);

    private async Task ReadStdinAsync(Ax25Session session, CancellationToken ct)
    {
        var reader = input ?? Console.In;
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null)
                return;

            var bytes = Encoding.UTF8.GetBytes(line + "\r");
            session.PostEvent(new DlDataRequest(bytes));
            MarkActivity();
        }
    }

    /// <summary>
    /// -r. Stdin to the link verbatim: no lines, no appended CR, nothing
    /// interpreted. Read in frame-sized bites so a bulk transfer does not sit
    /// in a buffer waiting to be filled, and so each read maps to one I-frame.
    /// </summary>
    private async Task PumpBinaryInputAsync(Ax25Session session, CancellationToken ct)
    {
        var stream = binaryInput ?? Console.OpenStandardInput();
        var buffer = new byte[Math.Max(1, session.Context.N1)];

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Zero is end of input, the same signal a null line gives in line mode.
            if (read == 0)
                return;

            // Copied deliberately: the request holds the memory until the
            // session has framed it, and this buffer is overwritten by the
            // next read.
            session.PostEvent(new DlDataRequest(buffer.AsSpan(0, read).ToArray()));
            MarkActivity();
        }
    }

    private void OnSignal(object? sender, DataLinkSignal sig)
    {
        if (sig is DataLinkDataIndication di)
        {
            MarkActivity();
            if (options.Binary)
            {
                // Verbatim, and through a stream rather than a TextWriter: a
                // writer would re-encode, which is the whole thing -r exists to
                // stop.
                var stream = binaryOutput ?? Console.OpenStandardOutput();
                stream.Write(di.Info.Span);
                stream.Flush();
            }
            else
            {
                var writer = output ?? Console.Out;
                writer.Write(RenderReceivedText(di.Info.Span));
                writer.Flush();
            }
        }
    }

    private void OnFrameTraced(object? sender, Ax25FrameEventArgs e)
    {
        // Runs on the pump thread. A throw here would take the link down over a
        // debug line, so nothing in Format is allowed to fail and this catches
        // anything that somehow does.
        try
        {
            WriteLine(FrameTrace.Format(e));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Progress, suppressed by -S.</summary>
    private void WriteStatus(string message)
    {
        if (options.Silent) return;
        WriteLine($"axcall: {message}");
    }

    /// <summary>A diagnostic. -S silences progress, not errors, so this always prints.</summary>
    private void WriteError(string message) => WriteLine($"axcall: {message}");

    private void WriteLine(string line)
    {
        var writer = status ?? Console.Error;
        lock (statusLock)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }

    // Whole seconds read better in a status line, but a sub-second -T is legal
    // and must not round to "0 s".
    private static string FormatSeconds(TimeSpan span)
        => span.TotalSeconds >= 1
            ? $"{span.TotalSeconds:0.##} s"
            : $"{span.TotalMilliseconds:0.##} ms";

    /// <summary>
    /// Decode a received I-frame's information field as text for the terminal.
    /// Packet data is CR-terminated (0x0D); CR / CRLF are translated to LF so
    /// received lines render as real line breaks instead of carriage returns
    /// that overwrite the current line. Lone LF and text without a terminator
    /// pass through unchanged (no spurious newline is added).
    /// </summary>
    public static string RenderReceivedText(ReadOnlySpan<byte> info)
        => Encoding.UTF8.GetString(info)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private void OnSessionAccepted(object? sender, Ax25SessionEventArgs e)
    {
        inboundSessionTcs.TrySetResult(e.Session);
    }

    private static string FormatCallsign(Callsign c)
        => c.Ssid == 0 ? c.Base : c.ToString();

    /// <summary>
    /// The link parameters a session actually ended up with, read from its live
    /// context once it is connected: modulus, window, paclen and whether SREJ was
    /// negotiated. This is what the flags and the XID exchange settled between
    /// them, so the user can see it. When the library enforces a smaller window
    /// than the negotiated k (the SREJ half-modulus hold, or the modulus itself),
    /// the enforced figure is shown alongside.
    /// </summary>
    public static string DescribeLink(Ax25SessionContext ctx)
    {
        var modulus = ctx.IsExtended ? "mod-128" : "mod-8";
        var window = ctx.EffectiveWindow < ctx.K
            ? $"window {ctx.K} ({ctx.EffectiveWindow} in effect)"
            : $"window {ctx.K}";
        var srej = ctx.SrejEnabled ? "on" : "off";
        return $"{modulus}, {window}, paclen {ctx.N1}, SREJ {srej}";
    }

    public async ValueTask DisposeAsync()
    {
        if (options.TraceFrames)
        {
            listener.FrameTraced -= OnFrameTraced;
        }
        await listener.DisposeAsync().ConfigureAwait(false);
    }
}
