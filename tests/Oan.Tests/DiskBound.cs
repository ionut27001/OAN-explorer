using Xunit;

namespace Oan.Tests;

/// <summary>
/// Tests that either saturate the disk or time how long the disk takes to answer. They share one
/// machine, so they must not share a moment.
/// </summary>
// A folder size is now walked on sixteen threads, and the search tests assert wall-clock
// deadlines — that a walk gets going within 250 ms, that a cancelled one stops within two
// seconds. Run side by side, the size walk is what makes the search miss its deadline, and the
// failure lands on the search, which is innocent.
[CollectionDefinition(DiskBound.Name, DisableParallelization = true)]
public sealed class DiskBound
{
    public const string Name = "disk-bound";
}
