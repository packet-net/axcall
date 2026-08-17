using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Axcall.Tests.Integration;

public sealed class InteropFixture : IAsyncLifetime
{
    private const string NetsimImage = "ghcr.io/packethacking/net-sim:main";
    private const string LinbpqImage = "m0lte/linbpq:latest";

    private INetwork? network;
    private IContainer? netsimContainer;
    private IContainer? linbpqContainer;

    private string? tempDir;

    public static string NetsimHost => "127.0.0.1";

    /// <summary>Host port mapped to net-sim node a (KISS 8100) — the hub endpoint.</summary>
    public int NetsimKissPort => netsimContainer?.GetMappedPublicPort(8100) ?? throw new InvalidOperationException("not started");

    /// <summary>Host port mapped to net-sim node b (KISS 8101) — the axcall-to-axcall listen peer.</summary>
    public int NetsimKissPortB => netsimContainer?.GetMappedPublicPort(8101) ?? throw new InvalidOperationException("not started");

    public async Task InitializeAsync()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"axcall-interop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        ExtractResource("Axcall.Tests.Integration.Resources.network.yaml", Path.Combine(tempDir, "network.yaml"));

        network = new NetworkBuilder()
            .WithName($"axcall-interop-{Guid.NewGuid():N}")
            .Build();
        await network.CreateAsync().ConfigureAwait(false);

        // Start netsim first
        netsimContainer = new ContainerBuilder(NetsimImage)
            .WithName($"axcall-netsim-{Guid.NewGuid():N}")
            .WithNetwork(network)
            .WithNetworkAliases("netsim")
            .WithResourceMapping(Path.Combine(tempDir, "network.yaml"), "/etc/sim/")
            .WithCommand("-autostart")
            .WithPortBinding(8100, true)
            .WithPortBinding(8101, true)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/healthz")))
            .Build();
        await netsimContainer.StartAsync().ConfigureAwait(false);

        // Get netsim's IP on the Docker network so LinBPQ can dial it
        // (LinBPQ doesn't resolve hostnames — IPADDR must be a numeric IP)
        var ipResult = await netsimContainer.ExecAsync(["hostname", "-i"]).ConfigureAwait(false);
        var netsimIp = ipResult.Stdout.Trim();

        // Write bpq32.cfg with netsim's actual IP
        var bpq32Template = ExtractResourceString("Axcall.Tests.Integration.Resources.bpq32.cfg");
        var bpq32 = bpq32Template.Replace("IPADDR=netsim", $"IPADDR={netsimIp}");
        File.WriteAllText(Path.Combine(tempDir, "bpq32.cfg"), bpq32);

        linbpqContainer = new ContainerBuilder(LinbpqImage)
            .WithName($"axcall-linbpq-{Guid.NewGuid():N}")
            .WithNetwork(network)
            .WithNetworkAliases("linbpq")
            .WithResourceMapping(Path.Combine(tempDir, "bpq32.cfg"), "/data/")
            .WithPortBinding(8010, true)
            .WithPortBinding(8008, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8008)))
            .Build();
        await linbpqContainer.StartAsync().ConfigureAwait(false);

        // LinBPQ retries the KISS-TCP dial; give it time to connect and initialise
        await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public async Task<string> GetNetsimLogsAsync()
    {
        if (netsimContainer is null) return "(not started)";
        var (stdout, stderr) = await netsimContainer.GetLogsAsync().ConfigureAwait(false);
        return $"STDOUT:\n{stdout}\nSTDERR:\n{stderr}";
    }

    public async Task<string> GetLinbpqLogsAsync()
    {
        if (linbpqContainer is null) return "(not started)";
        var (stdout, stderr) = await linbpqContainer.GetLogsAsync().ConfigureAwait(false);
        return $"STDOUT:\n{stdout}\nSTDERR:\n{stderr}";
    }

    public async Task DisposeAsync()
    {
        if (linbpqContainer is not null) await linbpqContainer.DisposeAsync().ConfigureAwait(false);
        if (netsimContainer is not null) await netsimContainer.DisposeAsync().ConfigureAwait(false);
        if (network is not null) await network.DeleteAsync().ConfigureAwait(false);
        if (tempDir is not null && Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void ExtractResource(string resourceName, string outputPath)
    {
        using var stream = typeof(InteropFixture).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"embedded resource not found: {resourceName}");
        using var file = File.Create(outputPath);
        stream.CopyTo(file);
    }

    private static string ExtractResourceString(string resourceName)
    {
        using var stream = typeof(InteropFixture).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
