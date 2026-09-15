using System.Buffers.Binary;
using System.Net;
using AwesomeAssertions;
using Axcall;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss;
using Xunit;
using Xunit.Abstractions;

namespace Axcall.Tests.Integration;

/// <summary>
/// IP over AX.25, against LinBPQ, on a simulated 1200 baud channel.
/// </summary>
/// <remarks>
/// <para>
/// The encapsulation is the easy part; what makes this worth testing is the
/// behaviour of an implementation that has been on the air for thirty years.
/// Every assertion here started as a claim in a man page and is now an
/// observation, and two of them came out differently from the reading.
/// </para>
/// <para>
/// Everything is built with the production codec, not with hand-written bytes,
/// so a change to <see cref="AxArpMessage"/> or <see cref="Ax25Ip"/> that a
/// real peer would reject fails here.
/// </para>
/// </remarks>
[Collection(InteropCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IpOverAx25Tests
{
    private static readonly Callsign OurCall = new("AXTEST");
    private static readonly Callsign LinbpqCall = new("PN0TST");

    /// <summary>Our address, which the node has a static ARP entry for.</summary>
    private static readonly IPAddress OurAddress = IPAddress.Parse("44.131.20.1");

    /// <summary>The node's own address.</summary>
    private static readonly IPAddress LinbpqAddress = IPAddress.Parse("44.131.20.2");

    /// <summary>Off-net, so a packet from it has to be routed rather than consumed.</summary>
    private static readonly IPAddress Elsewhere = IPAddress.Parse("44.131.99.9");

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The channel the harness simulates.</summary>
    private const double ChannelBitsPerSecond = 1200;

    /// <summary>
    /// How long to leave between asking again, for a frame of this size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here travels in UI frames, which are unacknowledged, so
    /// asking once is not a test of anything: losing a frame is the protocol
    /// behaving normally. But asking again on a fixed short timer is worse
    /// than not asking at all, and the CI runner proved it. A 316-byte frame
    /// is two seconds of air time at 1200 baud, and the simulator does not run
    /// faster than real time under load. Retrying every ten seconds queued
    /// transmissions behind each other until the node was keying almost
    /// continuously, and a half-duplex node that is transmitting is deaf: the
    /// peer answered, and nothing was listening. The channel log showed both
    /// fragments leaving the peer and neither arriving.
    /// </para>
    /// <para>
    /// So the interval is the frame's own air time with a large factor on it,
    /// which is what makes a retry a second chance rather than a denial of
    /// service against ourselves.
    /// </para>
    /// </remarks>
    private static TimeSpan RetryIntervalFor(int frameBytes)
        => TimeSpan.FromSeconds(Math.Max(20, frameBytes * 8 / ChannelBitsPerSecond * 8));

    private readonly InteropFixture fixture;
    private readonly ITestOutputHelper output;

    public IpOverAx25Tests(InteropFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact]
    public async Task Linbpq_Answers_An_Ax25_Arp_Request()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await using var kiss = await KissTcpClient.ConnectAsync(InteropFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);

        var request = new AxArpMessage(
            AxArpOperation.Request,
            OurCall,
            OurAddress,
            AxArpMessage.NoCallsign,
            LinbpqAddress);

        var reply = await ExchangeAsync(
            kiss,
            Ax25Ip.Arp(Ax25Ip.Broadcast, OurCall, request.ToBytes()),
            frame => Ax25Ip.TryGetPayload(frame, Ax25Pid.Arp, out _),
            cts.Token);

        if (reply is null) await DumpLogsAsync();
        reply.Should().NotBeNull("LinBPQ should answer an ARP request broadcast to QST");

        Ax25Ip.TryGetPayload(reply!, Ax25Pid.Arp, out var payload).Should().BeTrue();
        AxArpMessage.TryParse(payload.Span, out var answer).Should().BeTrue("the reply should parse as an AX.25 ARP message");

        answer!.Operation.Should().Be(AxArpOperation.Reply);
        answer.SenderCallsign.Should().Be(LinbpqCall);
        answer.SenderAddress.Should().Be(LinbpqAddress);
        answer.TargetCallsign.Should().Be(OurCall);
        answer.TargetAddress.Should().Be(OurAddress);
    }

    /// <summary>
    /// The ARP protocol type field is the one place two live implementations
    /// disagree: the Linux kernel fills it in from the generic ARP code and
    /// sends ETH_P_IP, LinBPQ sends the AX.25 PID widened to sixteen bits.
    /// </summary>
    /// <remarks>
    /// This asks with each of them in turn. LinBPQ answers both and hands back
    /// whatever it was asked with, which is why <see cref="AxArpMessage"/>
    /// carries the field rather than assuming a value, and why nothing here
    /// checks it on receive.
    /// </remarks>
    [Theory]
    [InlineData(AxArpMessage.ProtocolTypeIp)]
    [InlineData(AxArpMessage.ProtocolTypeBpq)]
    public async Task Linbpq_Ignores_The_Arp_Protocol_Type_And_Reflects_It(ushort protocolType)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await using var kiss = await KissTcpClient.ConnectAsync(InteropFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);

        var request = new AxArpMessage(
            AxArpOperation.Request,
            OurCall,
            OurAddress,
            AxArpMessage.NoCallsign,
            LinbpqAddress,
            protocolType);

        var reply = await ExchangeAsync(
            kiss,
            Ax25Ip.Arp(Ax25Ip.Broadcast, OurCall, request.ToBytes()),
            frame => Ax25Ip.TryGetPayload(frame, Ax25Pid.Arp, out _),
            cts.Token);

        if (reply is null) await DumpLogsAsync();
        reply.Should().NotBeNull($"LinBPQ should answer an ARP request whatever the protocol type, and 0x{protocolType:X4} is one of the two in the field");

        Ax25Ip.TryGetPayload(reply!, Ax25Pid.Arp, out var payload).Should().BeTrue();
        AxArpMessage.TryParse(payload.Span, out var answer).Should().BeTrue();
        answer!.ProtocolType.Should().Be(protocolType, "LinBPQ replies in place, so the field comes back as it went out");
    }

    /// <summary>
    /// A ping, the whole way: UI frame, PID 0xCC, into LinBPQ's IP stack, out
    /// to the container's own kernel over its TAP device, and all the way back.
    /// </summary>
    [Fact]
    public async Task Linbpq_Answers_An_Icmp_Echo_Carried_In_A_Ui_Frame()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await using var kiss = await KissTcpClient.ConnectAsync(InteropFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);

        var request = Icmp.EchoRequest(OurAddress, LinbpqAddress, identifier: 0x4158, sequence: 1, "axcall interop"u8);

        var reply = await ExchangeAsync(
            kiss,
            Ax25Ip.Datagram(LinbpqCall, OurCall, request),
            frame => Ax25Ip.TryGetPayload(frame, Ax25Pid.Ip, out _),
            cts.Token);

        if (reply is null) await DumpLogsAsync();
        reply.Should().NotBeNull("LinBPQ should answer a ping sent to its own address");

        Ax25Ip.TryGetPayload(reply!, Ax25Pid.Ip, out var payload).Should().BeTrue();
        IpPacket.TryRead(payload.Span, out var ip).Should().BeTrue();

        ip.Protocol.Should().Be(IpPacket.ProtocolIcmp);
        ip.Source.Should().Be(LinbpqAddress);
        ip.Destination.Should().Be(OurAddress);
        Icmp.TypeOf(payload.Span).Should().Be(Icmp.EchoReply);
    }

    /// <summary>
    /// What a peer will send us when the packet does not fit, which decides
    /// what our receive path has to tolerate.
    /// </summary>
    /// <remarks>
    /// The reading said NOS-style segmentation with PID 0x08, which LinBPQ does
    /// implement on receive. It does not use it here: what comes back is
    /// ordinary IP fragmentation, MF bit and fragment offset, in two UI frames
    /// with PID 0xCC, split so that no IP packet exceeds 252 bytes. That number
    /// is a hardcoded 256 in LinBPQ rounded down to an eight-byte boundary, and
    /// it pays no attention to the PACLEN configured on the port, which is 120
    /// here.
    /// </remarks>
    [Fact]
    public async Task Linbpq_Splits_A_Long_Packet_With_Ip_Fragmentation_Not_Segmentation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await using var kiss = await KissTcpClient.ConnectAsync(InteropFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);

        // From off-net to us, so LinBPQ routes it back over the air rather than
        // consuming it, and we see exactly what it puts on the channel.
        var payload = new byte[272];
        Random.Shared.NextBytes(payload);
        var packet = Icmp.EchoRequest(Elsewhere, OurAddress, identifier: 0x4159, sequence: 2, payload);
        packet.Length.Should().Be(300, "the point is a packet longer than LinBPQ will send in one frame");

        var fragments = await CollectAsync(
            kiss,
            Ax25Ip.Datagram(LinbpqCall, OurCall, packet),
            frame => Ax25Ip.TryGetPayload(frame, Ax25Pid.Ip, out _),
            count: 2,
            cts.Token);

        if (fragments.Count != 2) await DumpLogsAsync();
        fragments.Should().HaveCount(2, "LinBPQ should split a 300 byte packet into two");

        foreach (var fragment in fragments)
        {
            fragment.Pid.Should().Be(Ax25Pid.Ip, "the split is at the IP layer, not AX.25 segmentation with PID 0x08");
        }

        Ax25Ip.TryGetPayload(fragments[0], Ax25Pid.Ip, out var first).Should().BeTrue();
        Ax25Ip.TryGetPayload(fragments[1], Ax25Pid.Ip, out var second).Should().BeTrue();

        IpPacket.TryRead(first.Span, out var head).Should().BeTrue();
        IpPacket.TryRead(second.Span, out var tail).Should().BeTrue();

        head.MoreFragments.Should().BeTrue();
        head.FragmentOffset.Should().Be(0);
        head.TotalLength.Should().Be(252, "LinBPQ splits at a hardcoded 256 rounded down to an eight byte boundary");

        tail.MoreFragments.Should().BeFalse();
        tail.FragmentOffset.Should().Be(232, "the first fragment carried 232 bytes of data");
        tail.TotalLength.Should().Be(68);
    }

    /// <summary>
    /// The ceiling <see cref="Ax25Ip.MaxFrameBytes"/> names, demonstrated.
    /// </summary>
    /// <remarks>
    /// LinBPQ discards a KISS frame over 329 bytes including the type byte,
    /// silently: nothing on the air, nothing in its log. The oversize frame
    /// below is carried to its port by the channel simulator and vanishes. The
    /// second half of the test sends a frame that does fit, and gets an answer,
    /// which is what makes the silence evidence rather than a dead link.
    /// </remarks>
    [Fact]
    public async Task Linbpq_Discards_A_Frame_Over_The_Size_It_Will_Accept()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await using var kiss = await KissTcpClient.ConnectAsync(InteropFixture.NetsimHost, fixture.NetsimKissPort, cts.Token);

        var tooBig = Icmp.EchoRequest(OurAddress, LinbpqAddress, 0x415A, 3, new byte[400]);
        var oversize = Ax25Ip.Datagram(LinbpqCall, OurCall, tooBig);
        oversize.ToBytes().Length.Should().BeGreaterThan(Ax25Ip.MaxFrameBytes);

        // Sent once, not retried. It is the longest thing any of these tests
        // puts on the channel, it is expected to be ignored, and repeating it
        // only takes air time away from the half of the test that has to work.
        var ignored = await ExchangeAsync(
            kiss,
            oversize,
            frame => Ax25Ip.TryGetPayload(frame, Ax25Pid.Ip, out _),
            cts.Token,
            timeout: TimeSpan.FromSeconds(25),
            retry: false);

        ignored.Should().BeNull("a frame over the size LinBPQ accepts is discarded without a word");

        // And the link was alive the whole time.
        var fits = Icmp.EchoRequest(OurAddress, LinbpqAddress, 0x415B, 4, "still here"u8);
        var answered = await ExchangeAsync(
            kiss,
            Ax25Ip.Datagram(LinbpqCall, OurCall, fits),
            frame => Ax25Ip.TryGetPayload(frame, Ax25Pid.Ip, out _),
            cts.Token);

        if (answered is null) await DumpLogsAsync();
        answered.Should().NotBeNull("a frame within the limit is still answered, so the silence above was the size");
    }

    /// <summary>
    /// What the containers saw, for a failure on a machine that is not this one.
    /// </summary>
    /// <remarks>
    /// net-sim logs every frame it carries and which node heard it, which is
    /// the difference between "the peer ignored us" and "it never arrived".
    /// Without this, a silent failure on a runner is unfalsifiable.
    /// </remarks>
    private async Task DumpLogsAsync()
    {
        output.WriteLine("=== netsim logs ===");
        output.WriteLine(await fixture.GetNetsimLogsAsync().ConfigureAwait(false));
        output.WriteLine("=== linbpq logs ===");
        output.WriteLine(await fixture.GetLinbpqLogsAsync().ConfigureAwait(false));
    }

    /// <summary>Send one frame and wait for the first reply that matches.</summary>
    private async Task<Ax25Frame?> ExchangeAsync(
        KissTcpClient kiss,
        Ax25Frame outbound,
        Func<Ax25Frame, bool> matches,
        CancellationToken ct,
        TimeSpan? timeout = null,
        bool retry = true)
    {
        var found = await CollectAsync(kiss, outbound, matches, count: 1, ct, timeout, retry).ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>Send one frame and collect replies until there are enough, or time runs out.</summary>
    private async Task<IReadOnlyList<Ax25Frame>> CollectAsync(
        KissTcpClient kiss,
        Ax25Frame outbound,
        Func<Ax25Frame, bool> matches,
        int count,
        CancellationToken ct,
        TimeSpan? timeout = null,
        bool retry = true)
    {
        var collected = new List<Ax25Frame>();

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(timeout ?? ReplyTimeout);

        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var inbound in ((IAx25Transport)kiss).ReceiveAsync(window.Token).ConfigureAwait(false))
                {
                    if (!Ax25Frame.TryParse(inbound.Ax25.Span, out var frame) || frame is null)
                        continue;

                    output.WriteLine($"rx {frame.Source.Callsign}>{frame.Destination.Callsign} "
                        + $"pid={(frame.Pid is { } pid ? $"0x{pid:X2}" : "none")} {frame.Info.Length} bytes");

                    if (!matches(frame))
                        continue;

                    collected.Add(frame);
                    if (collected.Count >= count)
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                // The window closed. Whatever arrived is the answer.
            }
        }, CancellationToken.None);

        // The reader has to be listening before the question goes out, or a
        // fast answer on a slow channel is missed.
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);

        var bytes = outbound.ToBytes();
        var label = $"tx {outbound.Source.Callsign}>{outbound.Destination.Callsign} "
            + $"pid={(outbound.Pid is { } p ? $"0x{p:X2}" : "none")} {outbound.Info.Length} bytes "
            + $"({bytes.Length} on air)";

        var interval = RetryIntervalFor(bytes.Length);

        while (!reader.IsCompleted && !window.IsCancellationRequested)
        {
            output.WriteLine(label);
            await kiss.SendFrameAsync(bytes, ct).ConfigureAwait(false);

            if (!retry)
                break;

            var elapsed = await Task.WhenAny(reader, Task.Delay(interval, window.Token)).ConfigureAwait(false);
            if (elapsed == reader)
                break;
        }

        await reader.ConfigureAwait(false);
        return collected;
    }

    /// <summary>The smallest ICMP builder that will make a real stack answer.</summary>
    private static class Icmp
    {
        public const byte EchoReply = 0;
        public const byte EchoRequestType = 8;

        public static byte TypeOf(ReadOnlySpan<byte> packet) => packet[20];

        public static byte[] EchoRequest(IPAddress source, IPAddress destination, ushort identifier, ushort sequence, ReadOnlySpan<byte> payload)
        {
            var icmp = new byte[8 + payload.Length];
            icmp[0] = EchoRequestType;
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
