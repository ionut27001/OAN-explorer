using Xunit;

namespace Oan.Tests;

/// <summary>
/// Tests that either saturate the disk or time how long the disk takes to answer, and tests that
/// queue behind one of the app's single-threaded workers. They share one machine, so they must
/// not share a moment.
/// </summary>
// A folder size is walked on sixteen threads, and the search tests assert wall-clock deadlines —
// that a walk gets going within 250 ms, that a cancelled one stops within two seconds. Run side
// by side, the size walk is what makes the search miss its deadline, and the failure lands on the
// search, which is innocent.
//
// The icon tests are here for a second reason: IconLoader drains its queue on ONE STA thread, so
// two classes asking for icons at once do not run concurrently, they queue — and whichever waited
// with a deadline is the one that fails.
[CollectionDefinition(DiskBound.Name, DisableParallelization = true)]
public sealed class DiskBound
{
    public const string Name = "disk-bound";
}
