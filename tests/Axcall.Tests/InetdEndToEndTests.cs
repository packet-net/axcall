using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Axinetd;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// axinetd end to end: a real caller over a real AX.25 session, answered by a
/// real program on the other end with the link on its standard input and
/// output. Only the radio is missing.
/// </summary>
public sealed class InetdEndToEndTests
{
    private static readonly Callsign Service = new("AXINET", 1);
    private static readonly Callsign Caller = new("AXCALR", 2);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs one call against the given rule and returns everything the caller
    /// received.
    /// </summary>
    private static async Task<string> CallAsync(InetdRule rule, string send, CancellationToken ct)
    {
        var (serviceSide, callerSide) = LoopbackTransport.CreatePair();

        var inbox = new SessionInbox();
        var listener = new Ax25Listener(serviceSide, new Ax25ListenerOptions
        {
            MyCall = Service,
            // The 3 s default would meter this test rather than test it.
            T2 = TimeSpan.Zero,
            ConfigureSession = inbox.Attach,
        });

        await using (listener.ConfigureAwait(false))
        {
            var log = new CapturingWriter();
            var rules = new Dictionary<Callsign, InetdRule> { [rule.Callsign] = rule };
            var server = new InboundServer(listener, inbox, rules, log, false);
            server.Start(ct);

            await listener.StartAsync(ct);
            listener.AcceptIncoming = true;

            var received = new MemoryStream();
            await using var caller = new SessionRelay(
                callerSide,
                Caller,
                null,
                null,
                new SessionRelayOptions
                {
                    Binary = true,
                    Silent = true,
                    AckDelay = TimeSpan.Zero,
                    // The service decides when the conversation is over: it
                    // hangs up when the program it ran exits.
                    WaitForRemoteDisconnect = true,
                },
                new CapturingWriter(),
                binaryInput: new MemoryStream(Encoding.ASCII.GetBytes(send)),
                binaryOutput: received);

            await caller.ConnectAndRelayAsync(Service, ct).WaitAsync(Budget, ct);
            await server.DrainAsync();

            return Encoding.ASCII.GetString(received.ToArray());
        }
    }

    private static InetdRule Shell(string script)
        => new(Service, null, InetdAction.Exec, "/bin/sh", ["-c", script]);

    [Fact]
    public async Task A_program_answers_the_call_with_the_link_on_its_stdin_and_stdout()
    {
        using var cts = new CancellationTokenSource(Budget);

        // Greet, read one line, answer it, exit. Exiting is what ends the call:
        // the program's stdout closes, and axinetd hangs up.
        var got = await CallAsync(
            Shell("printf 'welcome to the test service\\n'; read line; printf 'you said: %s\\n' \"$line\""),
            "ping\n",
            cts.Token);

        got.Should().Contain("welcome to the test service\n");
        got.Should().Contain("you said: ping\n");
    }

    /// <summary>
    /// The one thing a service knows about who is on the other end, without
    /// having to be told in its arguments.
    /// </summary>
    [Fact]
    public async Task The_caller_is_in_the_environment()
    {
        using var cts = new CancellationTokenSource(Budget);

        var got = await CallAsync(Shell("printf 'caller=%s\\n' \"$AX25_CALLER\""), "", cts.Token);

        got.Should().Contain("caller=AXCALR-2");
    }

    [Fact]
    public async Task The_caller_is_substituted_into_the_arguments()
    {
        using var cts = new CancellationTokenSource(Budget);

        var rule = new InetdRule(Service, null, InetdAction.Exec, "/bin/sh",
            ["-c", "printf 'arg=%s\\n' \"$1\"", "sh", "%r"]);

        (await CallAsync(rule, "", cts.Token)).Should().Contain("arg=AXCALR-2");
    }

    /// <summary>
    /// Standard error is the operator's, not the caller's. Sending it down the
    /// link would leak paths and stack traces to a stranger and interleave them
    /// with whatever the program meant to say.
    /// </summary>
    [Fact]
    public async Task What_a_program_writes_to_stderr_does_not_go_down_the_link()
    {
        using var cts = new CancellationTokenSource(Budget);

        var got = await CallAsync(
            Shell("printf 'visible\\n'; printf 'secret path /etc/shadow\\n' >&2"),
            "",
            cts.Token);

        got.Should().Contain("visible\n");
        got.Should().NotContain("secret");
    }

    [Fact]
    public async Task A_tcp_rule_hands_the_call_to_a_local_service()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;

        // A local service that greets and then echoes, the shape of most things
        // anyone would point this at.
        using var local = new TcpListener(IPAddress.Loopback, 0);
        local.Start();
        var endpoint = (IPEndPoint)local.LocalEndpoint;

        var serving = Task.Run(async () =>
        {
            using var client = await local.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("local service here\n"), ct);
            await stream.FlushAsync(ct);

            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"echo: {Encoding.ASCII.GetString(buffer, 0, read)}"), ct);
            await stream.FlushAsync(ct);
            // Closing is what tells axinetd the conversation is over.
        }, CancellationToken.None);

        var rule = new InetdRule(Service, null, InetdAction.Tcp, $"127.0.0.1:{endpoint.Port}", []);
        var got = await CallAsync(rule, "knock\n", ct);

        await serving.WaitAsync(Budget, ct);
        local.Stop();

        got.Should().Contain("local service here\n");
        got.Should().Contain("echo: knock\n");
    }

    [Fact]
    public async Task A_bulk_reply_is_not_truncated_when_the_program_exits()
    {
        using var cts = new CancellationTokenSource(Budget);

        // 4 kB out of the program and straight into an exit, which is the
        // truncation case: without a flush before the hangup this arrives as
        // one window and the rest is lost.
        var got = await CallAsync(
            Shell("i=0; while [ $i -lt 128 ]; do printf '0123456789abcdefghijklmnopqrstuv\\n'; i=$((i+1)); done"),
            "",
            cts.Token);

        got.Length.Should().Be(128 * 33);
    }
}
