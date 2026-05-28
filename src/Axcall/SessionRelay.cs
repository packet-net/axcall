using System.Text;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;

namespace Axcall;

public sealed class SessionRelay : IAsyncDisposable
{
    private readonly Ax25Listener listener;
    private readonly TextReader? input;
    private readonly TextWriter? output;
    private readonly TaskCompletionSource<Ax25Session> inboundSessionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // input/output default to the process console; tests inject their own so
    // multiple relays can run in one process without fighting over Console.
    public SessionRelay(IKissModem modem, Callsign myCall, TextReader? input = null, TextWriter? output = null)
    {
        this.input = input;
        this.output = output;
        listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = myCall,
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
        // Injected reader (tests / explicit redirection): use the async path.
        if (input is not null)
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await input.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (line is null)
                    return;

                session.PostEvent(new DlDataRequest(Encoding.UTF8.GetBytes(line + "\r")));
            }
            return;
        }

        // Real console/pipe: read synchronously on a dedicated background
        // thread. The cancellable Console.In.ReadLineAsync is unreliable on the
        // Windows console — it can surface existing console-buffer content (the
        // shell prompt) instead of just the user's typed line. Plain
        // Console.ReadLine is the robust path and mirrors the blocking read-pump
        // the KISS serial modem uses. A blocking read can't be interrupted, so
        // on shutdown we stop posting and let the thread die with the process.
        var done = new TaskCompletionSource();
        var reader = new Thread(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string? line = Console.ReadLine();
                    if (line is null || ct.IsCancellationRequested)
                        break;
                    session.PostEvent(new DlDataRequest(Encoding.UTF8.GetBytes(line + "\r")));
                }
            }
#pragma warning disable CA1031 // a background reader must not crash the process
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                done.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "axcall-stdin",
        };
        reader.Start();

        using var registration = ct.Register(() => done.TrySetResult());
        await done.Task.ConfigureAwait(false);
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
