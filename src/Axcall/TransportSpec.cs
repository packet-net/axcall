using System.Globalization;

namespace Axcall;

/// <summary>
/// Where the KISS frames go: a serial device, or a TCP endpoint.
/// </summary>
/// <remarks>
/// Parsed from a port spec, which is whatever the user wrote as the first
/// positional argument or put in the transport column of a ports-file entry.
/// Both accept the same two concrete forms, so a ports file is a naming layer
/// over the spec and nothing more:
/// <list type="bullet">
/// <item><c>/dev/ttyUSB0</c> or <c>/dev/ttyUSB0:57600</c>, a serial device with
/// an optional baud suffix.</item>
/// <item><c>10.45.0.66:8001</c> or <c>[::1]:8001</c>, KISS over TCP.</item>
/// </list>
/// A bare name is neither, and is resolved through <see cref="PortsFile"/>
/// before it reaches here.
/// </remarks>
internal sealed record TransportSpec
{
    private TransportSpec() { }

    /// <summary>Serial device path; null when this is a TCP endpoint.</summary>
    public string? Device { get; private init; }

    /// <summary>
    /// Baud rate carried by the spec's ":baud" suffix, null when it had none.
    /// The caller resolves the effective rate: --baud beats this beats the
    /// library's default.
    /// </summary>
    public int? Baud { get; private init; }

    /// <summary>TCP host; null when this is a serial device.</summary>
    public string? Host { get; private init; }

    /// <summary>TCP port; meaningless when <see cref="Host"/> is null.</summary>
    public int TcpPort { get; private init; }

    public bool IsSerial => Device is not null;

    /// <summary>
    /// True if the spec is meant as a filesystem path rather than a host:port
    /// or a ports-file name. Decided by shape alone, before the file exists:
    /// axcall should report "failed to open modem", not "unknown port", when
    /// /dev/ttyUSB0 is unplugged.
    /// </summary>
    internal static bool LooksLikePath(string spec)
        => spec.StartsWith('/')
           || spec.StartsWith("./", StringComparison.Ordinal)
           || spec.StartsWith("../", StringComparison.Ordinal);

    /// <summary>
    /// True if the spec is a name to look up rather than a transport: anything
    /// that is neither a path nor carries a colon.
    /// </summary>
    internal static bool LooksLikeName(string spec)
        => !LooksLikePath(spec) && !spec.Contains(':', StringComparison.Ordinal);

    /// <summary>
    /// Parse a concrete transport. Returns false with a message in
    /// <paramref name="error"/> that the caller prefixes with its own context
    /// ("--tcp: ...", "/etc/axcall/ports:3: ...").
    /// </summary>
    internal static bool TryParse(string spec, out TransportSpec? result, out string? error)
    {
        result = null;
        error = null;

        if (spec.Length == 0)
        {
            error = "empty port spec";
            return false;
        }

        if (LooksLikePath(spec))
            return TryParseDevice(spec, out result, out error);

        if (spec.Contains(':', StringComparison.Ordinal))
            return TryParseEndpoint(spec, out result, out error);

        error = $"'{spec}' is neither a device path nor host:port";
        return false;
    }

    /// <summary>Parse "/dev/ttyUSB0" or "/dev/ttyUSB0:57600".</summary>
    internal static bool TryParseDevice(string spec, out TransportSpec? result, out string? error)
    {
        result = null;
        error = null;

        var device = spec;
        int? baud = null;

        // Only a trailing all-digits group counts as a baud suffix, and only
        // after the last '/', so a device whose name contains a colon is still
        // reachable as long as it does not end in ":<digits>".
        var colon = spec.LastIndexOf(':');
        if (colon > spec.LastIndexOf('/') && colon >= 0)
        {
            var tail = spec[(colon + 1)..];
            if (tail.Length > 0 && int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                if (parsed <= 0)
                {
                    error = $"invalid baud rate in '{spec}'";
                    return false;
                }
                device = spec[..colon];
                baud = parsed;
            }
        }

        if (device.Length == 0)
        {
            error = $"missing device path in '{spec}'";
            return false;
        }

        result = new TransportSpec { Device = device, Baud = baud };
        return true;
    }

    /// <summary>Parse "host:port", including a bracketed IPv6 literal.</summary>
    internal static bool TryParseEndpoint(string spec, out TransportSpec? result, out string? error)
    {
        result = null;
        error = null;

        string host;
        string portText;

        if (spec.StartsWith('['))
        {
            // [::1]:8001. The brackets disambiguate the literal's own colons
            // and are stripped here, because Socket wants the bare address.
            var close = spec.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= spec.Length || spec[close + 1] != ':')
            {
                error = $"expected [address]:port, got '{spec}'";
                return false;
            }
            host = spec[1..close];
            portText = spec[(close + 2)..];
        }
        else
        {
            var colon = spec.LastIndexOf(':');
            if (colon <= 0)
            {
                error = $"expected host:port, got '{spec}'";
                return false;
            }
            host = spec[..colon];
            portText = spec[(colon + 1)..];
        }

        if (host.Length == 0)
        {
            error = $"missing host in '{spec}'";
            return false;
        }
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            error = $"invalid TCP port in '{spec}'";
            return false;
        }

        result = new TransportSpec { Host = host, TcpPort = port };
        return true;
    }

    /// <summary>The spec as written, for status and error messages.</summary>
    public override string ToString()
        => IsSerial
            ? Baud is { } b ? $"{Device}:{b}" : Device!
            : Host!.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{TcpPort}" : $"{Host}:{TcpPort}";
}
