using Packet.Ax25;

namespace Axlisten;

/// <summary>
/// Remembers which links on the channel were set up as modulo 128, so their I
/// and S frames can be read correctly.
/// </summary>
/// <remarks>
/// <para>
/// An I or S frame's control field is one octet under modulo 8 and two under
/// modulo 128, and nothing in the frame says which. The two stations know
/// because they negotiated it: SABME for 128, SABM for 8. A monitor that heard
/// the negotiation knows too, and that is all this is. Unnumbered frames are
/// the same width either way, so the set-up itself always reads correctly.
/// </para>
/// <para>
/// A link already up when the monitor started has not been seen set up, and
/// falls back to <see cref="MonitoredFrame.LooksExtended"/>.
/// </para>
/// </remarks>
internal sealed class LinkTracker
{
    // Bounded, so a busy channel over a long run cannot grow this forever.
    // A link that falls out simply goes back to being guessed at.
    private const int MaxLinks = 1024;

    private readonly Dictionary<string, bool> links = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    /// <summary>Note what this frame says about its link, and tell it what is known.</summary>
    public void Observe(MonitoredFrame heard)
    {
        // Addresses and U frames read the same at either modulus, so a
        // modulo-8 parse is enough to find the link and see a set-up.
        if (MonitoredFrame.Parse(heard.Bytes.Span) is not { } frame)
            return;

        var key = Key(heard.Label, frame);

        switch (frame.FrameType)
        {
            case Ax25FrameType.Sabme when frame.IsCommand:
                Remember(key, true);
                break;
            case Ax25FrameType.Sabm when frame.IsCommand:
                Remember(key, false);
                break;
            case Ax25FrameType.Disc:
            case Ax25FrameType.Dm:
                links.Remove(key);
                break;
        }

        heard.LinkExtended = links.TryGetValue(key, out var extended) ? extended : null;
    }

    private void Remember(string key, bool extended)
    {
        if (!links.ContainsKey(key))
        {
            order.Enqueue(key);
            while (order.Count > MaxLinks)
                links.Remove(order.Dequeue());
        }
        links[key] = extended;
    }

    // Either direction is the same link.
    private static string Key(string port, Ax25Frame frame)
    {
        var a = frame.Source.Callsign.ToString();
        var b = frame.Destination.Callsign.ToString();
        return string.CompareOrdinal(a, b) < 0 ? $"{port} {a} {b}" : $"{port} {b} {a}";
    }
}
