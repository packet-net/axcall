using Packet.Ax25.Session;

namespace Axcall;

/// <summary>
/// Copies bytes between a connected AX.25 session and a pair of streams until
/// one end stops, then closes the link properly.
/// </summary>
/// <remarks>
/// Two streams rather than one because the far side is not always a socket. A
/// proxied connection reads and writes the same socket; a program run for an
/// inbound caller has a stdin and a stdout, which are different objects
/// pointing opposite ways.
/// </remarks>
/// <param name="session">The connected session.</param>
/// <param name="inbox">Where this conversation's received data is collecting.</param>
/// <param name="input">Bytes to send, read until it ends.</param>
/// <param name="output">Where received bytes are written.</param>
/// <param name="ct">Cancelled when the service is shutting down.</param>
public sealed class SessionStreamBridge(
    Ax25Session session,
    SessionInbox.Subscription inbox,
    Stream input,
    Stream output,
    CancellationToken ct)
{
    /// <summary>Why the conversation ended, in a form fit for a log line.</summary>
    public const string StationHungUp = "the station hung up";

    /// <summary>
    /// Run until one side finishes. Returns a short description of which one
    /// did, for the log.
    /// </summary>
    public async Task<string> RunAsync()
    {
        var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelReg = ct.Register(() => cancelTcs.TrySetResult());

        var toOutput = Task.Run(PumpToOutputAsync, CancellationToken.None);
        var fromInput = Task.Run(PumpFromInputAsync, CancellationToken.None);

        var first = await Task.WhenAny(fromInput, inbox.Disconnected, cancelTcs.Task).ConfigureAwait(false);

        string outcome;
        if (first == inbox.Disconnected)
        {
            // The station hung up. Nothing to send and nothing to wait for;
            // what is left is to hand over whatever already arrived.
            outcome = StationHungUp;
        }
        else if (first == cancelTcs.Task)
        {
            // Shutting down. Hang up at once rather than flushing: the operator
            // asked for this to stop, not to finish.
            await SessionTeardown.HangUpAsync(session, inbox.Disconnected, CancellationToken.None).ConfigureAwait(false);
            outcome = "the service is shutting down";
        }
        else
        {
            // This end closed, which means "I have said everything", not "drop
            // it now". Anything still queued or unacknowledged has to go out
            // before the link closes, or a file sent through would be truncated
            // at one window.
            await SessionTeardown.DrainAsync(session, inbox.Disconnected, cancelTcs.Task).ConfigureAwait(false);
            await SessionTeardown.HangUpAsync(session, inbox.Disconnected, ct).ConfigureAwait(false);
            outcome = "the local end hung up";
        }

        // Either way, everything received is written out before the far side
        // closes behind us.
        inbox.Finish();
        await toOutput.ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Received frames to the output, in order, one write per frame.</summary>
    private async Task PumpToOutputAsync()
    {
        try
        {
            // Deliberately not cancelled: bytes that crossed the radio link are
            // expensive, and dropping them on the floor because the service is
            // stopping would be a poor way to treat them. A dead reader fails
            // the write instead, which is caught below.
            await foreach (var info in inbox.Data.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await output.WriteAsync(info, CancellationToken.None).ConfigureAwait(false);
                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The other side's bytes to the link, verbatim. Read in frame-sized bites
    /// so a bulk transfer does not sit in a buffer waiting to be filled, and so
    /// each read maps to one I-frame.
    /// </summary>
    private async Task PumpFromInputAsync()
    {
        var buffer = new byte[Math.Max(1, session.Context.N1)];

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
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
