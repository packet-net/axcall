using System.Globalization;

namespace Axcall;

/// <summary>One line of egress policy.</summary>
/// <param name="Allow">Whether a packet matching this rule goes out.</param>
/// <param name="Protocol">The IP protocol number, or null for any.</param>
/// <param name="Destination">Where it is going, or <see cref="IpPrefix.Any"/>.</param>
/// <param name="FirstPort">Low end of the destination port range, or null for any.</param>
/// <param name="LastPort">High end of the destination port range, or null for any.</param>
public sealed record EgressRule(
    bool Allow,
    byte? Protocol,
    IpPrefix Destination,
    ushort? FirstPort = null,
    ushort? LastPort = null)
{
    public bool MatchesPort(ushort? port)
    {
        if (FirstPort is null)
            return true;
        if (port is null)
            return false;
        return port >= FirstPort && port <= (LastPort ?? FirstPort);
    }

    public override string ToString()
    {
        var protocol = Protocol switch
        {
            null => "any",
            IpPacket.ProtocolIcmp => "icmp",
            IpPacket.ProtocolTcp => "tcp",
            IpPacket.ProtocolUdp => "udp",
            _ => Protocol.Value.ToString(CultureInfo.InvariantCulture),
        };

        var ports = FirstPort is null ? ""
            : LastPort is null || LastPort == FirstPort ? $" port {FirstPort}"
            : $" port {FirstPort}-{LastPort}";

        return $"{(Allow ? "allow" : "deny")} {protocol} to {Destination}{ports}";
    }
}

/// <summary>
/// What is allowed out of the radio, decided before anything is transmitted.
/// </summary>
/// <remarks>
/// <para>
/// This is not a security feature and it is not a firewall. It is the thing
/// that stops a general purpose operating system using somebody's amateur
/// licence to announce its printers.
/// </para>
/// <para>
/// Put an unfiltered IP interface on a shared radio channel and the machine
/// will transmit, unasked and forever: mDNS, LLMNR, SSDP, NetBIOS, DHCP
/// renewals, IPv6 router solicitations, NTP. None of it was requested by the
/// operator, all of it is sent under their callsign, and on a channel where
/// one full frame takes over a second at 1200 baud it is not harmless noise.
/// So the interface starts closed and is opened deliberately.
/// </para>
/// <para>
/// Rules are tried in order and the first match decides. Nothing matching
/// means the packet does not go out. Two things no ordinary rule can let
/// through: a destination that is multicast, broadcast or link-local
/// (<see cref="IpPrefix.IsChatty"/>) needs a rule that names its range
/// explicitly, because a wildcard rule was written by somebody thinking about
/// unicast; and anything that is not IPv4 never reaches the filter at all,
/// because a packet whose shape is not understood cannot be judged.
/// </para>
/// </remarks>
public sealed class EgressFilter
{
    private readonly EgressRule[] rules;

    public EgressFilter(IReadOnlyList<EgressRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = [.. rules];
    }

    /// <summary>The rules, in the order they are tried.</summary>
    public IReadOnlyList<EgressRule> Rules => rules;

    /// <summary>Whether this policy came from the built-in default rather than from config.</summary>
    public bool IsDefault { get; private init; }

    /// <summary>
    /// What is allowed when nothing has been configured: ping, and the two
    /// transport protocols people actually use, to one station at a time, with
    /// the ports that no operator has ever deliberately sent over a radio
    /// taken back out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A default that allowed nothing would be the purest reading of
    /// default-deny and would also mean that every first run of axtun looks
    /// broken, which teaches people to turn the filter off. A default that
    /// allowed everything is the thing this class exists to prevent. This is
    /// the middle: the traffic somebody sets up an IP link in order to use,
    /// and nothing else.
    /// </para>
    /// <para>
    /// The denied ports are the loud ones, listed before the allows so they
    /// win: NetBIOS and SMB (137-139, 445), mDNS (5353), LLMNR (5355), SSDP
    /// (1900), WS-Discovery (3702), DHCP (67-68). These are sent by hosts on
    /// their own initiative and are never what the link was for.
    /// </para>
    /// </remarks>
    public static EgressFilter Default { get; } = new(
    [
        new EgressRule(Allow: false, IpPacket.ProtocolUdp, IpPrefix.Any, 67, 68),
        new EgressRule(Allow: false, IpPacket.ProtocolUdp, IpPrefix.Any, 137, 139),
        new EgressRule(Allow: false, IpPacket.ProtocolTcp, IpPrefix.Any, 137, 139),
        new EgressRule(Allow: false, IpPacket.ProtocolTcp, IpPrefix.Any, 445),
        new EgressRule(Allow: false, IpPacket.ProtocolUdp, IpPrefix.Any, 1900),
        new EgressRule(Allow: false, IpPacket.ProtocolUdp, IpPrefix.Any, 3702),
        new EgressRule(Allow: false, IpPacket.ProtocolUdp, IpPrefix.Any, 5353),
        new EgressRule(Allow: false, IpPacket.ProtocolUdp, IpPrefix.Any, 5355),
        new EgressRule(Allow: true, IpPacket.ProtocolIcmp, IpPrefix.Any),
        new EgressRule(Allow: true, IpPacket.ProtocolTcp, IpPrefix.Any),
        new EgressRule(Allow: true, IpPacket.ProtocolUdp, IpPrefix.Any),
    ])
    { IsDefault = true };

    /// <summary>Deny everything. What <c>--no-egress</c> and an empty rule set both mean.</summary>
    public static EgressFilter DenyAll { get; } = new([]);

    /// <summary>
    /// Decide on one packet. The reason is filled in when the answer is no, so
    /// the operator can see what they would have to allow.
    /// </summary>
    public bool Allows(in IpPacket packet, out string reason)
    {
        var destination = packet.DestinationValue;
        var chatty = IpPrefix.IsChatty(destination);
        var port = packet.DestinationPort;

        foreach (var rule in rules)
        {
            if (rule.Protocol is { } protocol && protocol != packet.Protocol)
                continue;
            if (!rule.Destination.Contains(destination))
                continue;

            // A wildcard destination means "anywhere a packet was going to go
            // anyway", which is not the same as "and also start broadcasting".
            if (chatty && rule.Destination.Length == 0)
                continue;

            // A trailing fragment carries no ports, so a port rule cannot
            // judge it. Allowing it would let a port rule be evaded by
            // fragmentation; denying it is what the reassembled packet would
            // have got if the first fragment was denied.
            if (!rule.MatchesPort(port))
                continue;

            if (rule.Allow)
            {
                reason = "";
                return true;
            }

            reason = $"blocked by '{rule}'";
            return false;
        }

        reason = chatty
            ? "no rule names this multicast or broadcast range, and a wildcard rule does not cover one"
            : "no rule allows it";
        return false;
    }

    /// <summary>A one-line summary for the startup banner.</summary>
    public string Describe()
    {
        if (rules.Length == 0)
            return "egress filter: everything denied";

        var allows = rules.Count(r => r.Allow);
        var denies = rules.Length - allows;
        var origin = IsDefault ? "default policy" : "configured";
        return $"egress filter: {origin}, {allows} allow and {denies} deny rule{(denies == 1 ? "" : "s")}, everything else denied";
    }

    /// <summary>
    /// Parse one rule: <c>allow|deny &lt;protocol&gt; [to &lt;prefix&gt;] [port &lt;n[-m]&gt;]</c>.
    /// </summary>
    /// <remarks>
    /// The protocol is a name (icmp, tcp, udp) or a number, or "any". The
    /// destination defaults to any, the port range to any. The word "to" is
    /// optional but reads better with it.
    /// </remarks>
    public static bool TryParseRule(string text, out EgressRule? rule, out string? error)
    {
        rule = null;
        error = null;

        var fields = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0)
        {
            error = "expected: allow|deny <protocol> [to <prefix>] [port <n[-m]>]";
            return false;
        }

        bool allow;
        switch (fields[0].ToLowerInvariant())
        {
            case "allow": allow = true; break;
            case "deny": allow = false; break;
            default:
                error = $"'{fields[0]}' is not 'allow' or 'deny'";
                return false;
        }

        if (fields.Length < 2)
        {
            error = "missing protocol: a name (icmp, tcp, udp), a number, or 'any'";
            return false;
        }

        if (!TryParseProtocol(fields[1], out var protocol, out error))
            return false;

        var destination = IpPrefix.Any;
        ushort? firstPort = null, lastPort = null;

        for (int i = 2; i < fields.Length; i++)
        {
            switch (fields[i].ToLowerInvariant())
            {
                case "to":
                    if (++i >= fields.Length)
                    {
                        error = "'to' needs a destination";
                        return false;
                    }
                    if (!IpPrefix.TryParse(fields[i], out destination, out error))
                        return false;
                    break;

                case "port":
                    if (++i >= fields.Length)
                    {
                        error = "'port' needs a number or range";
                        return false;
                    }
                    if (protocol is not (IpPacket.ProtocolTcp or IpPacket.ProtocolUdp))
                    {
                        error = "only tcp and udp rules can match on a port";
                        return false;
                    }
                    if (!TryParsePorts(fields[i], out firstPort, out lastPort, out error))
                        return false;
                    break;

                default:
                    error = $"unexpected '{fields[i]}': expected 'to' or 'port'";
                    return false;
            }
        }

        rule = new EgressRule(allow, protocol, destination, firstPort, lastPort);
        return true;
    }

    private static bool TryParseProtocol(string text, out byte? protocol, out string? error)
    {
        protocol = null;
        error = null;

        switch (text.ToLowerInvariant())
        {
            case "any": return true;
            case "icmp": protocol = IpPacket.ProtocolIcmp; return true;
            case "tcp": protocol = IpPacket.ProtocolTcp; return true;
            case "udp": protocol = IpPacket.ProtocolUdp; return true;
        }

        if (byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            protocol = number;
            return true;
        }

        error = $"'{text}' is not a protocol: want icmp, tcp, udp, a number 0..255, or 'any'";
        return false;
    }

    private static bool TryParsePorts(string text, out ushort? first, out ushort? last, out string? error)
    {
        first = last = null;
        error = null;

        if (text.Equals("any", StringComparison.OrdinalIgnoreCase))
            return true;

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var firstText = dash < 0 ? text : text[..dash];

        if (!ushort.TryParse(firstText, NumberStyles.None, CultureInfo.InvariantCulture, out var f) || f == 0)
        {
            error = $"'{firstText}' is not a port in 1..65535";
            return false;
        }
        first = f;

        if (dash < 0)
            return true;

        if (!ushort.TryParse(text[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var l) || l == 0)
        {
            error = $"'{text[(dash + 1)..]}' is not a port in 1..65535";
            return false;
        }
        if (l < f)
        {
            error = $"port range {f}-{l} runs backwards";
            return false;
        }
        last = l;
        return true;
    }
}
