using Xunit;

namespace Axcall.Tests.Integration;

/// <remarks>
/// Not parallelised against the rest of the suite. These tests run real
/// sessions across the net-sim AFSK1200 simulator and assert on what the link
/// settled on, which means they assert on timing: the pre-SABM XID exchange
/// has to complete before the dial gives up on it. Sharing a loaded machine
/// with the rest of the suite is enough to lose that race and report SREJ off
/// on one end and on at the other.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InteropCollection : ICollectionFixture<InteropFixture>
{
    public const string Name = "Interop";
}
