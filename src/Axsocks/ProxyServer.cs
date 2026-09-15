using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Axcall;
using Packet.Ax25.Session;
using Packet.Core;

namespace Axsocks;

/// <summary>
/// A SOCKS5 proxy in front of one AX.25 port: every accepted CONNECT becomes a
/// connected-mode session to a callsign, and the bytes pass through untouched.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is exact rather than approximate. A connected-mode AX.25
/// session is reliable, ordered and flow-controlled, and so is a TCP
/// connection, so a proxy between them has nothing to reconcile: it opens a
/// link, copies bytes, and closes the link when the bytes run out.
/// </para>
/// <para>
/// What it is not is a general purpose network. There is no UDP, no ICMP and
/// no notion of a port on the far side: the port number in a SOCKS request is
/// read and ignored, because a callsign is the whole address. A station
/// decides what to run for a caller by which of its callsigns was called, not
/// by a port number.
/// </para>
/// </remarks>
internal sealed class ProxyServer(
    Ax25Listener listener,
    SessionInbox inbox,
    IReadOnlyDictionary<string, HostEntry> hosts,
    string portName,
    TextWriter log,
    bool quiet)
{
    // One AX.25 session exists per (local, remote) callsign pair, and the
    // library hands the same session back for a second connect to the same
    // peer. Two proxied connections to one station would therefore share one
    // link and interleave their bytes, so the second is refused instead.
    private readonly HashSet<Callsign> busy = [];
    private readonly Lock busyLock = new();

    private readonly TaskCompletionSource<IPEndPoint> bound =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The endpoint actually listened on, once it is. Worth having separately
    /// from what was asked for: port 0 means "any", and the answer is only
    /// known after the bind.
    /// </summary>
    public Task<IPEndPoint> Bound => bound.Task;

    public async Task RunAsync(IPEndPoint bind, CancellationToken ct)
    {
        using var tcp = new TcpListener(bind);
        tcp.Start();

        var actual = (IPEndPoint)tcp.LocalEndpoint;
        bound.TrySetResult(actual);
        Write($"listening on {actual}, calling out as {listener.MyCall} over {portName}");

        var connections = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Each connection runs on its own; one client hanging up
                // badly, or one station refusing to answer, must not stop the
                // proxy serving anyone else.
                connections.Add(Task.Run(() => HandleAsync(client, ct), CancellationToken.None));
                connections.RemoveAll(t => t.IsCompleted);
            }
        }
        finally
        {
            bound.TrySetCanceled(CancellationToken.None);
            tcp.Stop();
            // Give the live conversations a moment to end themselves now that
            // the token is cancelled, rather than dropping them mid-frame.
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var peer = client.Client.RemoteEndPoint;
        using (client)
        {
            // Nagle would hold a short request back waiting for company, which
            // on a link this slow is exactly the wrong trade.
            client.NoDelay = true;
            var stream = client.GetStream();

            try
            {
                var result = await Socks5.ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (result.Request is not { } request)
                {
                    if (result.CanReply)
                        await Socks5.WriteReplyAsync(stream, result.Reply, ct).ConfigureAwait(false);
                    Write($"{peer}: {result.Error}");
                    return;
                }

                if (!HostsFile.TryResolve(hosts, request.Host, out var target, out var wantedPort, out var error))
                {
                    await Socks5.WriteReplyAsync(stream, Socks5Reply.HostUnreachable, ct).ConfigureAwait(false);
                    Write($"{peer}: {error}");
                    return;
                }

                // One modem, one port. An entry naming a different one is a
                // configuration the operator meant, so say so plainly rather
                // than silently calling over the wrong radio.
                if (wantedPort is not null && !string.Equals(wantedPort, portName, StringComparison.Ordinal))
                {
                    await Socks5.WriteReplyAsync(stream, Socks5Reply.NetworkUnreachable, ct).ConfigureAwait(false);
                    Write($"{peer}: {request.Host} is on port '{wantedPort}', and this proxy is on '{portName}'");
                    return;
                }

                if (!TryClaim(target))
                {
                    await Socks5.WriteReplyAsync(stream, Socks5Reply.GeneralFailure, ct).ConfigureAwait(false);
                    Write($"{peer}: {target} already has a session through this proxy; "
                        + "one connection at a time per station");
                    return;
                }

                try
                {
                    await ConnectAndRelayAsync(stream, peer, request, target, ct).ConfigureAwait(false);
                }
                finally
                {
                    Release(target);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException ex)
            {
                Write($"{peer}: {ex.Message}");
            }
            catch (SocketException ex)
            {
                Write($"{peer}: {ex.Message}");
            }
        }
    }

    private async Task ConnectAndRelayAsync(
        NetworkStream stream,
        EndPoint? peer,
        Socks5Request request,
        Callsign target,
        CancellationToken ct)
    {
        Write($"{peer}: {request.Host} -> {target}, connecting");

        // Armed before the dial, not after it: a station that answers with a
        // banner can have bytes on the way before ConnectAsync has returned.
        var subscription = inbox.Arm(target);
        try
        {
            Ax25Session session;
            try
            {
                session = await listener.ConnectAsync(target, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await Socks5.WriteReplyAsync(stream, Socks5Reply.HostUnreachable, ct).ConfigureAwait(false);
                Write($"{peer}: {target} did not answer");
                return;
            }
            catch (InvalidOperationException)
            {
                await Socks5.WriteReplyAsync(stream, Socks5Reply.ConnectionRefused, ct).ConfigureAwait(false);
                Write($"{peer}: {target} refused the connection");
                return;
            }

            Write($"{peer}: connected to {target} ({SessionRelay.DescribeLink(session.Context)})");
            await Socks5.WriteReplyAsync(stream, Socks5Reply.Succeeded, ct).ConfigureAwait(false);

            var outcome = await new SessionStreamBridge(session, subscription, stream, stream, ct)
                .RunAsync().ConfigureAwait(false);
            Write($"{peer}: {target} closed, {outcome}");
        }
        finally
        {
            inbox.Disarm(target, subscription);
        }
    }

    private bool TryClaim(Callsign target)
    {
        lock (busyLock)
        {
            return busy.Add(target);
        }
    }

    private void Release(Callsign target)
    {
        lock (busyLock)
        {
            busy.Remove(target);
        }
    }

    private void Write(string message)
    {
        if (quiet) return;
        WriteAlways(message);
    }

    private void WriteAlways(string message)
    {
        // Timestamped, because this runs unattended and a log line without one
        // is half a log line. Local time, second resolution, ASCII throughout.
        var now = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        lock (log)
        {
            log.WriteLine($"{now} axsocks: {message}");
            log.Flush();
        }
    }
}
