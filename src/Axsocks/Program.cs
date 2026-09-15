using System.Globalization;
using System.Net;
using System.Reflection;
using Axcall;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss.Serial;
using Packet.Kiss;

namespace Axsocks;

/// <summary>
/// axsocks: a SOCKS5 proxy in front of one AX.25 port, so software that speaks
/// sockets can reach a station without knowing what AX.25 is.
/// </summary>
internal static class Program
{
    internal const int DefaultSocksPort = 1080;
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
            // Channel access before anything else goes out, so the first frame
            // is keyed with the settings that were asked for rather than the
            // ones the TNC happened to boot with.
            if (parsed.Channel.Any)
            {
                if (modem is not ICsmaChannelParams csma)
                    return Err($"this transport cannot set KISS channel parameters: {parsed.Transport}", 2);

                await parsed.Channel.ApplyAsync(csma, appCts.Token).ConfigureAwait(false);
            }

            var inbox = new SessionInbox();
            var listener = new Ax25Listener(modem, new Ax25ListenerOptions
            {
                MyCall = parsed.MyCall,
                T3 = parsed.Keepalive,
                PreferExtendedConnect = parsed.Mod128,
                K = parsed.Window,
                PreConnectXidNegotiatesSrej = !parsed.NoXid,
                ConfigureSession = inbox.Attach,
            });
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
                await listener.StartAsync(appCts.Token).ConfigureAwait(false);

                // Outbound only. Accepting inbound sessions is axinetd's job,
                // and a proxy that silently answered calls would be a surprise.
                listener.AcceptIncoming = false;

                var server = new ProxyServer(listener, inbox, parsed.Hosts, parsed.PortName, Console.Error, parsed.Quiet);
                await server.RunAsync(parsed.Bind, appCts.Token).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return Err($"cannot listen on {parsed.Bind}: {ex.Message}", 3);
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
        Callsign MyCall,
        TransportSpec Transport,
        string PortName,
        int BaudRate,
        IPEndPoint Bind,
        IReadOnlyDictionary<string, HostEntry> Hosts,
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
        string? mycall = null, listenArg = null, hostsArg = null;
        string? serialArg = null, tcpArg = null, baudArg = null;
        string? paclenArg = null, windowArg = null, keepaliveArg = null;
        string? txdelayArg = null, persistArg = null, slottimeArg = null, txtailArg = null;
        bool mod128 = false, noXid = false, trace = false, quiet = false;
        var positionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-s" or "--mycall": if (!Next(args, ref i, arg, out mycall)) return null; break;
                case "--listen": if (!Next(args, ref i, arg, out listenArg)) return null; break;
                case "--hosts": if (!Next(args, ref i, arg, out hostsArg)) return null; break;
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
                case "-d": trace = true; break;
                case "-q" or "--quiet": quiet = true; break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                        return Fail($"unknown option: {arg}");
                    positionals.Add(arg);
                    break;
            }
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

        Callsign myCall;
        if (mycall is not null)
        {
            if (!Callsign.TryParse(mycall.ToUpperInvariant(), out myCall))
                return Fail($"invalid callsign: {mycall}");
        }
        else if (entry?.Callsign is { } fromFile)
        {
            myCall = fromFile;
        }
        else
        {
            return Fail("missing -s <callsign>: the port did not supply one");
        }

        int baudRate = transport!.Baud ?? KissSerialModem.DefaultBaudRate;
        if (baudArg is not null)
        {
            if (!TryParseCount(baudArg, out var b) || b < 1) return Fail($"invalid baud rate: {baudArg}");
            baudRate = b;
        }

        if (!TryParseBind(listenArg, out var bind, out err)) return Fail(err!);

        var hostsPaths = hostsArg is not null ? (IReadOnlyList<string>)[hostsArg] : HostsFile.SearchPaths();
        if (!HostsFile.TryLoad(hostsPaths, out var hosts, out err)) return Fail(err!);

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

        // The command line beats the ports file, exactly as it does for axcall.
        channel = (entry?.Channel ?? ChannelParams.None).OverriddenBy(channel);

        return new ParsedArgs(myCall, transport, portName, baudRate, bind, hosts!, paclen, window,
            mod128, noXid, keepalive, channel, trace, quiet);
    }

    /// <summary>
    /// Where to accept SOCKS connections. Loopback unless told otherwise:
    /// binding this to the world hands anyone who can reach the port the use of
    /// your callsign and your transmitter.
    /// </summary>
    internal static bool TryParseBind(string? spec, out IPEndPoint bind, out string? error)
    {
        bind = new IPEndPoint(IPAddress.Loopback, DefaultSocksPort);
        error = null;

        if (spec is null)
            return true;

        if (int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var bare))
        {
            if (bare is < 1 or > 65535)
            {
                error = $"invalid --listen port: {spec}";
                return false;
            }
            bind = new IPEndPoint(IPAddress.Loopback, bare);
            return true;
        }

        if (IPEndPoint.TryParse(spec, out var parsed) && parsed.Port != 0)
        {
            bind = parsed;
            return true;
        }

        error = $"invalid --listen '{spec}': want a port, or address:port";
        return false;
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
        Console.Error.WriteLine($"axsocks: {message}");
        return code;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            Usage: axsocks [options] <port>

            A SOCKS5 proxy in front of one AX.25 port. Every connection it accepts
            becomes a connected-mode session to a callsign, so software that speaks
            sockets reaches the packet network without knowing what AX.25 is.

              <port>                 A name from the ports file, a device path, or
                                     host:port for a KISS-over-TCP modem.

            Options:
              -s, --mycall <call>    Callsign to call out as. Required unless the
                                     ports file entry supplies one.
                  --listen <spec>    Where to accept SOCKS5 connections: a port, or
                                     address:port (default 127.0.0.1:{DefaultSocksPort}).
                  --hosts <file>     Hosts file to use instead of the usual search.
                  --serial <dev[:baud]>  Serial KISS TNC, instead of a <port>.
                  --tcp <host:port>  KISS over TCP, instead of a <port>.
                  --baud <rate>      Serial baud rate (default: {KissSerialModem.DefaultBaudRate}).
              -p, --paclen <bytes>   N1, 1..{MaxPaclen}.
              -w, --window <frames>  k, 1..{MaxWindowMod8} (1..{MaxWindowMod128} with --mod128).
                  --mod128           Prefer AX.25 v2.2 (SABME) on each call.
                  --no-xid           Skip pre-connect XID negotiation.
                  --keepalive <s>    T3 (default {SessionRelayOptions.DefaultKeepaliveSeconds}).
                  --txdelay <ms>     KISS channel access, as kissparms(8) set it.
                  --persist <0-255>
                  --slottime <ms>
                  --txtail <ms>
              -d                     Trace every frame to stderr.
              -q, --quiet            Log errors only.
              -h, --help             This help.
              -v, --version          Version and library versions.

            Names come from the hosts file ({HostsFile.SystemPath}, then the
            per-user one), and a name that is itself a callsign needs no entry:

              # name     callsign    port
              gb7rdg     GB7RDG      radio

            Point a client at it with a form that lets the proxy resolve the name,
            or it will hand us an IP address we can do nothing with:

              curl --socks5-hostname localhost:{DefaultSocksPort} http://gb7rdg/
              ssh -o ProxyCommand='nc -X 5 -x localhost:{DefaultSocksPort} %h %p' pi@lab

            One connection at a time per station: an AX.25 link is a single ordered
            stream, so a second call to a station already in a session is refused
            rather than interleaved. Bear it in mind before pointing a browser at
            this, which will cheerfully open six.
            """);
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"axsocks {AsmVersion(typeof(Program))}");
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
