using Packet.Ax25.Session;

namespace Axcall;

/// <summary>
/// Closing a session properly: flush what is queued, then hang up and wait for
/// the handshake.
/// </summary>
/// <remarks>
/// Shared because it is the subtle part and there is now more than one caller.
/// The terminal and the proxy pump bytes differently, but they end a link the
/// same way, and getting that wrong truncates transfers in a way that only
/// shows up on a slow link with more data than one window.
/// </remarks>
public static class SessionTeardown
{
    /// <summary>How long to wait for the disconnect handshake before giving up and going anyway.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Wait until everything handed to the session has actually been sent and
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
    /// <param name="disconnected">Completes when the peer drops the link.</param>
    /// <param name="abort">Completes when the user gives up waiting (Ctrl-C).</param>
    public static async Task DrainAsync(Ax25Session session, Task disconnected, Task abort)
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

            if (disconnected.IsCompleted || abort.IsCompleted)
                return;

            await Task.WhenAny(
                Task.Delay(TimeSpan.FromMilliseconds(50)),
                disconnected,
                abort).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hang up and wait for the handshake, rather than dropping the link and
    /// leaving the peer to time out, which is what the kernel version did on
    /// its idle timeout. Returns once the peer has acknowledged, the wait has
    /// run out of patience, or the caller has been cancelled.
    /// </summary>
    public static async Task HangUpAsync(Ax25Session session, Task disconnected, CancellationToken ct)
    {
        session.PostEvent(new DlDisconnectRequest());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HandshakeTimeout);
        try
        {
            await disconnected.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
