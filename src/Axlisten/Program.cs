using System.Globalization;
using System.Reflection;
using Axcall;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.Serial;

namespace Axlisten;

/// <summary>
/// axlisten: show what is on the channel.
/// </summary>
/// <remarks>
/// The kernel version listened on a packet socket, and so heard every AX.25
/// interface on the machine, including what the machine itself sent. This one
/// listens on KISS, and so hears what each TNC hears. That is everything on
/// the air, but not what another program sends through the same TNC, because a
/// KISS TNC does not echo transmissions back to the host.
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-v") || args.Contains("-V"))
        {
            PrintVersion();
            return 0;
        }

        // -h is hex in the classic command line, so there only --help is help.
        var classic = args.Contains("--classic");
        if (args.Contains("--help") || (!classic && args.Contains("-h")))
        {
            PrintUsage();
            return 0;
        }

        if (ParseArgs(args) is not { } parsed) return 2;

        using var appCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            appCts.Cancel();
        };

        var monitor = new ChannelMonitor(
            parsed.Ports.Select(p => ToMonitorPort(p, parsed.Baud)).ToList(),
            CreateFormatter(parsed, Console.IsOutputRedirected),
            parsed.Filter,
            Console.Out,
            Console.Error,
            parsed.Quiet || parsed.Mode == OutputMode.Classic);

        return await monitor.RunAsync(appCts.Token).ConfigureAwait(false);
    }

    internal enum OutputMode
    {
        Friendly,
        Json,
        Classic,
    }

    internal enum ColorChoice
    {
        Auto,
        Always,
        Never,
    }

    /// <summary>A port to open, and whether failing to open it is fatal.</summary>
    internal sealed record PortChoice(string Name, TransportSpec Transport, bool Required);

    internal sealed record ParsedArgs(
        IReadOnlyList<PortChoice> Ports,
        int? Baud,
        OutputMode Mode,
        CallFilter Filter,
        bool Quiet,
        bool Hex,
        ColorChoice Color,
        TimeStyle Time,
        ClassicDump Dump,
        int Timestamps,
        bool EightBit,
        bool Ibm);

    internal static IFrameFormatter CreateFormatter(ParsedArgs parsed, bool outputRedirected)
    {
        var color = parsed.Color switch
        {
            ColorChoice.Always => true,
            ColorChoice.Never => false,
            // The no-color.org convention, and a terminal that says it cannot.
            _ => !outputRedirected
                 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                 && Environment.GetEnvironmentVariable("TERM") != "dumb",
        };

        return parsed.Mode switch
        {
            OutputMode.Json => new JsonFormatter(),
            OutputMode.Classic => new ClassicFormatter(
                parsed.Dump, parsed.Timestamps, parsed.EightBit, parsed.Ibm, parsed.Color == ColorChoice.Always),
            _ => new FriendlyFormatter(color, parsed.Time, parsed.Hex, parsed.Ports.Max(p => p.Name.Length)),
        };
    }

    private static MonitorPort ToMonitorPort(PortChoice port, int? baudOverride)
    {
        var spec = port.Transport;
        var baud = baudOverride ?? spec.Baud ?? KissSerialModem.DefaultBaudRate;
        var description = spec.IsSerial ? $"{spec.Device}:{baud}" : spec.ToString();

        return new MonitorPort(
            port.Name,
            description,
            async ct => spec.IsSerial
                ? KissSerialModem.Open(spec.Device!, baud)
                // The library's read-idle timeout is kept: it is what notices a
                // TCP link that died without a FIN, and the monitor redials.
                : await KissTcpClient.ConnectAsync(spec.Host!, spec.TcpPort, null, TimeProvider.System, ct).ConfigureAwait(false),
            port.Required);
    }

    internal static ParsedArgs? ParseArgs(string[] args)
    {
        var classic = args.Contains("--classic");
        var portArgs = new List<string>();
        var serialArgs = new List<string>();
        var tcpArgs = new List<string>();
        var filter = new CallFilter();
        string? baudArg = null;
        bool quiet = false, json = false, hex = false, eightBit = false, ibm = false;
        var color = ColorChoice.Auto;
        var time = TimeStyle.Local;
        var dump = ClassicDump.Ascii;
        int timestamps = 0;
        string? modernOnly = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? value;
            switch (arg)
            {
                case "--classic": break;
                case "--serial": if (!Next(args, ref i, arg, out value)) return null; serialArgs.Add(value!); break;
                case "--tcp": if (!Next(args, ref i, arg, out value)) return null; tcpArgs.Add(value!); break;
                case "--baud": if (!Next(args, ref i, arg, out baudArg)) return null; break;
                case "--call":
                    if (!Next(args, ref i, arg, out value)) return null;
                    if (!filter.TryAdd(value!)) return Fail($"invalid callsign: {value}");
                    break;
                case "-q" or "--quiet": quiet = true; break;

                case "--json": json = true; modernOnly ??= arg; break;
                case "--hex": hex = true; modernOnly ??= arg; break;
                case "--utc": time = TimeStyle.Utc; modernOnly ??= arg; break;
                case "--no-time": time = TimeStyle.None; modernOnly ??= arg; break;
                case "--no-color": color = ColorChoice.Never; modernOnly ??= arg; break;
                case "--color":
                    if (!Next(args, ref i, arg, out value)) return null;
                    ColorChoice? when = value switch
                    {
                        "auto" => ColorChoice.Auto,
                        "always" => ColorChoice.Always,
                        "never" => ColorChoice.Never,
                        _ => null,
                    };
                    if (when is null) return Fail($"--color takes auto, always or never, not '{value}'");
                    color = when.Value;
                    modernOnly ??= arg;
                    break;

                case "-x" when !classic: hex = true; break;

                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        return Fail($"unknown option: {arg}");

                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        if (!classic)
                            return Fail($"unknown option: {arg}");

                        // getopt("8achip:rtv"): letters cluster, and -p takes
                        // the rest of its argument or the next one.
                        for (int j = 1; j < arg.Length; j++)
                        {
                            switch (arg[j])
                            {
                                case '8': eightBit = true; break;
                                case 'a': break; // All frames. KISS only ever gives us all of them.
                                case 'c': color = ColorChoice.Always; break;
                                case 'h': dump = ClassicDump.Hex; break;
                                case 'i': ibm = true; break;
                                case 'r': dump = ClassicDump.Readable; break;
                                case 't': timestamps++; break;
                                case 'p':
                                    if (j + 1 < arg.Length)
                                    {
                                        portArgs.Add(arg[(j + 1)..]);
                                    }
                                    else
                                    {
                                        if (!Next(args, ref i, "-p", out value)) return null;
                                        portArgs.Add(value!);
                                    }
                                    j = arg.Length;
                                    break;
                                default:
                                    return Fail($"unknown option: -{arg[j]}");
                            }
                        }
                        break;
                    }

                    portArgs.Add(arg);
                    break;
            }
        }

        if (classic && modernOnly is not null)
            return Fail($"{modernOnly} is not an option of the classic output");
        if (timestamps > 5)
            return Fail("only -t, -tt, -ttt, -tttt and -ttttt are supported");

        int? baud = null;
        if (baudArg is not null)
        {
            if (!int.TryParse(baudArg, NumberStyles.None, CultureInfo.InvariantCulture, out var b) || b < 1)
                return Fail($"invalid baud rate: {baudArg}");
            baud = b;
        }

        var ports = new List<PortChoice>();
        string? err;

        foreach (var name in portArgs)
        {
            PortEntry? entry = null;
            TransportSpec? transport;
            if (TransportSpec.LooksLikeName(name))
            {
                if (!PortsFile.TryResolve(name, out entry, out err)) return Fail(err!);
                transport = entry!.Transport;
            }
            else if (!TransportSpec.TryParse(name, out transport, out err))
            {
                return Fail(err!);
            }
            Add(ports, new PortChoice(name, transport!, Required: true));
        }
        foreach (var serial in serialArgs)
        {
            if (!TransportSpec.TryParseDevice(serial, out var transport, out err)) return Fail($"--serial: {err}");
            Add(ports, new PortChoice(serial, transport!, Required: true));
        }
        foreach (var tcp in tcpArgs)
        {
            if (!TransportSpec.TryParseEndpoint(tcp, out var transport, out err)) return Fail($"--tcp: {err}");
            Add(ports, new PortChoice(tcp, transport!, Required: true));
        }

        // Nothing named: every port in the ports file, which is what the
        // classic one did with every AX.25 interface on the machine.
        if (ports.Count == 0)
        {
            if (!PortsFile.TryLoad(out var entries, out err)) return Fail(err!);
            foreach (var entry in entries!.Values)
                Add(ports, new PortChoice(entry.Name, entry.Transport, Required: false));

            if (ports.Count == 0)
            {
                return Fail(
                    $"no port given, and none configured in {string.Join(", ", PortsFile.SearchPaths())}. "
                    + "Name one: a ports-file name, a device path, or host:port.");
            }
        }

        return new ParsedArgs(
            ports, baud,
            classic ? OutputMode.Classic : json ? OutputMode.Json : OutputMode.Friendly,
            filter, quiet, hex, color, time, dump, timestamps, eightBit, ibm);
    }

    // One TNC is one port, however many names it goes by. Opening a serial
    // device twice fails, and a TCP modem opened twice prints every frame twice.
    private static void Add(List<PortChoice> ports, PortChoice port)
    {
        var key = TransportKey(port.Transport);
        if (!ports.Any(p => TransportKey(p.Transport) == key))
            ports.Add(port);
    }

    private static string TransportKey(TransportSpec spec)
        => spec.IsSerial ? spec.Device! : $"{spec.Host}:{spec.TcpPort}";

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

    private static ParsedArgs? Fail(string message)
    {
        Console.Error.WriteLine($"axlisten: {message}");
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            Usage: axlisten [options] [port...]
                   axlisten --classic [-8achirt] [-p port]

            Show every AX.25 frame the TNC hears, decoded. Receive only: it never
            transmits and needs no callsign.

              port                   A name from the ports file, a device path, or
                                     host:port for a KISS-over-TCP modem. Give as
                                     many as you like. With none, every port in the
                                     ports file.

            Options:
                  --serial <dev[:baud]>  Serial KISS TNC. Repeatable.
                  --tcp <host:port>  KISS over TCP. Repeatable.
                  --baud <rate>      Serial baud rate (default: {KissSerialModem.DefaultBaudRate}).
                  --call <call>      Only frames to, from or via this station.
                                     Without an SSID, any SSID. Repeatable.
              -x, --hex              Hex dump every payload, as well as decoding it.
                  --json             One JSON object per frame, per line.
                  --color <when>     auto (the default), always or never.
                  --no-color         Same as --color never.
                  --utc              Timestamps in UTC rather than local time.
                  --no-time          No timestamps.
              -q, --quiet            No status messages; frames and errors only.
                  --classic          The output of the ax25-apps axlisten, and its
                                     options (below).
              -h, --help             This help.
              -v, --version          Version and library versions.

            With --classic:
              -a                     Accepted and ignored: every frame is shown.
              -c                     Colour.
              -h                     Hex dump. (--help is help.)
              -r                     Readable dump: the text as sent.
              -8                     Pass 8-bit characters.
              -i                     Map IBM code page 437 characters.
              -p <port>              The port to listen on.
              -t                     No timestamps. -tt Unix time, -ttt time since
                                     the previous frame, -tttt date and time,
                                     -ttttt time since the first frame.

            Ports file: {PortsFile.SystemPath}, then ~/.config/axcall/ports, or ${PortsFile.PathEnvVar}.
            """);
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"axlisten {AsmVersion(typeof(Program))}");
        Console.WriteLine();
        Console.WriteLine("Runtime libraries:");
        Console.WriteLine($"  Packet.Core         {AsmVersion(typeof(Callsign))}");
        Console.WriteLine($"  Packet.Ax25         {AsmVersion(typeof(Packet.Ax25.Ax25Frame))}");
        Console.WriteLine($"  Packet.Kiss         {AsmVersion(typeof(KissTcpClient))}");
        Console.WriteLine($"  Packet.Kiss.Serial  {AsmVersion(typeof(KissSerialModem))}");
    }

    private static string AsmVersion(Type type)
        => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
           ?? type.Assembly.GetName().Version?.ToString()
           ?? "unknown";
}
