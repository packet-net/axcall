using Xunit;

namespace Axcall.Tests.Integration.Peers;

/// <summary>
/// The on-demand peer suite: implementations other than LinBPQ, run when the
/// IP code changes or a peer is updated rather than on every push.
/// </summary>
/// <remarks>
/// <para>
/// Not parallelised, for the same reason as the LinBPQ collection: these tests
/// drive a real-time DSP simulation in containers, so sharing a loaded machine
/// with the rest of the suite makes them measure the machine rather than the
/// code.
/// </para>
/// <para>
/// <c>Category=Peers</c> is what keeps them out of CI. The reason is not
/// preference: the kernel leg of this suite cannot run in a container at all
/// (AF_AX25 is refused inside a non-init user namespace, which is what every
/// runner here is), so a peer suite that CI could run in full does not exist.
/// Given that, running the rest nightly buys little, and the results belong in
/// docs/ip-over-ax25.md where a stale one is visibly stale.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class XrouterCollection : ICollectionFixture<XrouterFixture>
{
    public const string Name = "XRouter peer";
}
