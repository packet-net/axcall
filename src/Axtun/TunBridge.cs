using System.Net;
using Axcall;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;

namespace Axtun;

/// <summary>
/// The pump: IP packets off the TUN device and onto the air, AX.25 frames off
/// the air and into the TUN device.
/// </summary>
/// <remarks>
/// <para>
/// Outbound is UI frames, PID 0xCC. Inbound accepts both UI and connected-mode
/// I-frames, because a peer configured for virtual-circuit mode is a peer that
/// exists; see <see cref="Ax25Ip"/>.
/// </para>
/// <para>
/// Nothing is transmitted that the egress filter has not agreed to, and that
/// check happens before the route lookup rather than after, so a denied packet
/// costs no lookup and can never be transmitted by a code path that forgot to
/// ask.
/// </para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class TunBridge
{
    /// <summary>How long a TUN read waits before checking whether to stop.</summary>
    private const int PollMilliseconds = 250;

    /// <summary>
    /// The least time between ARP requests for the same address.
    /// </summary>
    /// <remarks>
    /// A stream of packets to an unknown station must not become a stream of
    /// ARP requests. One every fifteen seconds is enough for the reply to
    /// arrive and for the sender's own retry to then succeed, on a channel
    /// where a frame takes a second.
    /// </remarks>
    private static readonly TimeSpan ArpInterval = TimeSpan.FromSeconds(15);

    private readonly TunDevice tun;
    private readonly IAx25Transport modem;
    private readonly Callsign myCall;
    private readonly AxtunConfig config;
    private readonly EgressFilter filter;
    private readonly NeighbourTable neighbours = new();
    private readonly Dictionary<uint, DateTimeOffset> lastArp = [];
    // Touched by both pumps: the TUN side reports filtered and unroutable
    // packets, the radio side reports a failed write back to the kernel.
    private readonly HashSet<string> reportedDrops = [];
    private readonly Lock reportGate = new();
    private readonly TextWriter log;
    private readonly bool quiet;

    private readonly uint localAddress;

    public TunBridge(
        TunDevice tun,
        IAx25Transport modem,
        Callsign myCall,
        AxtunConfig config,
        IPAddress? localAddress,
        TextWriter log,
        bool quiet)
    {
        this.tun = tun;
        this.modem = modem;
        this.myCall = myCall;
        this.config = config;
        this.log = log;
        this.quiet = quiet;
        filter = config.Filter;
        this.localAddress = localAddress is null ? 0 : IpPrefix.ToUInt32(localAddress);
    }

    /// <summary>Packets handed to the radio.</summary>
    public long Transmitted { get; private set; }

    /// <summary>Packets handed to the kernel.</summary>
    public long Received { get; private set; }

    /// <summary>Packets the filter refused.</summary>
    public long Filtered { get; private set; }

    /// <summary>Packets with nowhere to go.</summary>
    public long Unroutable { get; private set; }

    /// <summary>Run both directions until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // The TUN side is a blocking file descriptor, so it gets a thread of
        // its own rather than pretending to be asynchronous on the thread pool.
        var fromTun = Task.Factory.StartNew(
            () => PumpFromTunAsync(ct),
            ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        var fromRadio = PumpFromRadioAsync(ct);

        await Task.WhenAll(fromTun, fromRadio).ConfigureAwait(false);
    }

    private async Task PumpFromTunAsync(CancellationToken ct)
    {
        // One MTU is what a TUN read hands over, but a peer or a route change
        // can leave a larger packet queued, and a short read silently truncates
        // it. Read into something that cannot be too small and judge the size
        // afterwards, where it can be reported.
        var buffer = new byte[Ax25Ip.MaxFrameBytes * 8];

        while (!ct.IsCancellationRequested)
        {
            if (!tun.WaitForPacket(PollMilliseconds))
                continue;

            int read;
            try
            {
                read = tun.Read(buffer);
            }
            catch (IOException ex)
            {
                if (ct.IsCancellationRequested) return;
                Report($"read from {tun.Name} failed: {ex.Message}");
                return;
            }

            if (read <= 0)
                continue;

            await SendAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        if (!IpPacket.TryRead(packet.Span, out var ip))
        {
            // IPv6, or something with a header we cannot read. Either way it is
            // not judgeable, and what cannot be judged does not go out.
            Filtered++;
            ReportOnce("not-ipv4", $"dropped a packet that is not IPv4: nothing but IPv4 is carried");
            return;
        }

        if (!filter.Allows(in ip, out var reason))
        {
            Filtered++;
            ReportOnce($"filter:{ip.Protocol}:{ip.DestinationPort}", $"dropped {ip.Describe()}: {reason}");
            return;
        }

        var destination = ip.DestinationValue;
        Callsign target;

        if (config.Lookup(destination) is { } route)
        {
            target = route.Callsign;
        }
        else if (neighbours.TryGet(destination, out var neighbour) && neighbour is not null)
        {
            target = neighbour.Callsign;
        }
        else
        {
            Unroutable++;
            await AskWhoHasAsync(destination, ct).ConfigureAwait(false);
            return;
        }

        int room = Ax25Ip.MaxMtu();
        if (packet.Length > room)
        {
            Filtered++;
            ReportOnce(
                "oversize",
                $"dropped {ip.Describe()}: {packet.Length} bytes will not fit, the most a frame to "
                    + $"{target} can carry is {room}. Lower the MTU on {tun.Name}.");
            return;
        }

        var frame = Ax25Ip.Datagram(target, myCall, packet.Span);
        await modem.SendAsync(frame.ToBytes(), ct).ConfigureAwait(false);
        Transmitted++;
    }

    /// <summary>
    /// Broadcast an ARP request for an address nothing else has answered for.
    /// </summary>
    /// <remarks>
    /// The packet that prompted it is dropped rather than queued. Whatever sent
    /// it will retry, and by then the reply will have arrived; holding packets
    /// for an address that may never answer is a queue that only ever fills up.
    /// </remarks>
    private async Task AskWhoHasAsync(uint destination, CancellationToken ct)
    {
        if (localAddress == 0)
        {
            ReportOnce(
                $"noroute:{destination}",
                $"no route to {IpPrefix.ToAddress(destination)}, and no local address to ask from. "
                    + "Add a route line, or give the interface an address.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (lastArp.TryGetValue(destination, out var previous) && now - previous < ArpInterval)
            return;
        lastArp[destination] = now;

        var request = new AxArpMessage(
            AxArpOperation.Request,
            myCall,
            IpPrefix.ToAddress(localAddress),
            AxArpMessage.NoCallsign,
            IpPrefix.ToAddress(destination));

        var frame = Ax25Ip.Arp(Ax25Ip.Broadcast, myCall, request.ToBytes());
        await modem.SendAsync(frame.ToBytes(), ct).ConfigureAwait(false);
        Verbose($"who has {IpPrefix.ToAddress(destination)}? asked {Ax25Ip.Broadcast}");
    }

    private async Task PumpFromRadioAsync(CancellationToken ct)
    {
        await foreach (var inbound in modem.ReceiveAsync(ct).ConfigureAwait(false))
        {
            if (!Ax25Frame.TryParse(inbound.Ax25.Span, out var frame) || frame is null)
                continue;

            // A frame that came through a repeater came from behind it, and
            // nothing here digipeats, so it cannot be answered. It is still
            // delivered upward, because receiving works fine; what is refused
            // is recording a station we would then fail to reach.
            var direct = frame.Digipeaters.Count == 0;

            if (Ax25Ip.TryGetPayload(frame, Ax25Pid.Ip, out var packet))
            {
                Deliver(packet.Span, frame.Source.Callsign, direct);
            }
            else if (direct && Ax25Ip.TryGetPayload(frame, Ax25Pid.Arp, out var arp))
            {
                await HandleArpAsync(arp, frame.Source.Callsign, ct).ConfigureAwait(false);
            }
        }
    }

    private void Deliver(ReadOnlySpan<byte> packet, Callsign from, bool direct)
    {
        if (!IpPacket.TryRead(packet, out var ip))
            return;

        if (direct)
        {
            // Whoever sent this is reachable, and now we know who. A fragment
            // counts: the source address is in every fragment's header.
            neighbours.Learn(ip.SourceValue, from);
        }
        else
        {
            ReportOnce(
                $"repeated:{from}",
                $"heard {ip.Source} from {from} through a repeater. The packet is delivered, but "
                    + "the station is not recorded, because nothing here digipeats and a reply "
                    + "sent direct would not reach it.");
        }

        try
        {
            tun.Write(packet);
            Received++;
        }
        catch (IOException ex)
        {
            ReportOnce("tun-write", $"cannot write to {tun.Name}: {ex.Message}");
        }
    }

    private async Task HandleArpAsync(ReadOnlyMemory<byte> message, Callsign from, CancellationToken ct)
    {
        if (!AxArpMessage.TryParse(message.Span, out var arp) || arp is null)
            return;

        // Take the sender either way. A reply is the answer to something we
        // asked; a request tells us the same thing for free.
        if (arp.SenderCallsign.Base.Length > 0)
            neighbours.Learn(IpPrefix.ToUInt32(arp.SenderAddress), arp.SenderCallsign);

        if (arp.Operation != AxArpOperation.Request)
            return;

        if (localAddress == 0 || IpPrefix.ToUInt32(arp.TargetAddress) != localAddress)
            return;

        var reply = new AxArpMessage(
            AxArpOperation.Reply,
            myCall,
            IpPrefix.ToAddress(localAddress),
            arp.SenderCallsign,
            arp.SenderAddress,
            // Hand back the protocol type that was asked with. LinBPQ sends
            // 0x00CC and the Linux kernel sends 0x0800, neither checks it, and
            // reflecting it means neither ever sees a value it did not choose.
            arp.ProtocolType);

        var frame = Ax25Ip.Arp(from, myCall, reply.ToBytes());
        await modem.SendAsync(frame.ToBytes(), ct).ConfigureAwait(false);
        Verbose($"told {from} that {IpPrefix.ToAddress(localAddress)} is {myCall}");
    }

    /// <summary>A line for the shutdown summary.</summary>
    public string Summarise()
        => $"{Transmitted} out, {Received} in, {Filtered} filtered, {Unroutable} with no route, "
         + $"{neighbours.Count} station{(neighbours.Count == 1 ? "" : "s")} known";

    private void Report(string message) => log.WriteLine($"axtun: {message}");

    /// <summary>
    /// Say each kind of drop once. A filter that logs every packet it refuses
    /// fills the journal with the same line and teaches the operator to stop
    /// reading it; saying it once tells them what to allow.
    /// </summary>
    private void ReportOnce(string key, string message)
    {
        if (quiet)
            return;

        lock (reportGate)
        {
            if (!reportedDrops.Add(key))
                return;
        }

        Report(message);
    }

    private void Verbose(string message)
    {
        if (!quiet)
            Report(message);
    }
}
