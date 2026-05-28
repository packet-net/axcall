using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.Serial;

namespace Axcall;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        string? myCallStr = null;
        string? portName = null;
        string? tcpArg = null;
        int baudRate = KissSerialModem.DefaultBaudRate;
        bool listen = false;
        string? destination = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-s" or "--mycall":
                    if (++i >= args.Length) return Err("missing value for --mycall");
                    myCallStr = args[i];
                    break;
                case "-p" or "--port":
                    if (++i >= args.Length) return Err("missing value for --port");
                    portName = args[i];
                    break;
                case "-t" or "--tcp":
                    if (++i >= args.Length) return Err("missing value for --tcp");
                    tcpArg = args[i];
                    break;
                case "-b" or "--baud":
                    if (++i >= args.Length) return Err("missing value for --baud");
                    if (!int.TryParse(args[i], out baudRate)) return Err($"invalid baud rate: {args[i]}");
                    break;
                case "-l" or "--listen":
                    listen = true;
                    break;
                default:
                    if (args[i].StartsWith('-')) return Err($"unknown option: {args[i]}");
                    if (destination is not null) return Err($"unexpected argument: {args[i]}");
                    destination = args[i];
                    break;
            }
        }

        if (myCallStr is null) return Err("--mycall is required");
        if (portName is null && tcpArg is null) return Err("one of --port or --tcp is required");
        if (portName is not null && tcpArg is not null) return Err("--port and --tcp are mutually exclusive");
        if (!listen && destination is null) return Err("<destination> is required (or use --listen)");
        if (listen && destination is not null) return Err("--listen and <destination> are mutually exclusive");

        if (!Callsign.TryParse(myCallStr.ToUpperInvariant(), out var myCall))
            return Err($"invalid callsign: {myCallStr}");

        Callsign? target = null;
        if (destination is not null)
        {
            if (!Callsign.TryParse(destination.ToUpperInvariant(), out var t))
                return Err($"invalid destination callsign: {destination}");
            target = t;
        }

        using var appCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            appCts.Cancel();
        };

        IKissModem modem;
        try
        {
            if (tcpArg is not null)
            {
                var (host, port) = ParseHostPort(tcpArg);
                modem = await KissTcpClient.ConnectAsync(host, port, appCts.Token).ConfigureAwait(false);
            }
            else
            {
                modem = KissSerialModem.Open(portName!, baudRate);
            }
        }
        catch (Exception ex)
        {
            return Err($"failed to open modem: {ex.Message}", 3);
        }

        try
        {
            await using var relay = new SessionRelay(modem, myCall);

            if (listen)
            {
                return await relay.ListenAndRelayAsync(appCts.Token).ConfigureAwait(false);
            }
            else
            {
                return await relay.ConnectAndRelayAsync(target!.Value, appCts.Token).ConfigureAwait(false);
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

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
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
              -h, --help             Show this help
            """);
    }
}
