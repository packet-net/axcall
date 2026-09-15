using System.Collections.Concurrent;
using System.Threading.Channels;
using Packet.Ax25.Session;
using Packet.Core;

namespace Axcall;

/// <summary>
/// Collects what arrives on a session, from before the conversation starts
/// rather than from whenever the caller gets round to asking.
/// </summary>
/// <remarks>
/// <para>
/// Sessions are built by the listener before the handshake finishes, and a
/// station that sends a banner the instant the link comes up can have bytes on
/// the way before there is anything to attach a handler to. Subscribing at
/// session-construction time and buffering closes that window: the data is
/// already collected by the time it is asked for.
/// </para>
/// <para>
/// Subscriptions are keyed by remote callsign rather than by session object.
/// For an outbound call the object does not exist until the dial returns,
/// whereas the callsign is known before it starts, so arming can happen first.
/// For an inbound call the listener may hand back a session it built for an
/// earlier conversation with the same peer, and keying on the callsign is what
/// lets a fresh subscription replace the spent one rather than the new
/// conversation writing into a channel that was closed with the last.
/// </para>
/// <para>
/// So: <see cref="Attach"/> once per session, from the listener's
/// ConfigureSession hook, and <see cref="Arm"/> once per conversation, before
/// an outbound dial or on an inbound accept.
/// </para>
/// </remarks>
public sealed class SessionInbox
{
    private readonly ConcurrentDictionary<Callsign, Subscription> current = new();

    /// <summary>
    /// Wire up a newly built session. Meant as the listener's
    /// <c>ConfigureSession</c> hook, which runs before any event reaches the
    /// session.
    /// </summary>
    public void Attach(Ax25Session session)
    {
        session.DataLinkSignalEmitted += (_, signal) =>
        {
            if (!current.TryGetValue(session.Context.Remote, out var subscription))
                return;

            switch (signal)
            {
                case DataLinkDataIndication data:
                    // Copied: the indication's memory is not ours to keep.
                    subscription.Post(data.Info.Span.ToArray());
                    break;

                case DataLinkDisconnectIndication:
                case DataLinkDisconnectConfirm:
                    subscription.Close();
                    break;
            }
        };
    }

    /// <summary>
    /// Start collecting for a conversation with <paramref name="remote"/>,
    /// discarding anything left from the last one.
    /// </summary>
    public Subscription Arm(Callsign remote)
    {
        var subscription = new Subscription();
        current[remote] = subscription;
        return subscription;
    }

    /// <summary>Stop collecting, unless someone else has already taken over.</summary>
    public void Disarm(Callsign remote, Subscription subscription)
        => current.TryRemove(new KeyValuePair<Callsign, Subscription>(remote, subscription));

    /// <summary>One conversation's worth of inbound data and its end.</summary>
    public sealed class Subscription
    {
        // Unbounded is safe here in a way it usually is not: the producer is a
        // radio link running at a few thousand bits per second, and the
        // consumer is a local socket. A backlog cannot outrun the reader by
        // enough to matter, and dropping received data to save memory would be
        // the wrong trade on a link this expensive.
        private readonly Channel<byte[]> channel =
            Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        private readonly TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChannelReader<byte[]> Data => channel.Reader;

        /// <summary>Completes when the peer drops the link.</summary>
        public Task Disconnected => disconnected.Task;

        public void Post(byte[] info) => channel.Writer.TryWrite(info);

        /// <summary>
        /// The link is gone. The channel is completed as well as the task, so a
        /// reader drains what already arrived and then stops, rather than
        /// waiting for more that cannot come.
        /// </summary>
        public void Close()
        {
            channel.Writer.TryComplete();
            disconnected.TrySetResult();
        }

        /// <summary>No more data will be wanted; used when this side hangs up.</summary>
        public void Finish() => channel.Writer.TryComplete();
    }
}
