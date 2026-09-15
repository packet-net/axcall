using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Axsocks;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// axsocks end to end: a real TCP client, the real SOCKS handshake, a real
/// AX.25 session and a real station on the other end of it. Only the radio is
/// missing.
/// </summary>
public sealed class ProxyEndToEndTests
{
    private static readonly Callsign Station = new("AXSTN", 1);
    private static readonly Callsign Proxy = new("AXPRX", 7);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A station that greets whoever calls it and then stays on the link until
    /// the caller hangs up, keeping whatever it was sent.
    /// </summary>
    private static SessionRelay BuildStation(LoopbackTransport transport, byte[] banner, Stream received)
        => new(
            transport,
            Station,
            null,
            null,
            new SessionRelayOptions
            {
                Binary = true,
                Silent = true,
                // The 3 s default would meter this test rather than test it.
                AckDelay = TimeSpan.Zero,
                WaitForRemoteDisconnect = true,
            },
            new CapturingWriter(),
            binaryInput: new MemoryStream(banner),
            binaryOutput: received);

    private static (Ax25Listener Listener, SessionInbox Inbox) BuildProxyLink(LoopbackTransport transport)
    {
        var inbox = new SessionInbox();
        var listener = new Ax25Listener(transport, new Ax25ListenerOptions
        {
            MyCall = Proxy,
            T2 = TimeSpan.Zero,
            ConfigureSession = inbox.Attach,
        });
        return (listener, inbox);
    }

    private static async Task<NetworkStream> HandshakeAsync(TcpClient client, string host, CancellationToken ct)
    {
        var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var method = new byte[2];
        await stream.ReadExactlyAsync(method, ct);
        method.Should().Equal([0x05, 0x00]);

        byte[] request = [0x05, 0x01, 0x00, 0x03, (byte)host.Length, .. Encoding.ASCII.GetBytes(host), 0x00, 0x50];
        await stream.WriteAsync(request, ct);

        return stream;
    }

    private static async Task<byte> ReadReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        var reply = new byte[10];
        await stream.ReadExactlyAsync(reply, ct);
        reply[0].Should().Be(0x05);
        return reply[1];
    }

    [Fact]
    public async Task Bytes_cross_in_both_directions_and_the_link_closes_cleanly()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (stationSide, proxySide) = LoopbackTransport.CreatePair();

        var banner = Encoding.ASCII.GetBytes("Welcome to the test station\r");
        var received = new MemoryStream();

        await using var station = BuildStation(stationSide, banner, received);
        var stationTask = Task.Run(() => station.ListenAndRelayAsync(ct), CancellationToken.None);
        await Task.Delay(200, ct);

        var (listener, inbox) = BuildProxyLink(proxySide);
        await using (listener.ConfigureAwait(false))
        {
            await listener.StartAsync(ct);
            listener.AcceptIncoming = false;

            var log = new CapturingWriter();
            var server = new ProxyServer(listener, inbox, new Dictionary<string, HostEntry>(), "test", log, false);
            using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var serverTask = Task.Run(
                () => server.RunAsync(new IPEndPoint(IPAddress.Loopback, 0), serverCts.Token),
                CancellationToken.None);

            var bound = await server.Bound.WaitAsync(ct);

            byte[] greeting;
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(bound, ct);

                // The station's callsign is a perfectly good hostname, so this
                // works with no hosts file at all.
                var stream = await HandshakeAsync(client, "AXSTN-1", ct);
                (await ReadReplyAsync(stream, ct)).Should().Be(0x00);

                greeting = new byte[banner.Length];
                await stream.ReadExactlyAsync(greeting, ct);

                await stream.WriteAsync(Encoding.ASCII.GetBytes("hello from the socket\r"), ct);
                await stream.FlushAsync(ct);

                // End of input, which is the proxy's cue to flush and hang up.
                client.Client.Shutdown(SocketShutdown.Send);

                await stationTask.WaitAsync(Budget, ct);
            }

            greeting.Should().Equal(banner);
            Encoding.ASCII.GetString(received.ToArray()).Should().Be("hello from the socket\r");

            await serverCts.CancelAsync();
            await serverTask.WaitAsync(Budget, CancellationToken.None);
        }
    }

    /// <summary>
    /// More than one window of data, sent and then immediately followed by the
    /// client closing its end. The proxy has to flush what is queued before it
    /// hangs up, or the transfer is silently truncated at one window; that is a
    /// bug this code base has had once already, on the terminal side.
    /// </summary>
    [Fact]
    public async Task A_bulk_transfer_is_not_truncated_when_the_client_closes()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (stationSide, proxySide) = LoopbackTransport.CreatePair();

        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        var received = new MemoryStream();

        await using var station = BuildStation(stationSide, [], received);
        var stationTask = Task.Run(() => station.ListenAndRelayAsync(ct), CancellationToken.None);
        await Task.Delay(200, ct);

        var (listener, inbox) = BuildProxyLink(proxySide);
        await using (listener.ConfigureAwait(false))
        {
            await listener.StartAsync(ct);
            listener.AcceptIncoming = false;

            var server = new ProxyServer(listener, inbox, new Dictionary<string, HostEntry>(), "test", new CapturingWriter(), false);
            using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var serverTask = Task.Run(
                () => server.RunAsync(new IPEndPoint(IPAddress.Loopback, 0), serverCts.Token),
                CancellationToken.None);

            var bound = await server.Bound.WaitAsync(ct);

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(bound, ct);
                var stream = await HandshakeAsync(client, "AXSTN-1", ct);
                (await ReadReplyAsync(stream, ct)).Should().Be(0x00);

                await stream.WriteAsync(payload, ct);
                await stream.FlushAsync(ct);
                client.Client.Shutdown(SocketShutdown.Send);

                await stationTask.WaitAsync(Budget, ct);
            }

            received.ToArray().Should().Equal(payload);

            await serverCts.CancelAsync();
            await serverTask.WaitAsync(Budget, CancellationToken.None);
        }
    }

    /// <summary>
    /// One AX.25 session exists per callsign pair, so a second conversation
    /// with the same station would share the first one's link and interleave
    /// with it. Refusing is the only honest answer.
    /// </summary>
    [Fact]
    public async Task A_second_call_to_a_station_already_in_session_is_refused()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (stationSide, proxySide) = LoopbackTransport.CreatePair();

        await using var station = BuildStation(stationSide, Encoding.ASCII.GetBytes("hi\r"), new MemoryStream());
        var stationTask = Task.Run(() => station.ListenAndRelayAsync(ct), CancellationToken.None);
        await Task.Delay(200, ct);

        var (listener, inbox) = BuildProxyLink(proxySide);
        await using (listener.ConfigureAwait(false))
        {
            await listener.StartAsync(ct);
            listener.AcceptIncoming = false;

            var log = new CapturingWriter();
            var server = new ProxyServer(listener, inbox, new Dictionary<string, HostEntry>(), "test", log, false);
            using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var serverTask = Task.Run(
                () => server.RunAsync(new IPEndPoint(IPAddress.Loopback, 0), serverCts.Token),
                CancellationToken.None);

            var bound = await server.Bound.WaitAsync(ct);

            using var first = new TcpClient();
            await first.ConnectAsync(bound, ct);
            var firstStream = await HandshakeAsync(first, "AXSTN-1", ct);
            (await ReadReplyAsync(firstStream, ct)).Should().Be(0x00);

            using (var second = new TcpClient())
            {
                await second.ConnectAsync(bound, ct);
                var secondStream = await HandshakeAsync(second, "AXSTN-1", ct);
                (await ReadReplyAsync(secondStream, ct)).Should().Be(0x01);
            }

            log.Snapshot().Should().Contain("one connection at a time");

            first.Client.Shutdown(SocketShutdown.Send);
            await stationTask.WaitAsync(Budget, ct);

            await serverCts.CancelAsync();
            await serverTask.WaitAsync(Budget, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_name_that_is_neither_a_host_nor_a_callsign_is_unreachable()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (_, proxySide) = LoopbackTransport.CreatePair();

        var (listener, inbox) = BuildProxyLink(proxySide);
        await using (listener.ConfigureAwait(false))
        {
            await listener.StartAsync(ct);
            listener.AcceptIncoming = false;

            var server = new ProxyServer(listener, inbox, new Dictionary<string, HostEntry>(), "test", new CapturingWriter(), false);
            using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var serverTask = Task.Run(
                () => server.RunAsync(new IPEndPoint(IPAddress.Loopback, 0), serverCts.Token),
                CancellationToken.None);

            var bound = await server.Bound.WaitAsync(ct);

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(bound, ct);
                var stream = await HandshakeAsync(client, "example.com", ct);
                (await ReadReplyAsync(stream, ct)).Should().Be(0x04);
            }

            await serverCts.CancelAsync();
            await serverTask.WaitAsync(Budget, CancellationToken.None);
        }
    }
}
