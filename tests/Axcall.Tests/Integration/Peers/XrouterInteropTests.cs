using System.Buffers.Binary;
using System.Net;
using AwesomeAssertions;
using Axcall;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Xunit;
using Xunit.Abstractions;

namespace Axcall.Tests.Integration.Peers;

/// <summary>
/// IP over AX.25 against XRouter, including the one encapsulation nothing else
/// available can send.
/// </summary>
/// <remarks>
/// <para>
/// <c>axtun</c> accepts IP in connected-mode I-frames as well as in UI frames,
/// because ax25rtd.conf(5) has a per-route mode of datagram or virtual connect
/// and both are configured in the field. That claim had never been tested
/// against anything: LinBPQ's virtual-circuit transmit is malformed on 64-bit,
/// so it cannot be used to check it, and the kernel leg needs a VM. XRouter
/// has route mode <c>v</c> and gets it right.
/// </para>
/// <para>
/// This matters more than it looks. The last claim in this codebase that
/// rested on reading rather than observation was the ARP protocol type, and it
/// shipped wrong, silently unreachable by an entire class of peer. This is the
/// other one.
/// </para>
/// </remarks>
[Collection(XrouterCollection.Name)]
[Trait("Category", "Peers")]
public sealed class XrouterInteropTests
{
    /// <summary>The station XRouter routes to in datagram mode.</summary>
    private static readonly Callsign DatagramCall = new("AXTEST");

    private static readonly IPAddress DatagramAddress = IPAddress.Parse("44.131.20.1");

    /// <summary>The station XRouter routes to in virtual-circuit mode.</summary>
    private static readonly Callsign VirtualCircuitCall = new("AXVCT");

    private static readonly IPAddress VirtualCircuitAddress = IPAddress.Parse("44.131.20.2");

    private static readonly Callsign XrouterCall = new("XR0TST");

    /// <summary>Off-net, so a packet from it has to be routed rather than consumed.</summary>
    private static readonly IPAddress Elsewhere = IPAddress.Parse("44.131.99.9");

    private readonly XrouterFixture fixture;
    private readonly ITestOutputHelper output;

    public XrouterInteropTests(XrouterFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    /// <summary>
    /// The row the interop table has never been able to prove: a peer whose
    /// route says virtual circuit opens a session and sends the IP inside
    /// I-frames, and our codec takes it out again.
    /// </summary>
    [Fact]
    public async Task Ip_Arrives_Inside_A_Connected_Session_When_The_Peers_Route_Says_Virtual_Circuit()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await using var kiss = await KissTcpClient.ConnectAsync(XrouterFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);

        var received = new List<Ax25Frame>();
        var gate = new Lock();
        var arrived = new TaskCompletionSource<Ax25Frame>(TaskCreationOptions.RunContinuationsAsynchronously);

        var listener = new Ax25Listener(kiss, new Ax25ListenerOptions { MyCall = VirtualCircuitCall });

        listener.FrameTraced += (_, e) =>
        {
            if (e.Direction != FrameDirection.Received)
                return;

            lock (gate)
            {
                received.Add(e.Frame);
            }
            output.WriteLine($"rx {FrameTrace.Format(e)}");

            if (e.Frame.FrameType == Ax25FrameType.I && Ax25Ip.TryGetPayload(e.Frame, Ax25Pid.Ip, out ReadOnlyMemory<byte> _))
                arrived.TrySetResult(e.Frame);
        };

        await using (listener)
        {
            await listener.StartAsync(cts.Token);

            // XRouter will call us to deliver the packet, so the session has to
            // be answered. That is the whole point of virtual-circuit mode.
            listener.AcceptIncoming = true;

            // Give XRouter something to route to the virtual-circuit station.
            // The trigger goes out on the same transport the listener holds:
            // net-sim hands received frames to one client per port, so a second
            // connection would be deaf.
            var trigger = Ax25Ip.Datagram(
                XrouterCall,
                DatagramCall,
                Icmp.EchoRequest(Elsewhere, VirtualCircuitAddress, 0x5601, 1, "virtual circuit row"u8));

            var frame = await SendUntilAsync(kiss, trigger, arrived.Task, cts.Token);

            if (frame is null)
            {
                await DumpLogsAsync();
                lock (gate)
                {
                    output.WriteLine($"{received.Count} frames were received, none of them IP in an I-frame");
                }
            }

            frame.Should().NotBeNull("XRouter should open a session and send the IP inside it");

            frame!.FrameType.Should().Be(Ax25FrameType.I, "virtual-circuit mode means I-frames, not UI");
            frame.Pid.Should().Be(Ax25Pid.Ip);
            frame.Source.Callsign.Should().Be(XrouterCall);
            frame.Destination.Callsign.Should().Be(VirtualCircuitCall);

            Ax25Ip.TryGetPayload(frame, Ax25Pid.Ip, out var payload).Should().BeTrue();
            IpPacket.TryRead(payload.Span, out var ip).Should().BeTrue("the I-frame should carry a readable IPv4 packet");

            ip.Protocol.Should().Be(IpPacket.ProtocolIcmp);
            ip.Source.Should().Be(Elsewhere);
            ip.Destination.Should().Be(VirtualCircuitAddress);
        }
    }

    /// <summary>
    /// The same node, the same run, the other encapsulation: a station whose
    /// route is datagram gets UI frames.
    /// </summary>
    [Fact]
    public async Task Ip_Arrives_In_A_Ui_Frame_When_The_Peers_Route_Says_Datagram()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var kiss = await KissTcpClient.ConnectAsync(XrouterFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);

        var arrived = new TaskCompletionSource<Ax25Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = StartReader(kiss, frame =>
        {
            if (frame.IsUi && Ax25Ip.TryGetPayload(frame, Ax25Pid.Ip, out ReadOnlyMemory<byte> _))
                arrived.TrySetResult(frame);
        }, cts.Token);

        var trigger = Ax25Ip.Datagram(
            XrouterCall,
            DatagramCall,
            Icmp.EchoRequest(Elsewhere, DatagramAddress, 0x5602, 1, "datagram row"u8));

        var frame = await SendUntilAsync(kiss, trigger, arrived.Task, cts.Token);

        if (frame is null) await DumpLogsAsync();
        frame.Should().NotBeNull("XRouter should forward the packet as a UI frame");

        frame!.IsUi.Should().BeTrue();
        frame.Pid.Should().Be(Ax25Pid.Ip);
        Ax25Ip.TryGetPayload(frame, Ax25Pid.Ip, out var payload).Should().BeTrue();
        IpPacket.TryRead(payload.Span, out var ip).Should().BeTrue();
        ip.Destination.Should().Be(DatagramAddress);
    }

    /// <summary>
    /// XRouter's AX.25 ARP, for the record: it agrees with LinBPQ and the Linux
    /// kernel on the protocol type, which is the field axtun got wrong.
    /// </summary>
    [Fact]
    public async Task Xrouter_Answers_Ax25_Arp_With_The_Same_Protocol_Type_As_Everything_Else()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var kiss = await KissTcpClient.ConnectAsync(XrouterFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);

        var arrived = new TaskCompletionSource<Ax25Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = StartReader(kiss, frame =>
        {
            if (Ax25Ip.TryGetPayload(frame, Ax25Pid.Arp, out ReadOnlyMemory<byte> _))
                arrived.TrySetResult(frame);
        }, cts.Token);

        var request = new AxArpMessage(
            AxArpOperation.Request, DatagramCall, DatagramAddress,
            AxArpMessage.NoCallsign, IPAddress.Parse("44.131.20.4"));

        var frame = await SendUntilAsync(kiss, Ax25Ip.Arp(Ax25Ip.Broadcast, DatagramCall, request.ToBytes()),
            arrived.Task, cts.Token);

        if (frame is null) await DumpLogsAsync();
        frame.Should().NotBeNull("XRouter should answer an ARP broadcast for its own address");

        Ax25Ip.TryGetPayload(frame!, Ax25Pid.Arp, out var payload).Should().BeTrue();
        AxArpMessage.TryParse(payload.Span, out var reply).Should().BeTrue();

        reply!.Operation.Should().Be(AxArpOperation.Reply);
        reply.SenderCallsign.Should().Be(XrouterCall);
        reply.ProtocolType.Should().Be(AxArpMessage.ProtocolTypeIp,
            "XRouter sends AX25_P_IP, as LinBPQ and the Linux kernel do");
    }

    /// <summary>
    /// Transmit until the answer arrives or the window closes.
    /// </summary>
    /// <remarks>
    /// Same reasoning as the LinBPQ suite: UI frames are unacknowledged, so one
    /// transmission is not a test, and retrying faster than a frame takes to
    /// send queues transmissions behind each other until the node is deaf.
    /// </remarks>
    private async Task<Ax25Frame?> SendUntilAsync(
        KissTcpClient kiss, Ax25Frame outbound, Task<Ax25Frame> answer, CancellationToken ct)
    {
        var bytes = outbound.ToBytes();
        var interval = TimeSpan.FromSeconds(20);

        while (!answer.IsCompleted && !ct.IsCancellationRequested)
        {
            output.WriteLine($"tx {outbound.Source.Callsign}>{outbound.Destination.Callsign} "
                + $"pid={(outbound.Pid is { } p ? $"0x{p:X2}" : "none")} {bytes.Length} on air");
            await kiss.SendFrameAsync(bytes, ct);

            var done = await Task.WhenAny(answer, Task.Delay(interval, ct));
            if (done == answer)
                return await answer;
        }

        return answer.IsCompletedSuccessfully ? await answer.ConfigureAwait(false) : null;
    }

    /// <summary>Read parsed frames off the transport, for the tests with no session layer.</summary>
    private CancellationTokenSource StartReader(KissTcpClient kiss, Action<Ax25Frame> onFrame, CancellationToken ct)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var inbound in ((Packet.Ax25.Transport.IAx25Transport)kiss).ReceiveAsync(stop.Token))
                {
                    if (!Ax25Frame.TryParse(inbound.Ax25.Span, out var frame) || frame is null)
                        continue;
                    output.WriteLine($"rx {frame.Source.Callsign}>{frame.Destination.Callsign} "
                        + $"pid={(frame.Pid is { } p ? $"0x{p:X2}" : "none")} {frame.Info.Length} bytes");
                    onFrame(frame);
                }
            }
            catch (OperationCanceledException)
            {
                // The window closed.
            }
        }, CancellationToken.None);
        return stop;
    }

    private async Task DumpLogsAsync()
    {
        output.WriteLine("=== netsim logs ===");
        output.WriteLine(await fixture.GetNetsimLogsAsync());
        output.WriteLine("=== xrouter logs ===");
        output.WriteLine(await fixture.GetXrouterLogsAsync());
    }

    /// <summary>The smallest ICMP builder that a real stack will route.</summary>
    private static class Icmp
    {
        public static byte[] EchoRequest(IPAddress source, IPAddress destination, ushort identifier, ushort sequence, ReadOnlySpan<byte> payload)
        {
            var icmp = new byte[8 + payload.Length];
            icmp[0] = 8;
            BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(4), identifier);
            BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(6), sequence);
            payload.CopyTo(icmp.AsSpan(8));
            BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(2), Checksum(icmp));

            var packet = new byte[20 + icmp.Length];
            packet[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), identifier);
            packet[8] = 64;
            packet[9] = IpPacket.ProtocolIcmp;
            source.TryWriteBytes(packet.AsSpan(12), out _);
            destination.TryWriteBytes(packet.AsSpan(16), out _);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20)));
            icmp.CopyTo(packet.AsSpan(20));

            return packet;
        }

        private static ushort Checksum(ReadOnlySpan<byte> data)
        {
            uint sum = 0;
            int i = 0;
            for (; i + 1 < data.Length; i += 2)
                sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            if (i < data.Length)
                sum += (uint)(data[i] << 8);
            while (sum >> 16 != 0)
                sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)~sum;
        }
    }
}
