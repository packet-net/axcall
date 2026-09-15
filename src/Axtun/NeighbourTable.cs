using Packet.Core;

namespace Axtun;

/// <summary>Where a neighbour was last heard.</summary>
/// <param name="Callsign">The station that answers for the address.</param>
/// <param name="LearnedAt">When it was last heard from.</param>
public sealed record Neighbour(Callsign Callsign, DateTimeOffset LearnedAt);

/// <summary>
/// Stations heard on the air, and the address each of them claimed.
/// </summary>
/// <remarks>
/// <para>
/// The config file decides where traffic goes; this is the fallback for
/// everyone not in it, and it is what makes answering ARP worth doing. A
/// station that ARPs us gets an answer and can then reach us, and we can only
/// answer its packets if we noticed who it was.
/// </para>
/// <para>
/// Entries expire after an hour, which is what LinBPQ uses and long enough
/// that nothing re-ARPs mid-conversation on a channel this slow. A station
/// that has gone away costs one wasted transmission before the next request
/// goes out.
/// </para>
/// </remarks>
public sealed class NeighbourTable(TimeProvider? timeProvider = null)
{
    /// <summary>How long an entry survives without being heard from again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly Dictionary<uint, Neighbour> entries = [];
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();

    /// <summary>
    /// Note that an address is reachable direct from a station.
    /// </summary>
    /// <remarks>
    /// Only ever called for a frame that arrived direct. A frame heard through
    /// a repeater came from behind it, and since nothing here digipeats,
    /// replying direct would go into the ground; the caller drops those rather
    /// than recording a station it cannot answer.
    /// </remarks>
    public void Learn(uint address, Callsign callsign)
    {
        lock (gate)
        {
            entries[address] = new Neighbour(callsign, time.GetUtcNow());
        }
    }

    /// <summary>The station that reaches an address, if one has been heard recently enough.</summary>
    public bool TryGet(uint address, out Neighbour? neighbour)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(address, out neighbour))
                return false;

            if (time.GetUtcNow() - neighbour.LearnedAt <= Lifetime)
                return true;

            entries.Remove(address);
            neighbour = null;
            return false;
        }
    }

    /// <summary>How many live entries there are, for the shutdown summary.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                var now = time.GetUtcNow();
                return entries.Values.Count(n => now - n.LearnedAt <= Lifetime);
            }
        }
    }
}
