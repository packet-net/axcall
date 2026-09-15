using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using Axcall;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.Serial;

namespace Axtun;

/// <summary>
/// axtun: IP over AX.25 on a TUN device, so software that already exists works
/// over radio.
/// </summary>
/// <remarks>
/// The other tools in this repo carry a stream: a terminal session, a socket, a
/// program's standard input. This one carries packets, which is a different
/// thing for a different person. ping, ssh, UDP, a route to a remote site's
/// whole LAN: none of that is reachable through a stream proxy, by construction.
/// </remarks>
internal static class Program
{
    internal const string DefaultInterface = "ax0";

    /// <summary>The smallest MTU IPv4 requires every link to carry (RFC 791).</summary>
    internal const int MinMtu = 68;

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

        if (!OperatingSystem.IsLinux())
        {
            // macOS has utun and Windows needs a driver installed; neither is
            // this interface, and claiming to support them would be a lie that
            // only shows up on somebody's bench.
            return Err("axtun needs Linux: it works through /dev/net/tun, which is a Linux interface.", 3);
        }

        if (ParseArgs(args) is not { } parsed) return 2;

        using var appCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            appCts.Cancel();
        };

        return await RunAsync(parsed, appCts.Token).ConfigureAwait(false);
    }

    [SupportedOSPlatform("linux")]
    private static async Task<int> RunAsync(ParsedArgs parsed, CancellationToken ct)
    {
        TunDevice tun;
        try
        {
            tun = TunDevice.Open(parsed.Interface);
        }
        catch (TunException ex)
        {
            return Err(ex.Message, 3);
        }

        using (tun)
        {
            if (parsed.Address is { } address)
            {
                try
                {
                    tun.Configure(address.Address, address.PrefixLength, parsed.Mtu);
                }
                catch (TunException ex)
                {
                    return Err(ex.Message, 3);
                }
            }

            // Either we just set it, or the operator did. Without it we cannot
            // answer ARP, which is the only way a station that has not been
            // told about us can find us.
            var local = parsed.Address?.Address ?? tun.ReadAddress();

            IAx25Transport modem;
            try
            {
                modem = parsed.Transport.IsSerial
                    ? KissSerialModem.Open(parsed.Transport.Device!, parsed.BaudRate)
                    : await KissTcpClient.ConnectAsync(parsed.Transport.Host!, parsed.Transport.TcpPort, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Err($"failed to open modem: {ex.Message}", 3);
            }

            try
            {
                // Channel access before anything else goes out, so the first
                // frame is keyed with the settings that were asked for rather
                // than the ones the TNC happened to boot with.
                if (parsed.Channel.Any)
                {
                    if (modem is not ICsmaChannelParams csma)
                        return Err($"this transport cannot set KISS channel parameters: {parsed.Transport}", 2);

                    await parsed.Channel.ApplyAsync(csma, ct).ConfigureAwait(false);
                }

                var bridge = new TunBridge(tun, modem, parsed.MyCall, parsed.Config, local, Console.Error, parsed.Quiet);
                Announce(tun, parsed, local);

                try
                {
                    await bridge.RunAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ctrl-C. The summary below is the reason for catching it.
                }

                Console.Error.WriteLine($"axtun: {bridge.Summarise()}");
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
    }

    [SupportedOSPlatform("linux")]
    private static void Announce(TunDevice tun, ParsedArgs parsed, IPAddress? local)
    {
        if (parsed.Quiet)
            return;

        var where = local is null ? "unaddressed" : local.ToString();
        Console.Error.WriteLine($"axtun: {tun.Name} is {where}, transmitting as {parsed.MyCall} on {parsed.PortName}");
        Console.Error.WriteLine($"axtun: {parsed.Config.Filter.Describe()}");
        Console.Error.WriteLine(
            $"axtun: {parsed.Config.Routes.Count} route{(parsed.Config.Routes.Count == 1 ? "" : "s")} configured"
            + (parsed.Config.Routes.Count == 0 ? "; every destination will be ARPed for" : ""));

        if (local is null)
        {
            Console.Error.WriteLine(
                $"axtun: {tun.Name} has no address, so ARP cannot be answered and nobody can "
                + $"discover us. Give it one with --addr, or 'ip addr add ... dev {tun.Name}'.");
        }
    }

    internal sealed record ParsedArgs(
        Callsign MyCall,
        TransportSpec Transport,
        string PortName,
        int BaudRate,
        string Interface,
        LocalAddress? Address,
        int Mtu,
        AxtunConfig Config,
        ChannelParams Channel,
        bool Quiet);

    /// <summary>An address and the size of the subnet it sits in.</summary>
    internal sealed record LocalAddress(IPAddress Address, int PrefixLength);

    internal static ParsedArgs? ParseArgs(string[] args)
    {
        string? mycall = null, devArg = null, addrArg = null, mtuArg = null, configArg = null;
        string? serialArg = null, tcpArg = null, baudArg = null;
        string? txdelayArg = null, persistArg = null, slottimeArg = null, txtailArg = null;
        bool quiet = false;
        var positionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-s" or "--mycall": if (!Next(args, ref i, arg, out mycall)) return null; break;
                case "--dev": if (!Next(args, ref i, arg, out devArg)) return null; break;
                case "--addr": if (!Next(args, ref i, arg, out addrArg)) return null; break;
                case "--mtu": if (!Next(args, ref i, arg, out mtuArg)) return null; break;
                case "--config": if (!Next(args, ref i, arg, out configArg)) return null; break;
                case "--serial": if (!Next(args, ref i, arg, out serialArg)) return null; break;
                case "--tcp": if (!Next(args, ref i, arg, out tcpArg)) return null; break;
                case "--baud": if (!Next(args, ref i, arg, out baudArg)) return null; break;
                case "--txdelay": if (!Next(args, ref i, arg, out txdelayArg)) return null; break;
                case "--persist": if (!Next(args, ref i, arg, out persistArg)) return null; break;
                case "--slottime": if (!Next(args, ref i, arg, out slottimeArg)) return null; break;
                case "--txtail": if (!Next(args, ref i, arg, out txtailArg)) return null; break;
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

        var deviceName = devArg ?? DefaultInterface;
        if (deviceName.Length == 0 || deviceName.Length > TunDevice.MaxNameLength
            || deviceName.Contains('/', StringComparison.Ordinal))
        {
            return Fail($"invalid --dev '{deviceName}': an interface name is 1 to {TunDevice.MaxNameLength} characters, with no slashes");
        }

        LocalAddress? address = null;
        if (addrArg is not null)
        {
            if (!TryParseLocalAddress(addrArg, out address, out err)) return Fail($"--addr: {err}");
        }

        int mtu = Ax25Ip.DefaultMtu;
        if (mtuArg is not null)
        {
            if (!TryParseCount(mtuArg, out mtu) || mtu < MinMtu || mtu > Ax25Ip.AbsoluteMaxMtu())
                return Fail($"invalid --mtu: {mtuArg} (must be {MinMtu}..{Ax25Ip.AbsoluteMaxMtu()} bytes)");

            // Above the known-good ceiling is allowed, because that ceiling is
            // one observation about one peer rather than a property of AX.25,
            // and refusing it would make this tool the reason somebody cannot
            // use a link that works. But say so: a station that silently
            // discards an overlong frame, as LinBPQ does, is indistinguishable
            // from a dead one.
            if (mtu > Ax25Ip.MaxMtu())
            {
                Console.Error.WriteLine(
                    $"axtun: --mtu {mtu} is above {Ax25Ip.MaxMtu()}, the largest packet every peer tested "
                    + "so far accepts. LinBPQ discards a longer frame without a word, so if the other end "
                    + "goes quiet, this is the first thing to put back.");
            }
        }

        if (addrArg is null && mtuArg is not null)
            return Fail("--mtu needs --addr: without an address to set, the interface is left exactly as it was found");

        var configPaths = configArg is not null ? (IReadOnlyList<string>)[configArg] : AxtunFile.SearchPaths();
        if (!AxtunFile.TryLoad(configPaths, out var config, out err)) return Fail(err!);

        if (!TryChannelParams(txdelayArg, persistArg, slottimeArg, txtailArg, out var channel, out err))
            return Fail(err!);

        // The command line beats the ports file, exactly as it does for axcall.
        channel = (entry?.Channel ?? ChannelParams.None).OverriddenBy(channel);

        return new ParsedArgs(myCall, transport, portName, baudRate, deviceName, address, mtu, config!, channel, quiet);
    }

    /// <summary>Parse "44.131.20.1/24": an address, and the subnet it is on.</summary>
    internal static bool TryParseLocalAddress(string text, out LocalAddress? address, out string? error)
    {
        address = null;
        error = null;

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            error = $"'{text}' needs a prefix length, as in {text}/24: the subnet is what decides what is on-link";
            return false;
        }

        if (!IPAddress.TryParse(text[..slash], out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"'{text[..slash]}' is not an IPv4 address";
            return false;
        }

        if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > 32)
        {
            error = $"'{text[(slash + 1)..]}' is not a prefix length in 0..32";
            return false;
        }

        address = new LocalAddress(parsed, length);
        return true;
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
        Console.Error.WriteLine($"axtun: {message}");
        return code;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            Usage: axtun [options] <port>

            IP over AX.25 on a TUN device, so software that already exists works over
            radio: ping, ssh, UDP, and a route to a remote site's whole LAN.

              <port>                 A name from the ports file, a device path, or
                                     host:port for a KISS-over-TCP modem.

            Options:
              -s, --mycall <call>    Callsign to transmit as. Required unless the
                                     ports file entry supplies one.
                  --dev <name>       Interface to create (default {DefaultInterface}).
                  --addr <addr/len>  Address to put on it, e.g. 44.131.20.1/24. Needs
                                     CAP_NET_ADMIN. Without it, the interface has to be
                                     configured already.
                  --mtu <bytes>      Interface MTU (default {Ax25Ip.DefaultMtu}). Above {Ax25Ip.MaxMtu()} is
                                     allowed but untested against any peer.
                  --config <file>    Config file to use instead of the usual search.
                  --serial <dev[:baud]>  Serial KISS TNC, instead of a <port>.
                  --tcp <host:port>  KISS over TCP, instead of a <port>.
                  --baud <rate>      Serial baud rate (default: {KissSerialModem.DefaultBaudRate}).
                  --txdelay <ms>     KISS channel access, as kissparms(8) set it.
                  --persist <0-255>
                  --slottime <ms>
                  --txtail <ms>
              -q, --quiet            Log errors only.
              -h, --help             This help.
              -v, --version          Version and library versions.

            Creating the interface needs CAP_NET_ADMIN. To run unprivileged, create it
            once as root and hand it over:

              sudo ip tuntap add {DefaultInterface} mode tun user $USER
              sudo ip addr add 44.131.20.1/24 dev {DefaultInterface}
              sudo ip link set {DefaultInterface} up mtu {Ax25Ip.DefaultMtu}
              axtun -s M0LTE-7 radio

            Config file: {AxtunFile.SystemPath}, or ${AxtunFile.PathEnvVar}. It says which
            station reaches which addresses, and what is allowed out of the radio.
            Nothing is transmitted that no rule allows.
            """);
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"axtun {AsmVersion(typeof(Program))}");
        Console.WriteLine();
        Console.WriteLine("Runtime libraries:");
        Console.WriteLine($"  Packet.Core         {AsmVersion(typeof(Callsign))}");
        Console.WriteLine($"  Packet.Ax25         {AsmVersion(typeof(Packet.Ax25.Ax25Frame))}");
        Console.WriteLine($"  Packet.Kiss.Serial  {AsmVersion(typeof(KissSerialModem))}");
    }

    private static string AsmVersion(Type type)
        => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
           ?? type.Assembly.GetName().Version?.ToString()
           ?? "unknown";

}
