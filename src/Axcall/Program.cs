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
/// null when their flag was not given, so the library's own default applies and
/// axcall never restates it.
/// </remarks>
internal sealed record ParsedArgs(
    Callsign MyCall,
    string? PortName,
    string? TcpArg,
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

    // --window (k): I-frames are numbered modulo 8 or 128 and at most modulus-1
    // may be outstanding, so the ceiling follows --mod128.
    internal const int MaxWindowMod8 = 7;
    internal const int MaxWindowMod128 = 127;

    // --paclen (N1): neither the XID I-field-length parameter nor the library's
    // segmenter caps this; 1024 is the conventional TNC/BPQ PACLEN ceiling and
    // keeps a frame well inside the KISS decoder's 4096-byte bound.
    internal const int MaxPaclen = 1024;

    // --retries (N2): the conventional 8-bit RETRY ceiling.
    internal const int MaxRetries = 255;

    // --frack (initial T1) and --ack-delay (T2), in seconds.
    internal const double MinFrackSeconds = 0.5;
    internal const double MaxFrackSeconds = 60;
    internal const double MaxAckDelaySeconds = 30;

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-V"))
        {
            PrintVersion();
            return 0;
        }

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
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
            if (parsed.TcpArg is not null)
            {
                var (host, port) = ParseHostPort(parsed.TcpArg);
                modem = await KissTcpClient.ConnectAsync(host, port, appCts.Token).ConfigureAwait(false);
            }
            else
            {
                modem = KissSerialModem.Open(parsed.PortName!, parsed.BaudRate);
            }
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
        string? portName = null;
        string? tcpArg = null;
        int baudRate = KissSerialModem.DefaultBaudRate;
        bool listen = false;
        string? destination = null;
        bool mod128 = false;
        long keepaliveSeconds = SessionRelayOptions.DefaultKeepaliveSeconds;
        // The link parameters are collected as given and validated after the
        // loop: --window's ceiling depends on --mod128 wherever that appears.
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
                case "-s" or "--mycall":
                    if (++i >= args.Length) return Fail("missing value for --mycall");
                    myCallStr = args[i];
                    break;
                case "-p" or "--port":
                    if (++i >= args.Length) return Fail("missing value for --port");
                    portName = args[i];
                    break;
                case "-t" or "--tcp":
                    if (++i >= args.Length) return Fail("missing value for --tcp");
                    tcpArg = args[i];
                    break;
                case "-b" or "--baud":
                    if (++i >= args.Length) return Fail("missing value for --baud");
                    if (!int.TryParse(args[i], out baudRate)) return Fail($"invalid baud rate: {args[i]}");
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
                case "--window":
                    if (++i >= args.Length) return Fail("missing value for --window");
                    windowArg = args[i];
                    break;
                case "--paclen":
                    if (++i >= args.Length) return Fail("missing value for --paclen");
                    paclenArg = args[i];
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
                    if (destination is not null) return Fail($"unexpected argument: {args[i]}");
                    destination = args[i];
                    break;
            }
        }

        if (myCallStr is null) return Fail("--mycall is required");
        if (portName is null && tcpArg is null) return Fail("one of --port or --tcp is required");
        if (portName is not null && tcpArg is not null) return Fail("--port and --tcp are mutually exclusive");
        if (!listen && destination is null) return Fail("<destination> is required (or use --listen)");
        if (listen && destination is not null) return Fail("--listen and <destination> are mutually exclusive");

        int? window = null;
        if (windowArg is not null)
        {
            int max = mod128 ? MaxWindowMod128 : MaxWindowMod8;
            if (!TryParseCount(windowArg, out var w) || w < 1 || w > max)
                return Fail($"invalid window: {windowArg} (must be 1..{MaxWindowMod8}, or 1..{MaxWindowMod128} with --mod128)");
            window = w;
        }

        int? paclen = null;
        if (paclenArg is not null)
        {
            if (!TryParseCount(paclenArg, out var p) || p < 1 || p > MaxPaclen)
                return Fail($"invalid paclen: {paclenArg} (must be 1..{MaxPaclen} bytes)");
            paclen = p;
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

        if (!Callsign.TryParse(myCallStr.ToUpperInvariant(), out var myCall))
            return Fail($"invalid callsign: {myCallStr}");

        Callsign? target = null;
        if (destination is not null)
        {
            if (!Callsign.TryParse(destination.ToUpperInvariant(), out var t))
                return Fail($"invalid destination callsign: {destination}");
            target = t;
        }

        return new ParsedArgs(
            myCall, portName, tcpArg, baudRate, listen, target, mod128, TimeSpan.FromSeconds(keepaliveSeconds),
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

    private static (string Host, int Port) ParseHostPort(string arg)
    {
        var lastColon = arg.LastIndexOf(':');
        if (lastColon <= 0)
            throw new ArgumentException($"expected host:port, got: {arg}");
        var host = arg[..lastColon];
        if (!int.TryParse(arg[(lastColon + 1)..], out var port) || port is < 1 or > 65535)
            throw new ArgumentException($"invalid port in: {arg}");
        return (host, port);
    }

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
            Usage: axcall <destination> -s <mycall> -p <serial-port>
                   axcall <destination> -s <mycall> -t <host:port>
                   axcall -l -s <mycall> -p <serial-port>
                   axcall -l -s <mycall> -t <host:port>

            Options:
              -s, --mycall <call>    Local callsign (required)
              -p, --port <port>      Serial port (e.g. /dev/ttyUSB0)
              -t, --tcp <host:port>  TCP KISS (e.g. localhost:8001)
              -b, --baud <rate>      Serial baud rate (default: 57600)
              -l, --listen           Wait for inbound connection
              --mod128               Dial with SABME (AX.25 v2.2, modulo 128), falling
                                     back to SABM if the peer answers FRMR or DM. Off by
                                     default: axcall dials with plain SABM (modulo 8).
                                     Inbound sessions (--listen) use whichever the
                                     caller asks for.
              --keepalive <seconds>  Idle-link poll interval, T3 (default: {SessionRelayOptions.DefaultKeepaliveSeconds}).
                                     With no traffic for this long axcall sends an RR
                                     poll to check the link is still up. Must be a
                                     positive whole number; 0 is rejected because the
                                     link layer has no "never poll" setting (a zero
                                     timer would poll continuously).
              --window <n>           Send window, k (MAXFRAME): frames in flight before
                                     an ack is needed. 1..{MaxWindowMod8}, or 1..{MaxWindowMod128} with --mod128.
                                     Default 4 on either modulus, after which XID takes
                                     the lower of what each end offers. With SREJ on
                                     the library holds it to half the modulus (4 on
                                     modulo 8), so 5..7 only take effect on a go-back-N
                                     link (--no-xid, or a peer without SREJ).
              --paclen <bytes>       Largest info field per frame, N1 (PACLEN). 1..{MaxPaclen}
                                     (default: 256). XID may lower it to the peer's.
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
                                     link is plain go-back-N. Outbound only; a --mod128
                                     dial the peer accepts negotiates XID after the UA
                                     regardless.
              -V, --version          Show version info (SDL + runtime libs)
              -h, --help             Show this help
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
