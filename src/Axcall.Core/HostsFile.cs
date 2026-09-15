using Packet.Core;

namespace Axcall;

/// <summary>One line of the hosts file: a hostname bound to a callsign.</summary>
/// <param name="Name">The name a client asks for, matched case-insensitively.</param>
/// <param name="Callsign">The station to connect to.</param>
/// <param name="Port">
/// The port to reach it over, or null when the entry left the column as "-",
/// meaning whichever port the proxy was started on.
/// </param>
public sealed record HostEntry(string Name, Callsign Callsign, string? Port);

/// <summary>
/// The hosts file: names for stations, so software that only understands
/// hostnames can reach a callsign.
/// </summary>
/// <remarks>
/// <para>
/// This is /etc/hosts for the packet network, and it exists for the same
/// reason: the thing on the other side has an address that people do not want
/// to type, and the software in between has no idea what that address means.
/// A SOCKS client hands over a name; this turns it into a callsign.
/// </para>
/// <code>
/// # name     callsign    port
/// gb7rdg     GB7RDG      radio
/// gb7cip     GB7CIP-1    radio
/// lab        M0LTE-2     -
/// </code>
/// <para>
/// A name that is not in the file but is itself a valid callsign is used as
/// one, so <c>gb7rdg-1</c> works with no configuration at all. That is the
/// common case for a quick test, and it means an empty hosts file is a
/// perfectly good hosts file.
/// </para>
/// <para>
/// Same conventions as the ports file: system first, user second, later
/// entries replacing earlier ones, and a malformed line failing the whole load
/// rather than being skipped.
/// </para>
/// </remarks>
public static class HostsFile
{
    /// <summary>Overrides both search paths when set, and is the only path consulted.</summary>
    public const string PathEnvVar = "AXCALL_HOSTS";

    public const string SystemPath = "/etc/axcall/hosts";

    /// <summary>~/.config/axcall/hosts, honouring XDG_CONFIG_HOME.</summary>
    public static string? UserPath
    {
        get
        {
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(config) ? null : Path.Combine(config, "axcall", "hosts");
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
    public static bool TryLoad(out Dictionary<string, HostEntry>? entries, out string? error)
        => TryLoad(SearchPaths(), out entries, out error);

    /// <summary>
    /// <see cref="TryLoad(out Dictionary{string, HostEntry}, out string)"/> over
    /// an explicit path list.
    /// </summary>
    public static bool TryLoad(IReadOnlyList<string> paths, out Dictionary<string, HostEntry>? entries, out string? error)
    {
        // Hostnames are case-insensitive everywhere else, so they are here too:
        // a client that upper-cases what the user typed still resolves.
        entries = new Dictionary<string, HostEntry>(StringComparer.OrdinalIgnoreCase);
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
    public static bool TryParseLine(string line, out HostEntry? entry, out string? error)
    {
        entry = null;
        error = null;

        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#'))
            return true;

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            error = "expected at least 2 columns: name callsign";
            return false;
        }

        var name = fields[0];
        if (!LooksLikeHostname(name))
        {
            error = $"'{name}' is not a usable hostname: letters, digits, hyphens and dots only";
            return false;
        }

        if (!Callsign.TryParse(fields[1].ToUpperInvariant(), out var callsign))
        {
            error = $"invalid callsign '{fields[1]}'";
            return false;
        }

        string? port = null;
        if (fields.Length > 2 && fields[2] != "-")
        {
            port = fields[2];
            if (!TransportSpec.LooksLikeName(port))
            {
                error = $"'{port}' cannot be a port name: a name must not look like a path or host:port";
                return false;
            }
        }

        entry = new HostEntry(name, callsign, port);
        return true;
    }

    /// <summary>
    /// Turn the name a client asked for into a station to call: the hosts file
    /// first, then the name read as a callsign in its own right.
    /// </summary>
    /// <remarks>
    /// The order matters. An entry has to be able to point <c>gb7rdg</c> at
    /// something other than the station literally called GB7RDG, because a name
    /// is a local choice and a callsign is not.
    /// </remarks>
    public static bool TryResolve(
        IReadOnlyDictionary<string, HostEntry> entries,
        string name,
        out Callsign callsign,
        out string? port,
        out string? error)
    {
        callsign = default;
        port = null;
        error = null;

        if (entries.TryGetValue(name, out var entry))
        {
            callsign = entry.Callsign;
            port = entry.Port;
            return true;
        }

        if (Callsign.TryParse(name.ToUpperInvariant(), out var parsed))
        {
            callsign = parsed;
            return true;
        }

        error = entries.Count == 0
            ? $"no host named '{name}', and it is not a callsign; no hosts are configured (looked in {string.Join(", ", SearchPaths())})"
            : $"no host named '{name}', and it is not a callsign; known hosts: {string.Join(", ", entries.Keys.Order(StringComparer.OrdinalIgnoreCase))}";
        return false;
    }

    // Deliberately narrow: this is the set a hostname can contain and still
    // survive every client between the user and us. Anything else is a typo or
    // an attempt to smuggle something through, and either way it is not a host.
    private static bool LooksLikeHostname(string name)
    {
        if (name.Length == 0 || name.Length > 253)
            return false;

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '.')
                return false;
        }

        return name[0] != '-' && name[0] != '.';
    }
}
