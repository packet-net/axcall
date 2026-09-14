using System.Globalization;
using Packet.Core;

namespace Axcall;

/// <summary>One line of the ports file: a name bound to a transport.</summary>
/// <param name="Name">The name the user writes as axcall's first argument.</param>
/// <param name="Callsign">
/// The port's own callsign, used when -s was not given; null when the entry
/// left the column as "-", in which case -s is required.
/// </param>
/// <param name="Transport">The serial device or TCP endpoint to open.</param>
/// <param name="Paclen">N1 default for this port, null when unset.</param>
/// <param name="Window">k default for this port, null when unset.</param>
internal sealed record PortEntry(
    string Name,
    Callsign? Callsign,
    TransportSpec Transport,
    int? Paclen,
    int? Window);

/// <summary>
/// The ports file: whitespace-separated columns binding a short name to a
/// transport, a callsign and optional link defaults.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets <c>axcall radio gb7rdg</c> work with nothing else on the
/// line, the way <c>axcall ax0 gb7rdg</c> did against the kernel stack, where
/// the port name was resolved through /etc/ax25/axports and carried the
/// callsign with it. We cannot reuse axports itself: it names a callsign,
/// paclen and window but no device, because kissattach bound the tty to the
/// port name separately, and that half of the arrangement is gone.
/// </para>
/// <para>
/// Format, with "-" meaning "not set" in any of the optional columns:
/// </para>
/// <code>
/// # name   callsign   transport            paclen  window  description
/// radio    M0LTE-7    /dev/ttyUSB0:57600   256     4       144.800 MHz
/// node     M0LTE-7    10.45.0.66:8001      -       -       LinBPQ
/// </code>
/// <para>
/// The system file is read first and the user file second, so a user entry
/// replaces a system entry of the same name. A malformed line fails the whole
/// load rather than being skipped: a typo in a config file should be reported,
/// not quietly changed into "unknown port".
/// </para>
/// </remarks>
internal static class PortsFile
{
    /// <summary>
    /// Overrides both search paths when set, and is the only path consulted.
    /// Exists so tests can point at a temporary file, and so a script can pin
    /// its own.
    /// </summary>
    internal const string PathEnvVar = "AXCALL_PORTS";

    internal const string SystemPath = "/etc/axcall/ports";

    /// <summary>~/.config/axcall/ports, honouring XDG_CONFIG_HOME.</summary>
    internal static string? UserPath
    {
        get
        {
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(config) ? null : Path.Combine(config, "axcall", "ports");
        }
    }

    /// <summary>The files consulted, in load order, whether or not they exist.</summary>
    internal static IReadOnlyList<string> SearchPaths()
    {
        var pinned = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrEmpty(pinned))
            return [pinned];

        return UserPath is { } user ? [SystemPath, user] : [SystemPath];
    }

    /// <summary>
    /// Look a name up. Returns false with a message in <paramref name="error"/>
    /// for an unreadable or malformed file as well as for a name that is simply
    /// not there; the two read differently, and the "not there" message lists
    /// what is, because a typo in a port name is the likeliest cause.
    /// </summary>
    internal static bool TryResolve(string name, out PortEntry? entry, out string? error)
    {
        entry = null;

        if (!TryLoad(out var entries, out error))
            return false;

        if (entries!.TryGetValue(name, out var found))
        {
            entry = found;
            return true;
        }

        var paths = string.Join(", ", SearchPaths());
        error = entries.Count == 0
            ? $"unknown port '{name}': it is not a device path or host:port, and no ports are configured (looked in {paths})"
            : $"unknown port '{name}' in {paths}; known ports: {string.Join(", ", entries.Keys.Order(StringComparer.Ordinal))}";
        return false;
    }

    /// <summary>
    /// Read every file in <see cref="SearchPaths"/> that exists, later files
    /// replacing same-named entries from earlier ones. A missing file is not an
    /// error; an unreadable or malformed one is.
    /// </summary>
    internal static bool TryLoad(out Dictionary<string, PortEntry>? entries, out string? error)
        => TryLoad(SearchPaths(), out entries, out error);

    /// <summary>
    /// <see cref="TryLoad(out Dictionary{string, PortEntry}, out string)"/>
    /// over an explicit path list, so the merge can be exercised without
    /// standing up the real search paths.
    /// </summary>
    internal static bool TryLoad(IReadOnlyList<string> paths, out Dictionary<string, PortEntry>? entries, out string? error)
    {
        entries = new Dictionary<string, PortEntry>(StringComparer.Ordinal);
        error = null;

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
                entries = null;
                error = $"cannot read {path}: {ex.Message}";
                return false;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryParseLine(lines[i], out var entry, out var lineError))
                {
                    entries = null;
                    error = $"{path}:{i + 1}: {lineError}";
                    return false;
                }
                if (entry is not null)
                    entries[entry.Name] = entry;
            }
        }

        return true;
    }

    /// <summary>
    /// Parse one line. A blank or comment line yields a null entry and true:
    /// nothing to add, nothing wrong.
    /// </summary>
    internal static bool TryParseLine(string line, out PortEntry? entry, out string? error)
    {
        entry = null;
        error = null;

        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#'))
            return true;

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3)
        {
            error = "expected at least 3 columns: name callsign transport";
            return false;
        }

        var name = fields[0];
        if (!TransportSpec.LooksLikeName(name))
        {
            error = $"'{name}' cannot be a port name: a name must not look like a path or host:port";
            return false;
        }

        Callsign? callsign = null;
        if (fields[1] != "-")
        {
            if (!Callsign.TryParse(fields[1].ToUpperInvariant(), out var parsed))
            {
                error = $"invalid callsign '{fields[1]}'";
                return false;
            }
            callsign = parsed;
        }

        if (!TransportSpec.TryParse(fields[2], out var transport, out var transportError))
        {
            error = transportError;
            return false;
        }

        if (!TryOptionalCount(fields, 3, "paclen", out var paclen, out error))
            return false;
        if (!TryOptionalCount(fields, 4, "window", out var window, out error))
            return false;

        // Anything past the window column is a free-text description, kept in
        // the file for the reader's benefit and ignored here.
        entry = new PortEntry(name, callsign, transport!, paclen, window);
        return true;
    }

    // An optional numeric column: absent, "-", or a positive whole number. The
    // range checks belong to the command line, which applies the same ceilings
    // to -p and -w whatever the value's source.
    private static bool TryOptionalCount(string[] fields, int index, string what, out int? value, out string? error)
    {
        value = null;
        error = null;

        if (index >= fields.Length || fields[index] == "-")
            return true;

        if (!int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            error = $"invalid {what} '{fields[index]}'";
            return false;
        }

        value = parsed;
        return true;
    }
}
