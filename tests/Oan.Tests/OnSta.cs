using Oan.Core.Shell;

namespace Oan.Tests;

/// <summary>
/// An STA apartment for a test. xunit runs on the thread pool, which is MTA, and a thumbnail
/// provider registered apartment-threaded would be marshalled to a host apartment there — the
/// test would still pass, and it would be timing the wrong thing.
///
/// One thread for the whole run, not one per call: creating a thread costs about as much as a
/// cached thumbnail lookup, and a benchmark that pays it inside the clock measures itself.
/// </summary>
/// <remarks>Uses the app's own <see cref="StaThread"/> rather than a hand-rolled one, so a test
/// that hangs hangs the way the app would. Everything that uses it is in the
/// <see cref="DiskBound"/> collection, which does not run in parallel.</remarks>
static class OnSta
{
    /// <summary>Generous on purpose: a deadline is the subject of some of these tests, not a
    /// property of the harness running them.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static StaThread _thread = new("test STA");

    public static T Run<T>(Func<T> job)
    {
        if (_thread.Lost) _thread = new StaThread("test STA");

        var result = default(T);
        if (!_thread.Run(() => result = job(), Patience))
            throw new TimeoutException($"Nothing came back within {Patience.TotalSeconds:0} seconds.");

        return result!;
    }

    public static void Run(Action job) => Run<object?>(() => { job(); return null; });
}
