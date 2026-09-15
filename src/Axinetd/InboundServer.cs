using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Axcall;
using Packet.Ax25.Session;
using Packet.Core;

namespace Axinetd;

/// <summary>
/// Answers inbound AX.25 calls by running something: a program with the session
/// on its standard input and output, or a TCP connection to a local service.
/// </summary>
/// <remarks>
/// The callsign called is the whole address, so it is also the whole routing
/// decision. There are no port numbers on this side of the link.
/// </remarks>
internal sealed class InboundServer(
    Ax25Listener listener,
    SessionInbox inbox,
    IReadOnlyDictionary<Callsign, InetdRule> rules,
    TextWriter log,
    bool quiet)
{
    /// <summary>How long a program gets to exit on its own after the link closes.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);

    // One session exists per pair of callsigns, and an accept can be raised for
    // a session that is already in a conversation. Running a second handler on
    // it would give one caller two programs writing down one link.
    private readonly HashSet<Callsign> busy = [];
    private readonly Lock busyLock = new();

    private readonly List<Task> running = [];
    private readonly Lock runningLock = new();

    public void Start(CancellationToken ct)
    {
        listener.SessionAccepted += (_, e) =>
        {
            var session = e.Session;
            var caller = session.Context.Remote;
            var called = session.Context.Local;

            if (!TryClaim(caller))
            {
                Write($"{caller} called {called} while already in session; ignoring the second call");
                return;
            }

            // Armed here rather than in ConfigureSession because a session is
            // reused for a peer that calls again, and this fires for every
            // call while that hook fires only for the first.
            var subscription = inbox.Arm(caller);

            var task = Task.Run(async () =>
            {
                try
                {
                    await ServeAsync(session, subscription, caller, called, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    WriteAlways($"{caller}: {ex.Message}");
                }
                finally
                {
                    inbox.Disarm(caller, subscription);
                    Release(caller);
                }
            }, CancellationToken.None);

            lock (runningLock)
            {
                running.Add(task);
                running.RemoveAll(t => t.IsCompleted);
            }
        };
    }

    /// <summary>
    /// Wait for the conversations still in progress, so a shutdown does not cut
    /// one off mid-frame.
    /// </summary>
    public async Task DrainAsync()
    {
        Task[] outstanding;
        lock (runningLock)
        {
            outstanding = [.. running];
        }

        await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None)
            .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
    }

    private async Task ServeAsync(
        Ax25Session session,
        SessionInbox.Subscription subscription,
        Callsign caller,
        Callsign called,
        CancellationToken ct)
    {
        if (!rules.TryGetValue(called, out var rule))
        {
            // Should not happen: the listener only answers for callsigns that
            // came out of the file. Worth saying rather than silently dropping
            // the caller, because it means the two have drifted apart.
            WriteAlways($"{caller} reached {called}, which has no rule; hanging up");
            await SessionTeardown.HangUpAsync(session, subscription.Disconnected, ct).ConfigureAwait(false);
            return;
        }

        Write($"{caller} -> {called} ({SessionRelay.DescribeLink(session.Context)})");

        var outcome = rule.Action switch
        {
            InetdAction.Exec => await ExecAsync(session, subscription, rule, caller, ct).ConfigureAwait(false),
            _ => await ForwardAsync(session, subscription, rule, caller, ct).ConfigureAwait(false),
        };

        Write($"{caller}: {outcome}");
    }

    private async Task<string> ExecAsync(
        Ax25Session session,
        SessionInbox.Subscription subscription,
        InetdRule rule,
        Callsign caller,
        CancellationToken ct)
    {
        var arguments = InetdFile.ExpandArguments(rule, caller);

        var startInfo = new ProcessStartInfo(rule.Target)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // The one thing a service can know about who is on the other end,
        // available without having to accept it as an argument.
        startInfo.Environment["AX25_CALLER"] = caller.ToString();

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start {rule.Target}");

        Write($"{caller}: running {rule.Target} as pid {process.Id}");

        // The program's standard error is the operator's, not the caller's.
        // Sending it down the link would leak paths and stack traces to a
        // stranger, and interleave them with whatever the program meant to say.
        var stderrPump = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                    WriteAlways($"{caller}: {rule.Target}: {line}");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);

        var outcome = await new SessionStreamBridge(
            session,
            subscription,
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            ct).RunAsync().ConfigureAwait(false);

        // The link is gone, so the program is talking to nobody. Closing its
        // input is the polite way to say so; anything that does not take the
        // hint is stopped, because an inbound call must not be able to leave a
        // process behind.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        try
        {
            await process.WaitForExitAsync(new CancellationTokenSource(ExitGrace).Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            WriteAlways($"{caller}: {rule.Target} did not exit within {ExitGrace.TotalSeconds:0} s; killing pid {process.Id}");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }
        }

        await stderrPump.ConfigureAwait(false);
        return $"{outcome}; {rule.Target} exited {(process.HasExited ? process.ExitCode : -1)}";
    }

    private async Task<string> ForwardAsync(
        Ax25Session session,
        SessionInbox.Subscription subscription,
        InetdRule rule,
        Callsign caller,
        CancellationToken ct)
    {
        if (!TransportSpec.TryParseEndpoint(rule.Target, out var endpoint, out var error))
            throw new InvalidOperationException(error);

        using var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(endpoint!.Host!, endpoint.TcpPort, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            WriteAlways($"{caller}: cannot reach {rule.Target}: {ex.Message}");
            await SessionTeardown.HangUpAsync(session, subscription.Disconnected, ct).ConfigureAwait(false);
            return $"{rule.Target} refused the connection";
        }

        Write($"{caller}: forwarding to {rule.Target}");

        var stream = client.GetStream();
        return await new SessionStreamBridge(session, subscription, stream, stream, ct)
            .RunAsync().ConfigureAwait(false);
    }

    private bool TryClaim(Callsign caller)
    {
        lock (busyLock)
        {
            return busy.Add(caller);
        }
    }

    private void Release(Callsign caller)
    {
        lock (busyLock)
        {
            busy.Remove(caller);
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
            log.WriteLine($"{now} axinetd: {message}");
            log.Flush();
        }
    }
}
