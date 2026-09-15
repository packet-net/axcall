using System.Buffers.Binary;
using System.Text;

namespace Axsocks;

/// <summary>The REP codes of RFC 1928 section 6, as sent back to the client.</summary>
internal enum Socks5Reply : byte
{
    Succeeded = 0x00,
    GeneralFailure = 0x01,
    NotAllowed = 0x02,
    NetworkUnreachable = 0x03,
    HostUnreachable = 0x04,
    ConnectionRefused = 0x05,
    TtlExpired = 0x06,
    CommandNotSupported = 0x07,
    AddressTypeNotSupported = 0x08,
}

/// <summary>What the client asked for: a name and a port.</summary>
internal sealed record Socks5Request(string Host, int Port);

/// <summary>
/// The outcome of the opening exchange: a request to act on, or a reason not
/// to.
/// </summary>
/// <param name="Request">The CONNECT to act on, null when there is none.</param>
/// <param name="Reply">The code to send back when there is one to send.</param>
/// <param name="Error">What went wrong, for the log.</param>
/// <param name="CanReply">
/// False when the client is not speaking SOCKS5 at all. There is nothing
/// useful to say to it, and saying it in SOCKS would be its own kind of
/// confusing, so the connection is simply closed.
/// </param>
internal readonly record struct Socks5Result(
    Socks5Request? Request,
    Socks5Reply Reply,
    string? Error,
    bool CanReply)
{
    public static Socks5Result Ok(Socks5Request request) => new(request, Socks5Reply.Succeeded, null, true);

    public static Socks5Result Refuse(Socks5Reply reply, string error) => new(null, reply, error, true);

    public static Socks5Result Unusable(string error) => new(null, Socks5Reply.GeneralFailure, error, false);
}

/// <summary>
/// The client-facing half of RFC 1928, which is all axsocks needs: no
/// authentication, and CONNECT to a name.
/// </summary>
/// <remarks>
/// Only the hostname address type is accepted. An IPv4 or IPv6 address means
/// the client resolved the name itself before connecting, and there is nothing
/// useful to be done with the result: a callsign is not an IP address and no
/// mapping back exists. That is why the documented invocations all push
/// resolution to the proxy, with curl's <c>--socks5-hostname</c> and netcat's
/// <c>-X 5 -x</c>.
/// </remarks>
internal static class Socks5
{
    public const byte Version = 0x05;

    private const byte MethodNoAuth = 0x00;
    private const byte MethodNone = 0xFF;
    private const byte CommandConnect = 0x01;
    private const byte AddressIpv4 = 0x01;
    private const byte AddressDomain = 0x03;
    private const byte AddressIpv6 = 0x04;

    /// <summary>
    /// Greet the client, agree on no authentication, and read its request.
    /// </summary>
    public static async Task<Socks5Result> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[2];

        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

            if (header[0] != Version)
                return Socks5Result.Unusable($"not SOCKS5: first byte was 0x{header[0]:X2}");

            var methods = new byte[header[1]];
            if (methods.Length > 0)
                await stream.ReadExactlyAsync(methods, ct).ConfigureAwait(false);

            if (Array.IndexOf(methods, MethodNoAuth) < 0)
            {
                await stream.WriteAsync(new byte[] { Version, MethodNone }, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return Socks5Result.Unusable("client offered no authentication method we support");
            }

            await stream.WriteAsync(new byte[] { Version, MethodNoAuth }, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            // VER CMD RSV ATYP
            var request = new byte[4];
            await stream.ReadExactlyAsync(request, ct).ConfigureAwait(false);

            if (request[0] != Version)
                return Socks5Result.Unusable($"bad request version 0x{request[0]:X2}");

            if (request[1] != CommandConnect)
                return Socks5Result.Refuse(Socks5Reply.CommandNotSupported,
                    $"only CONNECT is supported, not command 0x{request[1]:X2}");

            string host;
            switch (request[3])
            {
                case AddressDomain:
                    var length = new byte[1];
                    await stream.ReadExactlyAsync(length, ct).ConfigureAwait(false);
                    if (length[0] == 0)
                        return Socks5Result.Refuse(Socks5Reply.GeneralFailure, "empty hostname");
                    var name = new byte[length[0]];
                    await stream.ReadExactlyAsync(name, ct).ConfigureAwait(false);
                    host = Encoding.ASCII.GetString(name);
                    break;

                case AddressIpv4:
                case AddressIpv6:
                    // Drain the address and port so the refusal is the only
                    // thing left in the conversation, then say why.
                    var address = new byte[request[3] == AddressIpv4 ? 4 + 2 : 16 + 2];
                    await stream.ReadExactlyAsync(address, ct).ConfigureAwait(false);
                    return Socks5Result.Refuse(Socks5Reply.AddressTypeNotSupported,
                        "the client resolved the name itself; use a form that lets the proxy resolve it, "
                        + "such as curl --socks5-hostname or nc -X 5 -x");

                default:
                    return Socks5Result.Refuse(Socks5Reply.AddressTypeNotSupported,
                        $"unknown address type 0x{request[3]:X2}");
            }

            var port = new byte[2];
            await stream.ReadExactlyAsync(port, ct).ConfigureAwait(false);

            return Socks5Result.Ok(new Socks5Request(host, BinaryPrimitives.ReadUInt16BigEndian(port)));
        }
        catch (EndOfStreamException)
        {
            return Socks5Result.Unusable("client went away mid-handshake");
        }
    }

    /// <summary>
    /// Send a reply. The bound address is always 0.0.0.0:0: there is no
    /// socket on this side of the link to report, and no client in practice
    /// looks at it for a CONNECT.
    /// </summary>
    public static async Task WriteReplyAsync(Stream stream, Socks5Reply reply, CancellationToken ct)
    {
        byte[] response = [Version, (byte)reply, 0x00, AddressIpv4, 0, 0, 0, 0, 0, 0];
        await stream.WriteAsync(response, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
