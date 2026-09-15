using System.Text;
using AwesomeAssertions;
using Axsocks;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// The opening exchange of RFC 1928, which is the only part of SOCKS axsocks
/// implements and the only part every client uses.
/// </summary>
public sealed class Socks5Tests
{
    private static byte[] Greeting(params byte[] methods)
        => [0x05, (byte)methods.Length, .. methods];

    private static byte[] ConnectToName(string host, ushort port)
        => [0x05, 0x01, 0x00, 0x03, (byte)host.Length, .. Encoding.ASCII.GetBytes(host),
            (byte)(port >> 8), (byte)(port & 0xFF)];

    private static async Task<(Socks5Result Result, byte[] Written)> ExchangeAsync(params byte[][] fromClient)
    {
        var inbound = new MemoryStream(fromClient.SelectMany(x => x).ToArray());
        var outbound = new MemoryStream();
        var result = await Socks5.ReadRequestAsync(new DuplexStream(inbound, outbound), CancellationToken.None);
        return (result, outbound.ToArray());
    }

    [Fact]
    public async Task Accepts_a_connect_to_a_name()
    {
        var (result, written) = await ExchangeAsync(Greeting(0x00), ConnectToName("gb7rdg", 80));

        result.Request.Should().Be(new Socks5Request("gb7rdg", 80));
        // Method selection: version 5, no authentication.
        written.Should().Equal([0x05, 0x00]);
    }

    [Fact]
    public async Task Picks_no_auth_out_of_several_offered_methods()
    {
        var (result, written) = await ExchangeAsync(Greeting(0x02, 0x01, 0x00), ConnectToName("lab", 22));

        result.Request.Should().NotBeNull();
        written.Should().Equal([0x05, 0x00]);
    }

    [Fact]
    public async Task Refuses_a_client_that_will_not_go_unauthenticated()
    {
        var (result, written) = await ExchangeAsync(Greeting(0x02));

        result.Request.Should().BeNull();
        result.CanReply.Should().BeFalse();
        // 0xFF is "no acceptable methods", which tells the client to give up
        // rather than leaving it waiting.
        written.Should().Equal([0x05, 0xFF]);
    }

    /// <summary>
    /// The important refusal. An IPv4 address means the client resolved the
    /// name itself, and there is no way back from an address to a callsign, so
    /// the only useful thing to do is say so rather than guess.
    /// </summary>
    [Fact]
    public async Task Refuses_an_address_the_client_resolved_itself()
    {
        byte[] connectToIp = [0x05, 0x01, 0x00, 0x01, 44, 131, 4, 1, 0x00, 0x50];
        var (result, _) = await ExchangeAsync(Greeting(0x00), connectToIp);

        result.Request.Should().BeNull();
        result.CanReply.Should().BeTrue();
        result.Reply.Should().Be(Socks5Reply.AddressTypeNotSupported);
        result.Error.Should().Contain("socks5-hostname");
    }

    [Fact]
    public async Task Refuses_bind_and_associate()
    {
        byte[] bind = [0x05, 0x02, 0x00, 0x03, 0x03, (byte)'l', (byte)'a', (byte)'b', 0x00, 0x50];
        var (result, _) = await ExchangeAsync(Greeting(0x00), bind);

        result.Reply.Should().Be(Socks5Reply.CommandNotSupported);
    }

    [Fact]
    public async Task Ignores_something_that_is_not_socks_at_all()
    {
        // An HTTP request arriving on the SOCKS port, which is what happens
        // when someone sets http_proxy instead of a SOCKS proxy.
        var (result, written) = await ExchangeAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));

        result.Request.Should().BeNull();
        result.CanReply.Should().BeFalse();
        written.Should().BeEmpty();
    }

    [Fact]
    public async Task Survives_a_client_that_vanishes_mid_handshake()
    {
        var (result, _) = await ExchangeAsync(Greeting(0x00), [0x05, 0x01, 0x00, 0x03, 0x06, (byte)'g']);

        result.Request.Should().BeNull();
        result.Error.Should().Contain("went away");
    }

    [Fact]
    public async Task Reply_is_the_shape_clients_expect()
    {
        var written = new MemoryStream();
        await Socks5.WriteReplyAsync(written, Socks5Reply.Succeeded, CancellationToken.None);

        // VER REP RSV ATYP=IPv4 0.0.0.0 port 0.
        written.ToArray().Should().Equal([0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0]);
    }
}

/// <summary>Reads from one stream and writes to another, as a socket does.</summary>
internal sealed class DuplexStream(Stream read, Stream write) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => read.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => write.WriteAsync(buffer, cancellationToken);

    public override void Flush() => write.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
