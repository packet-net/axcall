using Packet.Core;

namespace Axcall;

/// <summary>What to do with an inbound connection.</summary>
public enum InetdAction
{
    /// <summary>Run a program with the session on its standard input and output.</summary>
    Exec,

    /// <summary>Open a TCP connection and pass the bytes through.</summary>
    Tcp,
}

/// <summary>One line of the inetd file: a callsign bound to something to do.</summary>
/// <param name="Callsign">The local callsign a caller has to have called.</param>
/// <param name="Port">
/// The port this applies to, or null when the entry left the column as "-",
/// meaning whichever port the service was started on.
/// </param>
/// <param name="Action">Run a program, or forward to a TCP port.</param>
/// <param name="Target">The program to run, or the host:port to forward to.</param>
/// <param name="Arguments">
/// Arguments for the program, before substitution. <c>%r</c> stands for the
/// calling station.
/// </param>
public sealed record InetdRule(
    Callsign Callsign,
    string? Port,
    InetdAction Action,
    string Target,
    IReadOnlyList<string> Arguments);

/// <summary>
/// The inetd file: which callsign a caller reached, and what should answer.
/// </summary>
/// <remarks>
/// <para>
/// This is what <c>ax25d</c> was, and it is the inbound half of making the
/// packet network reachable from any language. With <c>exec</c>, writing a
/// packet service becomes writing a program that reads standard input.
/// </para>
/// <code>
/// # callsign   port    action
/// M0LTE-1      radio   exec  /usr/local/bin/bbs --user %r
/// M0LTE-2      radio   tcp   127.0.0.1:8080
/// </code>
/// <para>
/// The callsign is the address, so it is also the way a station decides what to
/// run: there are no port numbers on this side. <c>%r</c> in an argument is
/// replaced with the calling station, which is the only thing a service knows
/// about who is on the other end.
/// </para>
/// <para>
/// Arguments are split on whitespace and there is no quoting. An argument that
/// needs a space in it wants a wrapper script, which is clearer anyway than a
/// line in a config file that has grown its own shell.
/// </para>
/// <para>
/// Same conventions as the ports file: system first, user second, later entries
/// replacing earlier ones for the same callsign, and a malformed line failing
/// the whole load rather than being skipped.
/// </para>
/// </remarks>
public static class InetdFile
{
    /// <summary>Overrides both search paths when set, and is the only path consulted.</summary>
    public const string PathEnvVar = "AXCALL_INETD";

    public const string SystemPath = "/etc/axcall/inetd";

    /// <summary>~/.config/axcall/inetd, honouring XDG_CONFIG_HOME.</summary>
    public static string? UserPath
    {
        get
        {
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(config) ? null : Path.Combine(config, "axcall", "inetd");
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
    public static bool TryLoad(out Dictionary<Callsign, InetdRule>? rules, out string? error)
        => TryLoad(SearchPaths(), out rules, out error);

    /// <summary>
    /// <see cref="TryLoad(out Dictionary{Callsign, InetdRule}, out string)"/>
    /// over an explicit path list.
    /// </summary>
    public static bool TryLoad(IReadOnlyList<string> paths, out Dictionary<Callsign, InetdRule>? rules, out string? error)
    {
        rules = [];
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
                rules = null;
                error = $"cannot read {path}: {ex.Message}";
                return false;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryParseLine(lines[i], out var rule, out var lineError))
                {
                    rules = null;
                    error = $"{path}:{i + 1}: {lineError}";
                    return false;
                }
                if (rule is not null)
                    rules[rule.Callsign] = rule;
            }
        }

        return true;
    }

    /// <summary>
    /// Parse one line. A blank or comment line yields a null rule and true:
    /// nothing to add, nothing wrong.
    /// </summary>
    public static bool TryParseLine(string line, out InetdRule? rule, out string? error)
    {
        rule = null;
        error = null;

        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#'))
            return true;

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4)
        {
            error = "expected at least 4 columns: callsign port action target";
            return false;
        }

        if (!Callsign.TryParse(fields[0].ToUpperInvariant(), out var callsign))
        {
            error = $"invalid callsign '{fields[0]}'";
            return false;
        }

        string? port = null;
        if (fields[1] != "-")
        {
            port = fields[1];
            if (!TransportSpec.LooksLikeName(port))
            {
                error = $"'{port}' cannot be a port name: a name must not look like a path or host:port";
                return false;
            }
        }

        switch (fields[2])
        {
            case "exec":
                var program = fields[3];
                if (!Path.IsPathRooted(program))
                {
                    // An unqualified name would be resolved against whatever
                    // PATH the service happened to inherit, which is not a
                    // thing to leave to chance when the trigger is a stranger
                    // calling in.
                    error = $"exec needs an absolute path, not '{program}'";
                    return false;
                }
                rule = new InetdRule(callsign, port, InetdAction.Exec, program, fields[4..]);
                return true;

            case "tcp":
                if (!TransportSpec.TryParseEndpoint(fields[3], out _, out var endpointError))
                {
                    error = $"tcp: {endpointError}";
                    return false;
                }
                if (fields.Length > 4)
                {
                    error = "tcp takes one host:port and nothing else";
                    return false;
                }
                rule = new InetdRule(callsign, port, InetdAction.Tcp, fields[3], []);
                return true;

            default:
                error = $"unknown action '{fields[2]}': want exec or tcp";
                return false;
        }
    }

    /// <summary>
    /// Fill in what a rule's arguments say about the caller: <c>%r</c> becomes
    /// the calling station.
    /// </summary>
    public static string[] ExpandArguments(InetdRule rule, Callsign caller)
    {
        var who = caller.ToString();
        var expanded = new string[rule.Arguments.Count];
        for (int i = 0; i < expanded.Length; i++)
            expanded[i] = rule.Arguments[i].Replace("%r", who, StringComparison.Ordinal);
        return expanded;
    }
}
