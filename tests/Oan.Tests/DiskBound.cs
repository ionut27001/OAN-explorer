using Xunit;

namespace Oan.Tests;

/// <summary>
/// Tests that own the machine while they run: they saturate the disk, time how long the disk
/// takes to answer, attach a real image and expect the drive letters back, or queue behind one of
/// the app's single-threaded workers. One collection, not several, because two collections that
/// each claim the machine still run alongside each other.
/// </summary>
// A folder size is walked on sixteen threads, and the search tests assert wall-clock deadlines —
// that a walk gets going within 250 ms, that a cancelled one stops within two seconds. Run side
// by side, the size walk is what makes the search miss its deadline, and the failure lands on the
// search, which is innocent.
//
// The icon tests are here for a second reason: IconLoader drains its queue on ONE STA thread, so
// two classes asking for icons at once do not run concurrently, they queue — and whichever waited
// with a deadline is the one that fails.
//
// The mount tests are here for a third: a drive letter belongs to the machine, not to a test, and
// an assertion that the letters are back to what they were cannot tell its own mount from another
// test's. MEASURED before blaming timing: a detached volume's letter disappears in 26-39 ms over
// three runs, while the failing assertion had already waited three seconds.
[CollectionDefinition(DiskBound.Name, DisableParallelization = true)]
public sealed class DiskBound
{
    public const string Name = "disk-bound";
}
