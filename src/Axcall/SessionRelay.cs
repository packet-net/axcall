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
}

public sealed class SessionRelay : IAsyncDisposable
{
    private readonly Ax25Listener listener;
    private readonly TextReader? input;
    private readonly TextWriter? output;
    private readonly TaskCompletionSource<Ax25Session> inboundSessionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // input/output default to the process console; tests inject their own so
    // multiple relays can run in one process without fighting over Console.
    // options defaults to a modulo-8 dial with the default keepalive.
    public SessionRelay(IAx25Transport modem, Callsign myCall, TextReader? input = null, TextWriter? output = null, SessionRelayOptions? options = null)
    {
        this.input = input;
        this.output = output;
        options ??= new SessionRelayOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Keepalive, TimeSpan.Zero);
        listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = myCall,
            // Always set, never inherited: Packet.Ax25 0.35 defaults to a 30 s
            // T3 and a SABME-first dial, neither of which suits a terminal
            // talking to modulo-8 nodes over a shared channel.
            T3 = options.Keepalive,
            PreferExtendedConnect = options.Mod128,
            ConfigureSession = session =>
            {
                session.DataLinkSignalEmitted += OnSignal;
            },
        });
        listener.SessionAccepted += OnSessionAccepted;
    }

    public async Task<int> ConnectAndRelayAsync(Callsign target, CancellationToken ct)
    {
        await listener.StartAsync(ct).ConfigureAwait(false);
        listener.AcceptIncoming = false;

        await Console.Error.WriteLineAsync($"axcall: connecting to {FormatCallsign(target)}...").ConfigureAwait(false);

        Ax25Session session;
        try
        {
            session = await listener.ConnectAsync(target, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync("axcall: connect timed out").ConfigureAwait(false);
            return 4;
        }
        catch (InvalidOperationException)
        {
            await Console.Error.WriteLineAsync("axcall: connect refused").ConfigureAwait(false);
            return 4;
        }

        await Console.Error.WriteLineAsync($"axcall: connected to {FormatCallsign(target)}").ConfigureAwait(false);
        return await RelayAsync(session, ct).ConfigureAwait(false);
    }

    public async Task<int> ListenAndRelayAsync(CancellationToken ct)
    {
        await listener.StartAsync(ct).ConfigureAwait(false);
        listener.AcceptIncoming = true;

        await Console.Error.WriteLineAsync("axcall: listening...").ConfigureAwait(false);

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
        await Console.Error.WriteLineAsync($"axcall: connection from {FormatCallsign(peer)}").ConfigureAwait(false);
        return await RelayAsync(session, ct).ConfigureAwait(false);
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
            var stdinTask = Task.Run(() => ReadStdinAsync(session, stdinCts.Token), CancellationToken.None);

            var completed = await Task.WhenAny(stdinTask, disconnectTcs.Task).ConfigureAwait(false);

            if (completed == stdinTask)
            {
                session.PostEvent(new DlDisconnectRequest());
                using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                discCts.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await disconnectTcs.Task.WaitAsync(discCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            else
            {
                await stdinCts.CancelAsync().ConfigureAwait(false);
            }

            await Console.Error.WriteLineAsync("axcall: disconnected").ConfigureAwait(false);
            return 0;
        }
        finally
        {
            session.DataLinkSignalEmitted -= DisconnectHandler;
        }
    }

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
        }
    }

    private void OnSignal(object? sender, DataLinkSignal sig)
    {
        if (sig is DataLinkDataIndication di)
        {
            var writer = output ?? Console.Out;
            writer.Write(RenderReceivedText(di.Info.Span));
            writer.Flush();
        }
    }

    /// <summary>
    /// Decode a received I-frame's information field as text for the terminal.
    /// Packet data is CR-terminated (0x0D); CR / CRLF are translated to LF so
    /// received lines render as real line breaks instead of carriage returns
    /// that overwrite the current line. Lone LF and text without a terminator
    /// pass through unchanged (no spurious newline is added).
    /// </summary>
    internal static string RenderReceivedText(ReadOnlySpan<byte> info)
        => Encoding.UTF8.GetString(info)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private void OnSessionAccepted(object? sender, Ax25SessionEventArgs e)
    {
        inboundSessionTcs.TrySetResult(e.Session);
    }

    private static string FormatCallsign(Callsign c)
        => c.Ssid == 0 ? c.Base : c.ToString();

    public async ValueTask DisposeAsync()
    {
        await listener.DisposeAsync().ConfigureAwait(false);
    }
}
