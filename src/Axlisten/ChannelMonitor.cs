using System.Threading.Channels;
using Packet.Ax25.Transport;

namespace Axlisten;

/// <summary>A port to listen on, and how to open it.</summary>
/// <param name="Name">What the port is called in the output.</param>
/// <param name="Description">The transport behind it, for status messages.</param>
/// <param name="Open">Opens the transport. Called again to reconnect.</param>
/// <param name="Required">
/// True for a port named on the command line, which has to open. False for
/// one found in the ports file when none was named, where a port that is
/// unplugged or already held by another program is a warning, not a reason
/// to listen to nothing.
/// </param>
internal sealed record MonitorPort(
    string Name,
    string Description,
    Func<CancellationToken, Task<IAx25Transport>> Open,
    bool Required = true);

/// <summary>
/// Listens on every port at once, and writes each frame as it arrives.
/// </summary>
/// <remarks>
/// <para>
/// Receive only. Nothing here ever calls SendAsync, and no callsign is
/// needed, so it is safe to run on any port at any time.
/// </para>
/// <para>
/// A port that goes away is reopened, with a growing delay, for as long as
/// the monitor runs. A monitor is left running, and the TNC at the other end
/// of it gets rebooted, unplugged and upgraded meanwhile. A TCP modem also
/// ends its stream after a long silence, to catch a half-open link; that is
/// redialled at once and without comment, and only a redial that fails is
/// reported.
/// </para>
/// </remarks>
internal sealed class ChannelMonitor(
    IReadOnlyList<MonitorPort> ports,
    IFrameFormatter formatter,
    CallFilter filter,
    TextWriter output,
    TextWriter log,
    bool quiet,
    TimeSpan? firstRetry = null)
{
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleRedialAfter = TimeSpan.FromSeconds(30);
    private readonly TimeSpan firstRetry = firstRetry ?? TimeSpan.FromSeconds(1);

    private readonly Channel<MonitoredFrame> heard =
        Channel.CreateUnbounded<MonitoredFrame>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Run until cancelled. Returns the process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var open = new List<(MonitorPort Port, IAx25Transport Transport)>();
        foreach (var port in ports)
        {
            try
            {
                open.Add((port, await port.Open(ct).ConfigureAwait(false)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CloseAllAsync(open).ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex)
            {
                if (port.Required)
                {
                    log.WriteLine($"axlisten: {port.Name}: failed to open {port.Description}: {ex.Message}");
                    await CloseAllAsync(open).ConfigureAwait(false);
                    return 3;
                }
                log.WriteLine($"axlisten: {port.Name}: skipped, cannot open {port.Description}: {ex.Message}");
            }
        }

        if (open.Count == 0)
        {
            log.WriteLine("axlisten: none of the configured ports could be opened");
            return 3;
        }

        if (!quiet)
        {
            log.WriteLine($"axlisten: listening on {string.Join(", ", open.Select(o => Describe(o.Port)))}");
        }

        // The writer stops everything when its reader goes away, so the
        // pumps share a token it can cancel.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = open.Select(o => PumpAsync(o.Port, o.Transport, stop.Token)).ToList();
        var writer = WriteAsync(stop);

        await Task.WhenAll(pumps).ConfigureAwait(false);
        heard.Writer.TryComplete();
        return await writer.ConfigureAwait(false);
    }

    private async Task<int> WriteAsync(CancellationTokenSource stop)
    {
        var ct = stop.Token;
        try
        {
            await foreach (var frame in heard.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!filter.Matches(frame))
                    continue;
                await output.WriteAsync(formatter.Format(frame)).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The reader went away: `axlisten | head`, or a closed pipe. That
            // is how a monitor is normally stopped from a script.
            await stop.CancelAsync().ConfigureAwait(false);
        }
        return 0;
    }

    private async Task PumpAsync(MonitorPort port, IAx25Transport transport, CancellationToken ct)
    {
        var delay = firstRetry;

        while (!ct.IsCancellationRequested)
        {
            string? why = null;
            var since = DateTimeOffset.UtcNow;
            try
            {
                await foreach (var inbound in transport.ReceiveAsync(ct).ConfigureAwait(false))
                {
                    // Copied: a transport may reuse its buffer once the
                    // enumerator moves on, and the writer reads it later.
                    heard.Writer.TryWrite(new MonitoredFrame(
                        port.Name, inbound.PortId, inbound.ReceivedAt, inbound.Ax25.ToArray(), inbound.Radio));
                    delay = firstRetry;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                why = ex.Message;
            }

            await CloseAsync(transport).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                return;

            // First, straight back: this is what an idle timeout looks like.
            // Not when the stream barely lasted, or a peer that accepts and
            // hangs up at once would have us spinning.
            if (why is null && DateTimeOffset.UtcNow - since > IdleRedialAfter && await TryOpenAsync(port, ct).ConfigureAwait(false) is { } again)
            {
                transport = again;
                continue;
            }

            log.WriteLine($"axlisten: {port.Name}: lost {port.Description}{(why is null ? "" : $": {why}")}; retrying");

            IAx25Transport? reopened = null;
            while (reopened is null && !ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                delay = delay * 2 > MaxRetry ? MaxRetry : delay * 2;
                reopened = await TryOpenAsync(port, ct).ConfigureAwait(false);
            }

            if (reopened is null)
                return;

            log.WriteLine($"axlisten: {port.Name}: reopened {port.Description}");
            transport = reopened;
        }

        await CloseAsync(transport).ConfigureAwait(false);
    }

    // "radio (/dev/ttyUSB0:57600)", but a port named by its own spec once.
    private static string Describe(MonitorPort port)
        => port.Name == port.Description ? port.Name : $"{port.Name} ({port.Description})";

    private static async Task<IAx25Transport?> TryOpenAsync(MonitorPort port, CancellationToken ct)
    {
        try
        {
            return await port.Open(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task CloseAllAsync(List<(MonitorPort Port, IAx25Transport Transport)> open)
    {
        foreach (var (_, transport) in open)
            await CloseAsync(transport).ConfigureAwait(false);
    }

    private static async Task CloseAsync(IAx25Transport transport)
    {
        try
        {
            if (transport is IAsyncDisposable ad)
                await ad.DisposeAsync().ConfigureAwait(false);
            else if (transport is IDisposable d)
                d.Dispose();
        }
        catch (Exception)
        {
            // Closing something that has already failed can fail again.
        }
    }
}
