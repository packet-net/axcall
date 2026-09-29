using Packet.Core;

namespace Axlisten;

/// <summary>
/// <c>--call</c>: show only frames that involve one of these stations, as
/// source, destination or digipeater.
/// </summary>
/// <remarks>
/// A callsign given without an SSID matches every SSID, since "show me
/// GB7RDG" nearly always means the station and not only its -0. Write the
/// SSID to mean exactly one, and <c>-0</c> to mean the bare call alone.
/// </remarks>
internal sealed class CallFilter
{
    private readonly List<(string Base, int? Ssid)> calls = [];

    public bool IsEmpty => calls.Count == 0;

    public bool TryAdd(string text)
    {
        var upper = text.ToUpperInvariant();
        if (!Callsign.TryParse(upper, out var call))
            return false;
        calls.Add((call.Base, upper.Contains('-', StringComparison.Ordinal) ? call.Ssid : null));
        return true;
    }

    public bool Matches(MonitoredFrame frame)
    {
        if (IsEmpty)
            return true;
        if (frame.Frame is not { } ax25)
            return false;

        if (Matches(ax25.Source.Callsign) || Matches(ax25.Destination.Callsign))
            return true;
        foreach (var digi in ax25.Digipeaters)
        {
            if (Matches(digi.Callsign))
                return true;
        }
        return false;
    }

    private bool Matches(Callsign call)
    {
        foreach (var (baseCall, ssid) in calls)
        {
            if (baseCall == call.Base && (ssid is null || ssid == call.Ssid))
                return true;
        }
        return false;
    }
}
