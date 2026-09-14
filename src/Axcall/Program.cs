using System.Globalization;
using System.Reflection;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.Serial;
using Packet.Ax25.Transport;

namespace Axcall;

/// <summary>Everything the command line settled, once it parsed cleanly.</summary>
/// <remarks>
/// The link parameters from <see cref="Window"/> to <see cref="AckDelay"/> are
/// null when neither a flag nor the ports file gave them, so the library's own
/// default applies and axcall never restates it.
/// </remarks>
internal sealed record ParsedArgs(
    Callsign MyCall,
    TransportSpec Transport,
    int BaudRate,
    bool Listen,
    Callsign? Target,
    bool Mod128,
    TimeSpan Keepalive,
    int? Window,
    int? Paclen,
    int? Retries,
    TimeSpan? Frack,
    TimeSpan? AckDelay,
    bool NoXid);

public static class Program
{
    // Largest --keepalive we accept, in whole seconds. T3 is armed as a
    // System.Threading timer, whose due time is capped at 0xFFFFFFFE ms (about
    // 49.7 days); a larger value would throw when the link came up rather than
    // at the command line, so bound it here where the user can see why.
    internal const int MaxKeepaliveSeconds = 4_294_967;

    // -w (k): I-frames are numbered modulo 8 or 128 and at most modulus-1
    // may be outstanding, so the ceiling follows -m.
    internal const int MaxWindowMod8 = 7;
    internal const int MaxWindowMod128 = 127;

    // -p (N1): neither the XID I-field-length parameter nor the library's
    // segmenter caps this; 1024 is the conventional TNC/BPQ PACLEN ceiling and
    // keeps a frame well inside the KISS decoder's 4096-byte bound. Classic
    // axcall stopped at 500, so this is a superset of what it took.
    internal const int MaxPaclen = 1024;

    // --retries (N2): the conventional 8-bit RETRY ceiling.
    internal const int MaxRetries = 255;

    // --frack (initial T1) and --ack-delay (T2), in seconds.
    internal const double MinFrackSeconds = 0.5;
    internal const double MaxFrackSeconds = 60;
    internal const double MaxAckDelaySeconds = 30;

    public static async Task<int> Main(string[] args)
    {
        // -v is classic axcall's version flag; -V and --version are ours. All
        // three win over everything else on the line, as classic's does.
        if (args.Contains("--version") || args.Contains("-V") || args.Contains("-v"))
        {
            PrintVersion();
            return 0;
        }

        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        if (args.Contains("--help"))
        {
            PrintUsage();
            return 0;
        }

        // -h is the one place we deliberately differ from classic axcall, where
        // it selects slave mode. Alone it means --help, as it does nearly
        // everywhere else. Alongside real arguments it is far more likely to be
        // a ported script asking for slave mode, and printing help and exiting 0
        // would look like a successful call, so that case fails loudly instead.
        if (args.Contains("-h"))
        {
            if (args.Length == 1)
            {
                PrintUsage();
                return 0;
            }
            return Err("-h here means --help, not classic axcall's slave mode; axcall has no screen modes");
        }

        if (ParseArgs(args) is not { } parsed) return 2;

        using var appCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            appCts.Cancel();
        };

        IAx25Transport modem;
        try
        {
            modem = parsed.Transport.IsSerial
                ? KissSerialModem.Open(parsed.Transport.Device!, parsed.BaudRate)
                : await KissTcpClient.ConnectAsync(parsed.Transport.Host!, parsed.Transport.TcpPort, appCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Err($"failed to open modem: {ex.Message}", 3);
        }

        try
        {
            var relayOptions = new SessionRelayOptions
            {
                Mod128 = parsed.Mod128,
                Keepalive = parsed.Keepalive,
                Window = parsed.Window,
                Paclen = parsed.Paclen,
                Retries = parsed.Retries,
                Frack = parsed.Frack,
                AckDelay = parsed.AckDelay,
                NoXid = parsed.NoXid,
            };
            await using var relay = new SessionRelay(modem, parsed.MyCall, options: relayOptions);

            if (parsed.Listen)
            {
                return await relay.ListenAndRelayAsync(appCts.Token).ConfigureAwait(false);
            }
            else
            {
                return await relay.ConnectAndRelayAsync(parsed.Target!.Value, appCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("axcall: interrupted").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            return Err($"fatal: {ex.Message}", 1);
        }
        finally
        {
            if (modem is IAsyncDisposable ad)
                await ad.DisposeAsync().ConfigureAwait(false);
            else if (modem is IDisposable d)
                d.Dispose();
        }
    }

    // Parse and validate the command line. Returns null after printing a usage
    // error to stderr (the caller exits 2). --help and --version are handled
    // before this runs.
    internal static ParsedArgs? ParseArgs(string[] args)
    {
        string? myCallStr = null;
        string? serialArg = null;
        string? tcpArg = null;
        string? baudArg = null;
        bool listen = false;
        var positionals = new List<string>();
        bool mod128 = false;
        long keepaliveSeconds = SessionRelayOptions.DefaultKeepaliveSeconds;
        // The link parameters are collected as given and validated after the
        // loop: -w's ceiling depends on -m wherever that appears, and a
        // ports-file entry can supply -p and -w when the flag did not.
        string? windowArg = null;
        string? paclenArg = null;
        string? retriesArg = null;
        string? frackArg = null;
        string? ackDelayArg = null;
        bool noXid = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                // Classic axcall spellings, same meaning as the kernel version.
                case "-s" or "--mycall":
                    if (++i >= args.Length) return Fail("missing value for -s (expected a callsign)");
                    myCallStr = args[i];
                    break;
                case "-p" or "--paclen":
                    if (++i >= args.Length) return Fail("missing value for -p (expected a paclen)");
                    paclenArg = args[i];
                    break;
                case "-w" or "--window":
                    if (++i >= args.Length) return Fail("missing value for -w (expected a window)");
                    windowArg = args[i];
                    break;
                case "-m":
                    if (++i >= args.Length) return Fail("missing value for -m (expected s or e)");
                    switch (args[i].ToLowerInvariant())
                    {
                        case "s": mod128 = false; break;
                        case "e": mod128 = true; break;
                        default: return Fail($"invalid value for -m: {args[i]} (expected s for modulo 8, e for modulo 128)");
                    }
                    break;

                // Classic spellings accepted and ignored: they select behaviour
                // axcall already has, or behaviour it never had.
                case "-b":
                    // Backoff. Packet.Ax25 has no linear/exponential selector:
                    // T1 is adapted from the measured round trip. The value is
                    // still validated, so "-b 9600" fails rather than being
                    // mistaken for a baud rate.
                    if (++i >= args.Length) return Fail("missing value for -b (expected l or e)");
                    if (args[i].ToLowerInvariant() is not ("l" or "e"))
                        return Fail($"invalid value for -b: {args[i]} (expected l for linear, e for exponential)");
                    break;
                case "-r":      // raw mode: axcall is always raw
                case "-t":      // talk mode: axcall has no screen modes
                case "-R":      // no remote commands: axcall has none to disable
                case "-8":      // UTF-8: axcall is always UTF-8
                    break;

                // Classic spellings we cannot honour, each failing with its own
                // reason rather than a bare "unknown option".
                case "-i":
                    return Fail("-i (IBM850) is not supported; axcall is UTF-8 only");
                case "-T":
                    return Fail("-T (idle timeout) is not implemented yet; see issue #31");
                case "-W":
                    return Fail("-W (wait for remote disconnect) is not implemented yet; see issue #32");
                case "-S":
                    return Fail("-S (silent) is not implemented yet; see issue #33");
                case "-d":
                    return Fail("-d (frame tracing) is not implemented yet; see issue #34");

                // axcall's own options, long only so the short letters stay
                // free for their classic meanings.
                case "--serial":
                    if (++i >= args.Length) return Fail("missing value for --serial");
                    serialArg = args[i];
                    break;
                case "--tcp":
                    if (++i >= args.Length) return Fail("missing value for --tcp");
                    tcpArg = args[i];
                    break;
                case "--baud":
                    if (++i >= args.Length) return Fail("missing value for --baud");
                    baudArg = args[i];
                    break;
                case "-l" or "--listen":
                    listen = true;
                    break;
                case "--mod128":
                    mod128 = true;
                    break;
                case "--keepalive":
                    if (++i >= args.Length) return Fail("missing value for --keepalive");
                    if (!long.TryParse(args[i], out keepaliveSeconds) || keepaliveSeconds <= 0)
                        return Fail($"invalid keepalive: {args[i]} (must be a positive whole number of seconds)");
                    if (keepaliveSeconds > MaxKeepaliveSeconds)
                        return Fail($"keepalive too large: {args[i]} (max {MaxKeepaliveSeconds} seconds)");
                    break;
                case "--retries":
                    if (++i >= args.Length) return Fail("missing value for --retries");
                    retriesArg = args[i];
                    break;
                case "--frack":
                    if (++i >= args.Length) return Fail("missing value for --frack");
                    frackArg = args[i];
                    break;
                case "--ack-delay":
                    if (++i >= args.Length) return Fail("missing value for --ack-delay");
                    ackDelayArg = args[i];
                    break;
                case "--no-xid":
                    noXid = true;
                    break;
                default:
                    if (args[i].StartsWith('-')) return Fail($"unknown option: {args[i]}");
                    positionals.Add(args[i]);
                    break;
            }
        }

        if (serialArg is not null && tcpArg is not null)
            return Fail("--serial and --tcp are mutually exclusive");

        bool transportFromFlag = serialArg is not null || tcpArg is not null;

        // Positional shape:
        //   <port> <destination>   the classic form
        //   <port>                 with --listen
        //   <destination>          with --serial or --tcp
        //   (none)                 with --listen and --serial or --tcp
        int expectedPositionals = (transportFromFlag ? 0 : 1) + (listen ? 0 : 1);

        if (positionals.Count < expectedPositionals)
        {
            return Fail(!transportFromFlag && positionals.Count == 0
                ? "missing <port>: a name from the ports file, a device path, or host:port"
                : "missing <destination> (or use --listen)");
        }
        if (positionals.Count > expectedPositionals)
        {
            var extra = positionals[expectedPositionals];
            return Fail(listen
                ? $"--listen takes no destination (got '{extra}')"
                : $"digipeater paths are not supported yet (got '{extra}'); see issue #35");
        }

        string? err;
        PortEntry? entry = null;
        TransportSpec? transport;

        if (tcpArg is not null)
        {
            if (!TransportSpec.TryParseEndpoint(tcpArg, out transport, out err)) return Fail($"--tcp: {err}");
        }
        else if (serialArg is not null)
        {
            if (!TransportSpec.TryParseDevice(serialArg, out transport, out err)) return Fail($"--serial: {err}");
        }
        else if (TransportSpec.LooksLikeName(positionals[0]))
        {
            if (!PortsFile.TryResolve(positionals[0], out entry, out err)) return Fail(err!);
            transport = entry!.Transport;
        }
        else
        {
            if (!TransportSpec.TryParse(positionals[0], out transport, out err)) return Fail(err!);
        }

        var destination = listen ? null : positionals[^1];

        // --baud beats a ":baud" suffix beats the library's default, so a flag
        // on the command line always wins over the ports file. It is ignored
        // for a TCP transport rather than refused, so a wrapper script that
        // always passes it still works against either kind of port.
        int baudRate = transport!.Baud ?? KissSerialModem.DefaultBaudRate;
        if (baudArg is not null)
        {
            if (!TryParseCount(baudArg, out var b) || b < 1) return Fail($"invalid baud rate: {baudArg}");
            baudRate = b;
        }

        int maxWindow = mod128 ? MaxWindowMod128 : MaxWindowMod8;

        int? window = null;
        if (windowArg is not null)
        {
            if (!TryParseCount(windowArg, out var w) || w < 1 || w > maxWindow)
                return Fail($"invalid window: {windowArg} (must be 1..{MaxWindowMod8}, or 1..{MaxWindowMod128} with -m e)");
            window = w;
        }
        else if (entry?.Window is { } fileWindow)
        {
            if (fileWindow > maxWindow)
                return Fail($"port '{entry.Name}': window {fileWindow} is above the modulo-{(mod128 ? 128 : 8)} ceiling of {maxWindow}");
            window = fileWindow;
        }

        int? paclen = null;
        if (paclenArg is not null)
        {
            if (!TryParseCount(paclenArg, out var p) || p < 1 || p > MaxPaclen)
                return Fail($"invalid paclen: {paclenArg} (must be 1..{MaxPaclen} bytes)");
            paclen = p;
        }
        else if (entry?.Paclen is { } filePaclen)
        {
            if (filePaclen > MaxPaclen)
                return Fail($"port '{entry.Name}': paclen {filePaclen} is above the ceiling of {MaxPaclen} bytes");
            paclen = filePaclen;
        }

        int? retries = null;
        if (retriesArg is not null)
        {
            if (!TryParseCount(retriesArg, out var r) || r < 1 || r > MaxRetries)
                return Fail($"invalid retries: {retriesArg} (must be 1..{MaxRetries})");
            retries = r;
        }

        TimeSpan? frack = null;
        if (frackArg is not null)
        {
            if (!TryParseSeconds(frackArg, out var f) || f < MinFrackSeconds || f > MaxFrackSeconds)
                return Fail(FormattableString.Invariant($"invalid frack: {frackArg} (must be {MinFrackSeconds}..{MaxFrackSeconds} seconds)"));
            frack = TimeSpan.FromSeconds(f);
        }

        TimeSpan? ackDelay = null;
        if (ackDelayArg is not null)
        {
            if (!TryParseSeconds(ackDelayArg, out var a) || a < 0 || a > MaxAckDelaySeconds)
                return Fail(FormattableString.Invariant($"invalid ack-delay: {ackDelayArg} (must be 0..{MaxAckDelaySeconds} seconds)"));
            ackDelay = TimeSpan.FromSeconds(a);
        }

        // -s beats the port's own callsign, the way it overrode the axports
        // entry under the kernel stack.
        Callsign myCall;
        if (myCallStr is not null)
        {
            if (!Callsign.TryParse(myCallStr.ToUpperInvariant(), out myCall))
                return Fail($"invalid callsign: {myCallStr}");
        }
        else if (entry?.Callsign is { } portCall)
        {
            myCall = portCall;
        }
        else
        {
            return Fail(entry is null
                ? "-s <mycall> is required"
                : $"-s <mycall> is required: port '{entry.Name}' does not name a callsign");
        }

        Callsign? target = null;
        if (destination is not null)
        {
            if (!Callsign.TryParse(destination.ToUpperInvariant(), out var t))
                return Fail($"invalid destination callsign: {destination}");
            target = t;
        }

        return new ParsedArgs(
            myCall, transport, baudRate, listen, target, mod128, TimeSpan.FromSeconds(keepaliveSeconds),
            window, paclen, retries, frack, ackDelay, noXid);
    }

    // A plain unsigned whole number: no sign, whitespace, separators or exponent.
    private static bool TryParseCount(string s, out int value)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    // Seconds with an optional decimal part ("2.5"), always with a '.' whatever
    // the locale; no sign, exponent or separators. NaN and infinity are rejected.
    private static bool TryParseSeconds(string s, out double value)
        => double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
           && double.IsFinite(value);

    private static int Err(string message, int code = 2)
    {
        Console.Error.WriteLine($"axcall: {message}");
        return code;
    }

    // Usage-error form of Err for ParseArgs: report, then hand back "no result".
    private static ParsedArgs? Fail(string message)
    {
        Err(message);
        return null;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine($"""
            Usage: axcall [options] <port> <destination>
                   axcall [options] --listen <port>

              <port>         a name from the ports file, a serial device, or a TCP
                             endpoint:
                               radio               a name from the ports file
                               /dev/ttyUSB0        a serial device, default baud
                               /dev/ttyUSB0:57600  a serial device at that baud
                               10.45.0.66:8001     KISS over TCP
              <destination>  the station to call

            Options, spelled as kernel AX.25 axcall spelled them:
              -s <mycall>            Local callsign. Required unless the port names one.
              -p <paclen>            Largest info field per frame, N1 (PACLEN). 1..{MaxPaclen}
                                     (default: 256). XID may lower it to the peer's.
              -w <window>            Send window, k (MAXFRAME): frames in flight before
                                     an ack is needed. 1..{MaxWindowMod8}, or 1..{MaxWindowMod128} with -m e.
                                     Default 4 on either modulus, after which XID takes
                                     the lower of what each end offers. With SREJ on
                                     the library holds it to half the modulus (4 on
                                     modulo 8), so 5..7 only take effect on a go-back-N
                                     link (--no-xid, or a peer without SREJ).
              -m s|e                 Modulus: s dials with SABM (modulo 8, the default),
                                     e dials with SABME (AX.25 v2.2, modulo 128), falling
                                     back to SABM if the peer answers FRMR or DM.
                                     Inbound sessions (--listen) use whichever the
                                     caller asks for.
              -b l|e                 Backoff. Accepted and ignored: the library adapts T1
                                     from the measured round trip and has no selector.
              -r, -t, -R, -8         Raw mode, talk mode, no remote commands, UTF-8.
                                     Accepted and ignored: axcall is always raw and
                                     always UTF-8, and has no remote commands to disable.
              -v                     Show version info (SDL + runtime libs).
              -h                     Show this help. Classic axcall's -h selects slave
                                     mode; axcall has no screen modes.

            axcall's own options, long only so the letters above keep their meanings:
              --serial <dev[:baud]>  Serial port, instead of a <port> argument.
              --tcp <host:port>      TCP KISS, instead of a <port> argument.
              --baud <rate>          Serial baud rate (default: {KissSerialModem.DefaultBaudRate}). Overrides a
                                     ":baud" suffix; ignored for a TCP port.
              -l, --listen           Wait for an inbound connection instead of calling.
              --mod128               Same as -m e.
              --keepalive <seconds>  Idle-link poll interval, T3 (default: {SessionRelayOptions.DefaultKeepaliveSeconds}).
                                     With no traffic for this long axcall sends an RR
                                     poll to check the link is still up. Must be a
                                     positive whole number; 0 is rejected because the
                                     link layer has no "never poll" setting (a zero
                                     timer would poll continuously). This keeps the link
                                     up; it is not classic axcall's -T, which drops it.
              --retries <n>          Retries before the link is dropped, N2 (RETRY /
                                     RETRIES). 1..{MaxRetries} (default: 10).
              --frack <seconds>      Initial ack timeout, T1 (FRACK). 0.5..60, decimals
                                     allowed (default: 6). Only the starting point: the
                                     library adapts T1 from the measured round trip
                                     after the first exchanges.
              --ack-delay <seconds>  Ack delay, T2 (RESPTIME). 0..30, decimals allowed
                                     (default: 3). Received frames are acknowledged
                                     together once this has elapsed; 0 acknowledges
                                     every frame at once.
              --no-xid               Skip the XID exchange axcall sends before the SABM
                                     on a modulo-8 dial. Without it axcall offers SREJ
                                     (selective retransmit) and its window; with it the
                                     link is plain go-back-N. Outbound only; a -m e
                                     dial the peer accepts negotiates XID after the UA
                                     regardless.
              -V, --version          Same as -v.
              --help                 Same as -h.

            The ports file binds a name to a transport, a callsign and optional link
            defaults, so "axcall radio gb7rdg" works with nothing else on the line.
            Read from {PortsFile.SystemPath} then {PortsFile.UserPath ?? "~/.config/axcall/ports"},
            the second overriding same-named entries in the first, or from the single
            file named by {PortsFile.PathEnvVar} when that is set. Columns, with "-" for
            an unset optional column:

              # name   callsign   transport            paclen  window  description
              radio    M0LTE-7    /dev/ttyUSB0:57600   256     4       144.800 MHz
              node     M0LTE-7    10.45.0.66:8001      -       -       LinBPQ

            Precedence throughout: a command-line flag beats the ports file, which
            beats the library default.
            """);
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"axcall {AsmVersion(typeof(Program))}");
        Console.WriteLine();
        Console.WriteLine("SDL spec tables:");
        Console.WriteLine($"  Packet.Ax25.Sdl     {AsmVersion(typeof(Packet.Ax25.Sdl.TransitionSpec))}");
        Console.WriteLine();
        Console.WriteLine("Runtime libraries:");
        Console.WriteLine($"  Packet.Core         {AsmVersion(typeof(Callsign))}");
        Console.WriteLine($"  Packet.Ax25         {AsmVersion(typeof(Packet.Ax25.Ax25Frame))}");
        Console.WriteLine($"  Packet.Kiss         {AsmVersion(typeof(KissEncoder))}");
        Console.WriteLine($"  Packet.Kiss.Serial  {AsmVersion(typeof(KissSerialModem))}");
    }

    // Read the assembly's informational version (the NuGet package version),
    // trimming any +commit-hash build-metadata suffix SourceLink appends.
    private static string AsmVersion(Type t)
    {
        var asm = t.Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "unknown";
    }
}
