using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Axcall.Tests.Integration.Peers;

/// <summary>
/// XRouter on a simulated channel, for the on-demand peer suite.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="InteropFixture"/> rather than folded into it. The
/// LinBPQ fixture runs on every push and has to stay cheap; this one builds a
/// derived image, needs the network to do it, and exists to answer questions
/// that change about once a year. See the peer suite notes in
/// docs/ip-over-ax25.md.
/// </para>
/// <para>
/// The derived image adds socat, because XRouter's only KISS-capable interface
/// type wants a serial device and net-sim speaks KISS over TCP. See the
/// Dockerfile for the full reasoning.
/// </para>
/// </remarks>
public sealed class XrouterFixture : IAsyncLifetime
{
    private const string NetsimImage = "ghcr.io/packethacking/net-sim:main";

    private INetwork? network;
    private IContainer? netsimContainer;
    private IContainer? xrouterContainer;
    private IFutureDockerImage? xrouterImage;
    private string? tempDir;

    public static string NetsimHost => "127.0.0.1";

    /// <summary>Host port mapped to net-sim node a, where the tests connect.</summary>
    public int NetsimKissPort => netsimContainer?.GetMappedPublicPort(8100)
        ?? throw new InvalidOperationException("not started");

    public async Task InitializeAsync()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"axcall-xrouter-{Guid.NewGuid():N}");
        var buildContext = Path.Combine(tempDir, "build");
        var data = Path.Combine(tempDir, "data");
        Directory.CreateDirectory(buildContext);
        Directory.CreateDirectory(data);

        // The build context has to be real files on disk for the image build,
        // and XRouter's config has to be a directory it can write into, since
        // it seeds its own state there on first run.
        Extract("Dockerfile", Path.Combine(buildContext, "Dockerfile"));
        Extract("with-kiss-pty.sh", Path.Combine(buildContext, "with-kiss-pty.sh"));
        Extract("XROUTER.CFG", Path.Combine(data, "XROUTER.CFG"));
        Extract("IPROUTE.SYS", Path.Combine(data, "IPROUTE.SYS"));
        Extract("xrouter-network.yaml", Path.Combine(tempDir, "network.yaml"));

        network = new NetworkBuilder().WithName($"axcall-xrouter-{Guid.NewGuid():N}").Build();
        await network.CreateAsync().ConfigureAwait(false);

        netsimContainer = new ContainerBuilder(NetsimImage)
            .WithName($"axcall-xr-netsim-{Guid.NewGuid():N}")
            .WithNetwork(network)
            .WithNetworkAliases("netsim")
            .WithResourceMapping(Path.Combine(tempDir, "network.yaml"), "/etc/sim/")
            .WithCommand("-autostart")
            .WithPortBinding(8100, true)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/healthz")))
            .Build();
        await netsimContainer.StartAsync().ConfigureAwait(false);

        // XRouter does not resolve hostnames for this, so socat gets an address.
        var ip = await netsimContainer.ExecAsync(["hostname", "-i"]).ConfigureAwait(false);
        var netsimIp = ip.Stdout.Trim();

        xrouterImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(buildContext)
            .WithDockerfile("Dockerfile")
            .WithName($"axcall-xrouter-kiss:{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();
        await xrouterImage.CreateAsync().ConfigureAwait(false);

        xrouterContainer = new ContainerBuilder(xrouterImage)
            .WithName($"axcall-xrouter-{Guid.NewGuid():N}")
            .WithNetwork(network)
            .WithNetworkAliases("xrouter")
            .WithEnvironment("KISS_TCP", $"{netsimIp}:8103")
            .WithEnvironment("KISS_PTY", "/dev/kisspty")
            .WithBindMount(data, "/data")
            // The image tails XRouter's own log files to stdout precisely so
            // this works against real boot lines rather than a sleep.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("xrouter version"))
            .Build();
        await xrouterContainer.StartAsync().ConfigureAwait(false);

        // Started is not the same as on the air: the KISS dial and the node's
        // own initialisation both still have to happen.
        await WaitForXrouterOnTheAirAsync().ConfigureAwait(false);
    }

    private async Task WaitForXrouterOnTheAirAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var logs = await GetNetsimLogsAsync().ConfigureAwait(false);
            if (logs.Contains("Attached to KISS TCP client", StringComparison.Ordinal)
                && logs.Contains("on port 8103", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "XRouter never attached to net-sim's KISS port. Its own log follows:\n"
            + await GetXrouterLogsAsync().ConfigureAwait(false));
    }

    public async Task<string> GetNetsimLogsAsync()
    {
        if (netsimContainer is null) return "(not started)";
        var (stdout, stderr) = await netsimContainer.GetLogsAsync().ConfigureAwait(false);
        return $"STDOUT:\n{stdout}\nSTDERR:\n{stderr}";
    }

    public async Task<string> GetXrouterLogsAsync()
    {
        if (xrouterContainer is null) return "(not started)";
        var (stdout, stderr) = await xrouterContainer.GetLogsAsync().ConfigureAwait(false);
        return $"STDOUT:\n{stdout}\nSTDERR:\n{stderr}";
    }

    public async Task DisposeAsync()
    {
        if (xrouterContainer is not null) await xrouterContainer.DisposeAsync().ConfigureAwait(false);
        if (netsimContainer is not null) await netsimContainer.DisposeAsync().ConfigureAwait(false);
        if (xrouterImage is not null) await xrouterImage.DisposeAsync().ConfigureAwait(false);
        if (network is not null) await network.DeleteAsync().ConfigureAwait(false);
        if (tempDir is not null && Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Extract(string name, string outputPath)
    {
        var resource = $"Axcall.Tests.Integration.Peers.Resources.{name}";
        using var stream = typeof(XrouterFixture).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded resource not found: {resource}");
        using var file = File.Create(outputPath);
        stream.CopyTo(file);
    }
}
