using Xunit;

namespace Axcall.Tests;

/// <summary>
/// Tests that pin <c>AXCALL_PORTS</c>, <c>AXCALL_HOSTS</c>, <c>AXCALL_INETD</c>
/// or <c>AXCALL_AXTUN</c> at a temporary file.
/// </summary>
/// <remarks>
/// Those variables are process-wide, so two test classes setting them at once
/// read each other's files and fail in ways that look like parser bugs. xunit
/// serialises the tests within a class but runs classes in parallel, so one
/// class per variable is not enough on its own; they have to share a
/// collection, and the collection has to be kept out of the parallel pool
/// because the state it owns belongs to the whole process rather than to the
/// collection.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConfigFilesCollection
{
    public const string Name = "config files";
}
