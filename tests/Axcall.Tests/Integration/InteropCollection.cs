using Xunit;

namespace Axcall.Tests.Integration;

[CollectionDefinition(Name)]
public sealed class InteropCollection : ICollectionFixture<InteropFixture>
{
    public const string Name = "Interop";
}
