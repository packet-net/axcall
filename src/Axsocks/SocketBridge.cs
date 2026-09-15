using Axcall;
using Packet.Ax25.Session;

namespace Axsocks;

/// <summary>
/// Copies bytes between a connected AX.25 session and a socket until one end
/// stops, then closes the link properly.
/// </summary>
internal sealed class SocketBridge(
    Ax25Session session,
    SessionInbox.Subscription inbox,
    Stream stream,
    CancellationToken ct)
{
    /// <summary>
    /// Run until one side finishes. Returns a short description of which one
    /// did, for the log.
    /// </summary>
    public async Task<string> RunAsync()
    {
        var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelReg = ct.Register(() => cancelTcs.TrySetResult());

        var toSocket = Task.Run(PumpToSocketAsync, CancellationToken.None);
        var fromSocket = Task.Run(PumpFromSocketAsync, CancellationToken.None);

        var first = await Task.WhenAny(fromSocket, inbox.Disconnected, cancelTcs.Task).ConfigureAwait(false);

        string outcome;
        if (first == inbox.Disconnected)
        {
            // The station hung up. Nothing to send and nothing to wait for;
            // what is left is to hand the client whatever already arrived.
            outcome = "the station hung up";
        }
        else if (first == cancelTcs.Task)
        {
            // Shutting down. Hang up at once rather than flushing: the operator
            // asked for this to stop, not to finish.
            await SessionTeardown.HangUpAsync(session, inbox.Disconnected, CancellationToken.None).ConfigureAwait(false);
            outcome = "the proxy is shutting down";
        }
        else
        {
            // The client closed its end, which means "I have said everything",
            // not "drop it now". Anything still queued or unacknowledged has to
            // go out before the link closes, or a file sent through the proxy
            // would be truncated at one window.
            await SessionTeardown.DrainAsync(session, inbox.Disconnected, cancelTcs.Task).ConfigureAwait(false);
            await SessionTeardown.HangUpAsync(session, inbox.Disconnected, ct).ConfigureAwait(false);
            outcome = "the client hung up";
        }

        // Either way, everything received is written out before the socket
        // closes behind us.
        inbox.Finish();
        await toSocket.ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Received frames to the client, in order, one write per frame.</summary>
    private async Task PumpToSocketAsync()
    {
        try
        {
            // Deliberately not cancelled: bytes that crossed the radio link are
            // expensive, and dropping them on the floor because the proxy is
            // stopping would be a poor way to treat them. A dead client fails
            // the write instead, which is caught below.
            await foreach (var info in inbox.Data.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await stream.WriteAsync(info, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The client's bytes to the link, verbatim. Read in frame-sized bites so a
    /// bulk transfer does not sit in a buffer waiting to be filled, and so each
    /// read maps to one I-frame.
    /// </summary>
    private async Task PumpFromSocketAsync()
    {
        var buffer = new byte[Math.Max(1, session.Context.N1)];

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            if (read == 0)
                return;

            // Copied deliberately: the request holds the memory until the
            // session has framed it, and this buffer is overwritten by the
            // next read.
            session.PostEvent(new DlDataRequest(buffer.AsSpan(0, read).ToArray()));
        }
    }
}
