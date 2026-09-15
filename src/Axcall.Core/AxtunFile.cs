using Packet.Core;

namespace Axcall;

/// <summary>One route: an address range, and the station that reaches it.</summary>
/// <param name="Prefix">The range this route covers.</param>
/// <param name="Callsign">The station to address frames to.</param>
public sealed record IpRoute(IpPrefix Prefix, Callsign Callsign)
{
    public override string ToString() => $"{Prefix} -> {Callsign}";
}

/// <summary>Everything the axtun config file holds: where to send, and what may go.</summary>
public sealed record AxtunConfig(IReadOnlyList<IpRoute> Routes, IReadOnlyList<EgressRule> EgressRules)
{
    public static readonly AxtunConfig Empty = new([], []);

    /// <summary>
    /// The station that reaches an address: the most specific route that
    /// covers it, or none.
    /// </summary>
    /// <remarks>
    /// Longest prefix wins, as it does in every routing table, so a host route
    /// beats the subnet it sits in and the subnet beats the default. Routes are
    /// held in the order they were read and searched linearly, which is the
    /// right trade for a table that is a handful of lines long and a channel
    /// that moves a frame a second.
    /// </remarks>
    public IpRoute? Lookup(uint address)
    {
        IpRoute? best = null;
        foreach (var route in Routes)
        {
            if (!route.Prefix.Contains(address))
                continue;
            if (best is null || route.Prefix.Length > best.Prefix.Length)
                best = route;
        }
        return best;
    }

    /// <summary>The egress policy these rules describe, or the default when there are none.</summary>
    public EgressFilter Filter => EgressRules.Count == 0 ? EgressFilter.Default : new EgressFilter(EgressRules);
}

/// <summary>
/// The axtun file: which station reaches which addresses, and what is allowed
/// out of the radio.
/// </summary>
/// <remarks>
/// <para>
/// A TUN device is NOARP: the kernel hands a packet straight to us and never
/// asks who owns the address. So the outbound half of this is not an
/// optimisation, it is the whole of how a packet finds a station, and it is
/// static on purpose. Deciding at run time which transmitter to key, from
/// something a stranger said on the air, is not a decision to hand to a
/// discovery protocol.
/// </para>
/// <code>
/// # what reaches where
/// route 44.131.20.2      PN0TST
/// route 44.131.91.0/24   GB7RDG-1
/// route default          GB7RDG-1
///
/// # what may go out (omit these and the default policy applies)
/// allow icmp
/// allow tcp to 44.0.0.0/8
/// allow udp to 44.0.0.0/8 port 123
/// </code>
/// <para>
/// The inbound half is the opposite: we answer ARP for our own address, so a
/// station that has not been told about us can still find us. See
/// <see cref="AxArpMessage"/>.
/// </para>
/// <para>
/// Same conventions as the other config files here: system first, user second,
/// and a malformed line failing the whole load rather than being skipped.
/// </para>
/// </remarks>
public static class AxtunFile
{
    /// <summary>Overrides both search paths when set, and is the only path consulted.</summary>
    public const string PathEnvVar = "AXCALL_AXTUN";

    public const string SystemPath = "/etc/axcall/axtun";

    /// <summary>~/.config/axcall/axtun, honouring XDG_CONFIG_HOME.</summary>
    public static string? UserPath
    {
        get
        {
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(config) ? null : Path.Combine(config, "axcall", "axtun");
        }
    }

    /// <summary>The files consulted, in load order, whether or not they exist.</summary>
    public static IReadOnlyList<string> SearchPaths()
    {
        var pinned = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrEmpty(pinned))
            return [pinned];

        return UserPath is { } user ? [SystemPath, user] : [SystemPath];
    }

    /// <summary>
    /// Read every file in <see cref="SearchPaths"/> that exists. A missing file
    /// is not an error; an unreadable or malformed one is.
    /// </summary>
    public static bool TryLoad(out AxtunConfig? config, out string? error)
        => TryLoad(SearchPaths(), out config, out error);

    /// <summary>
    /// <see cref="TryLoad(out AxtunConfig, out string)"/> over an explicit path list.
    /// </summary>
    public static bool TryLoad(IReadOnlyList<string> paths, out AxtunConfig? config, out string? error)
    {
        config = null;
        error = null;

        var routes = new List<IpRoute>();
        var egress = new List<EgressRule>();

        foreach (var path in paths)
        {
            string[] lines;
            try
            {
                if (!File.Exists(path))
                    continue;
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"cannot read {path}: {ex.Message}";
                return false;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryParseLine(lines[i], out var route, out var rule, out var lineError))
                {
                    error = $"{path}:{i + 1}: {lineError}";
                    return false;
                }

                if (route is not null)
                {
                    // A second route for the same range replaces the first, so
                    // a user file can correct a system one.
                    routes.RemoveAll(r => r.Prefix == route.Prefix);
                    routes.Add(route);
                }
                if (rule is not null)
                    egress.Add(rule);
            }
        }

        config = new AxtunConfig(routes, egress);
        return true;
    }

    /// <summary>
    /// Parse one line. A blank or comment line yields nothing and true.
    /// </summary>
    public static bool TryParseLine(string line, out IpRoute? route, out EgressRule? rule, out string? error)
    {
        route = null;
        rule = null;
        error = null;

        var text = line.Trim();
        var comment = text.IndexOf('#', StringComparison.Ordinal);
        if (comment >= 0)
            text = text[..comment].Trim();
        if (text.Length == 0)
            return true;

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        switch (fields[0].ToLowerInvariant())
        {
            case "route":
                return TryParseRoute(fields, out route, out error);

            case "allow" or "deny":
                if (!EgressFilter.TryParseRule(text, out rule, out error))
                    return false;
                return true;

            default:
                error = $"'{fields[0]}' is not a keyword: want 'route', 'allow' or 'deny'";
                return false;
        }
    }

    private static bool TryParseRoute(string[] fields, out IpRoute? route, out string? error)
    {
        route = null;
        error = null;

        if (fields.Length < 3)
        {
            error = "expected: route <prefix|default> <callsign>";
            return false;
        }

        var prefixText = fields[1].Equals("default", StringComparison.OrdinalIgnoreCase) ? "any" : fields[1];
        if (!IpPrefix.TryParse(prefixText, out var prefix, out error))
            return false;

        if (!Callsign.TryParse(fields[2].ToUpperInvariant(), out var callsign))
        {
            error = $"invalid callsign '{fields[2]}'";
            return false;
        }

        if (fields.Length > 3)
        {
            // "via" is refused rather than ignored. Somebody who wrote a
            // digipeater path wants their traffic to go through it, and
            // silently sending direct would be a station that looks configured
            // and is not reachable.
            error = fields[3].Equals("via", StringComparison.OrdinalIgnoreCase)
                ? "digipeater paths are not supported: this carries IP direct, and a route "
                  + "through a digipeater would need layer 2 digipeating, which this suite does not do"
                : $"unexpected '{fields[3]}': a route is a prefix and a callsign";
            return false;
        }

        route = new IpRoute(prefix, callsign);
        return true;
    }

}
