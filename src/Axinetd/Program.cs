using System.Globalization;
using System.Reflection;
using Axcall;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.Serial;

namespace Axinetd;

/// <summary>
/// axinetd: answers inbound AX.25 calls by running a program with the session
/// on its standard input and output, or by forwarding to a local TCP service.
/// </summary>
internal static class Program
{
    internal const int MaxPaclen = 256;
    internal const int MaxWindowMod8 = 7;
    internal const int MaxWindowMod128 = 63;
    internal const double MaxKeepaliveSeconds = 86400;

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-v") || args.Contains("-V"))
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
            if (parsed.Channel.Any)
            {
                if (modem is not ICsmaChannelParams csma)
                    return Err($"this transport cannot set KISS channel parameters: {parsed.Transport}", 2);

                await parsed.Channel.ApplyAsync(csma, appCts.Token).ConfigureAwait(false);
            }

            var inbox = new SessionInbox();
            var listener = new Ax25Listener(modem, new Ax25ListenerOptions
            {
                MyCall = parsed.Callsigns[0],
                T3 = parsed.Keepalive,
                PreferExtendedConnect = parsed.Mod128,
                K = parsed.Window,
                PreConnectXidNegotiatesSrej = !parsed.NoXid,
                ConfigureSession = inbox.Attach,
            });
            foreach (var alias in parsed.Callsigns.Skip(1))
                listener.AddLocalAlias(alias);

            if (parsed.Paclen is { } paclen)
            {
                listener.UpdateSessionParameters(listener.CurrentSessionParameters with { N1 = paclen });
            }
            if (parsed.TraceFrames)
            {
                listener.FrameTraced += (_, e) => Console.Error.WriteLine(FrameTrace.Format(e));
            }

            await using (listener.ConfigureAwait(false))
            {
                var server = new InboundServer(listener, inbox, parsed.Rules, Console.Error, parsed.Quiet);
                server.Start(appCts.Token);

                await listener.StartAsync(appCts.Token).ConfigureAwait(false);
                listener.AcceptIncoming = true;

                if (!parsed.Quiet)
                {
                    Console.Error.WriteLine(
                        $"axinetd: answering for {string.Join(", ", parsed.Callsigns)} on {parsed.PortName}");
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, appCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                listener.AcceptIncoming = false;
                await server.DrainAsync().ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
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

    internal sealed record ParsedArgs(
        IReadOnlyList<Callsign> Callsigns,
        IReadOnlyDictionary<Callsign, InetdRule> Rules,
        TransportSpec Transport,
        string PortName,
        int BaudRate,
        int? Paclen,
        int? Window,
        bool Mod128,
        bool NoXid,
        TimeSpan Keepalive,
        ChannelParams Channel,
        bool TraceFrames,
        bool Quiet);

    internal static ParsedArgs? ParseArgs(string[] args)
    {
        string? configArg = null;
        string? serialArg = null, tcpArg = null, baudArg = null;
        string? paclenArg = null, windowArg = null, keepaliveArg = null;
        string? txdelayArg = null, persistArg = null, slottimeArg = null, txtailArg = null;
        bool mod128 = false, noXid = false, trace = false, quiet = false, allowRoot = false;
        var positionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-c" or "--config": if (!Next(args, ref i, arg, out configArg)) return null; break;
                case "--serial": if (!Next(args, ref i, arg, out serialArg)) return null; break;
                case "--tcp": if (!Next(args, ref i, arg, out tcpArg)) return null; break;
                case "--baud": if (!Next(args, ref i, arg, out baudArg)) return null; break;
                case "-p" or "--paclen": if (!Next(args, ref i, arg, out paclenArg)) return null; break;
                case "-w" or "--window": if (!Next(args, ref i, arg, out windowArg)) return null; break;
                case "--keepalive": if (!Next(args, ref i, arg, out keepaliveArg)) return null; break;
                case "--txdelay": if (!Next(args, ref i, arg, out txdelayArg)) return null; break;
                case "--persist": if (!Next(args, ref i, arg, out persistArg)) return null; break;
                case "--slottime": if (!Next(args, ref i, arg, out slottimeArg)) return null; break;
                case "--txtail": if (!Next(args, ref i, arg, out txtailArg)) return null; break;
                case "--mod128": mod128 = true; break;
                case "--no-xid": noXid = true; break;
                case "--allow-root": allowRoot = true; break;
                case "-d": trace = true; break;
                case "-q" or "--quiet": quiet = true; break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                        return Fail($"unknown option: {arg}");
                    positionals.Add(arg);
                    break;
            }
        }

        // axinetd runs programs chosen by a config file whenever a stranger
        // calls in, and it has no way to drop privileges before it does. As
        // root that turns one bad line into a root shell for anyone with a
        // radio, so it is refused unless the operator says otherwise.
        if (Environment.IsPrivilegedProcess && !allowRoot)
        {
            return Fail("refusing to run as root: axinetd runs programs on behalf of whoever calls in, "
                + "and cannot drop privileges first. Run it as a dedicated unprivileged user, "
                + "or pass --allow-root if you are certain.");
        }

        var transportFromFlag = serialArg is not null || tcpArg is not null;
        if (positionals.Count == 0 && !transportFromFlag)
            return Fail("missing <port>: a name from the ports file, a device path, or host:port");
        if (positionals.Count > (transportFromFlag ? 0 : 1))
            return Fail($"unexpected argument '{positionals[transportFromFlag ? 0 : 1]}'");

        string? err;
        PortEntry? entry = null;
        TransportSpec? transport;
        var portName = transportFromFlag ? (serialArg ?? tcpArg)! : positionals[0];

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

        var configPaths = configArg is not null ? (IReadOnlyList<string>)[configArg] : InetdFile.SearchPaths();
        if (!InetdFile.TryLoad(configPaths, out var allRules, out err)) return Fail(err!);

        // Rules naming another port belong to another instance. Ordered so the
        // callsign the listener takes as its own is the same one every time.
        var rules = allRules!
            .Where(r => r.Value.Port is null || string.Equals(r.Value.Port, portName, StringComparison.Ordinal))
            .OrderBy(r => r.Key.ToString(), StringComparer.Ordinal)
            .ToDictionary(r => r.Key, r => r.Value);

        if (rules.Count == 0)
        {
            return Fail($"nothing to answer for on port '{portName}': no rules in "
                + $"{string.Join(", ", configPaths)}");
        }

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
                return Fail($"invalid window: {windowArg} (must be 1..{MaxWindowMod8}, or 1..{MaxWindowMod128} with --mod128)");
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

        var keepalive = TimeSpan.FromSeconds(SessionRelayOptions.DefaultKeepaliveSeconds);
        if (keepaliveArg is not null)
        {
            if (!double.TryParse(keepaliveArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || seconds <= 0 || seconds > MaxKeepaliveSeconds)
                return Fail($"invalid keepalive: {keepaliveArg} (seconds, 0 < n <= {MaxKeepaliveSeconds})");
            keepalive = TimeSpan.FromSeconds(seconds);
        }

        if (!TryChannelParams(txdelayArg, persistArg, slottimeArg, txtailArg, out var channel, out err))
            return Fail(err!);

        channel = (entry?.Channel ?? ChannelParams.None).OverriddenBy(channel);

        return new ParsedArgs([.. rules.Keys], rules, transport, portName, baudRate, paclen, window,
            mod128, noXid, keepalive, channel, trace, quiet);
    }

    private static bool TryChannelParams(
        string? txdelay, string? persist, string? slottime, string? txtail,
        out ChannelParams channel, out string? error)
    {
        channel = ChannelParams.None;
        byte? txDelayValue = null, persistValue = null, slotTimeValue = null, txTailValue = null;

        if (txdelay is not null)
        {
            if (!ChannelParams.TryParseTimerMs(txdelay, out var v, out error)) return FailOut($"--txdelay: {error}", out error);
            txDelayValue = v;
        }
        if (persist is not null)
        {
            if (!ChannelParams.TryParsePersist(persist, out var v, out error)) return FailOut($"--persist: {error}", out error);
            persistValue = v;
        }
        if (slottime is not null)
        {
            if (!ChannelParams.TryParseTimerMs(slottime, out var v, out error)) return FailOut($"--slottime: {error}", out error);
            slotTimeValue = v;
        }
        if (txtail is not null)
        {
            if (!ChannelParams.TryParseTimerMs(txtail, out var v, out error)) return FailOut($"--txtail: {error}", out error);
            txTailValue = v;
        }

        channel = new ChannelParams(txDelayValue, persistValue, slotTimeValue, txTailValue);
        error = null;
        return true;
    }

    private static bool FailOut(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static bool Next(string[] args, ref int i, string option, out string? value)
    {
        if (i + 1 >= args.Length)
        {
            value = null;
            Fail($"{option} needs a value");
            return false;
        }
        value = args[++i];
        return true;
    }

    private static bool TryParseCount(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static ParsedArgs? Fail(string message)
    {
        Err(message);
        return null;
    }

    private static int Err(string message, int code = 2)
    {
        Console.Error.WriteLine($"axinetd: {message}");
        return code;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            Usage: axinetd [options] <port>

            Answers inbound AX.25 calls. Each call runs a program with the session on
            its standard input and output, or is forwarded to a local TCP service.
            Writing a packet service becomes writing a program that reads stdin.

              <port>                 A name from the ports file, a device path, or
                                     host:port for a KISS-over-TCP modem.

            Options:
              -c, --config <file>    Rules file to use instead of the usual search.
                  --serial <dev[:baud]>  Serial KISS TNC, instead of a <port>.
                  --tcp <host:port>  KISS over TCP, instead of a <port>.
                  --baud <rate>      Serial baud rate (default: {KissSerialModem.DefaultBaudRate}).
              -p, --paclen <bytes>   N1, 1..{MaxPaclen}.
              -w, --window <frames>  k, 1..{MaxWindowMod8} (1..{MaxWindowMod128} with --mod128).
                  --mod128           Prefer AX.25 v2.2 (SABME) on each link.
                  --no-xid           Skip pre-connect XID negotiation.
                  --keepalive <s>    T3 (default {SessionRelayOptions.DefaultKeepaliveSeconds}).
                  --txdelay <ms>     KISS channel access, as kissparms(8) set it.
                  --persist <0-255>
                  --slottime <ms>
                  --txtail <ms>
                  --allow-root       Run as root anyway. Read the warning first.
              -d                     Trace every frame to stderr.
              -q, --quiet            Log errors only.
              -h, --help             This help.
              -v, --version          Version and library versions.

            Rules come from {InetdFile.SystemPath}, then the per-user file. The
            callsign called is the whole address; there are no port numbers on the
            AX.25 side:

              # callsign   port    action
              M0LTE-1      radio   exec  /usr/local/bin/bbs --user %r
              M0LTE-2      radio   tcp   127.0.0.1:8080

            %r is the calling station, also passed as AX25_CALLER in the environment.
            A program's standard error goes to the log, not down the link.

            axinetd will not run as root. It runs programs on behalf of whoever calls
            in and cannot drop privileges first, so one careless line would be a root
            shell for anyone with a radio. Run it as a dedicated unprivileged user.
            """);
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"axinetd {AsmVersion(typeof(Program))}");
        Console.WriteLine();
        Console.WriteLine("Runtime libraries:");
        Console.WriteLine($"  Packet.Core         {AsmVersion(typeof(Callsign))}");
        Console.WriteLine($"  Packet.Ax25         {AsmVersion(typeof(Ax25Listener))}");
        Console.WriteLine($"  Packet.Kiss.Serial  {AsmVersion(typeof(KissSerialModem))}");
    }

    private static string AsmVersion(Type type)
        => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
           ?? type.Assembly.GetName().Version?.ToString()
           ?? "unknown";
}
