using Xunit;

namespace Oan.Tests;

/// <summary>
/// Everything that attaches a real image belongs to this collection, so no two of those tests
/// run at once. They share one machine and one set of drive letters, and a test asserting the
/// letters are back where they started cannot tell its own mount from another test's.
/// </summary>
// DisableParallelization, and it is the whole point: a drive letter belongs to the machine, not
// to a test. Without it xunit runs these classes alongside every other, and an assertion that the
// drives are back to what they were fails on a letter it never created.
//
// MEASURED before blaming timing: a detached volume's letter disappears in 26-39 ms over three
// runs, while the failing assertion had already waited three seconds. The wait was never the
// problem.
[CollectionDefinition(Mounting.Name, DisableParallelization = true)]
public sealed class Mounting
{
    public const string Name = "mounting";
}

/// <summary>
/// Mounting cannot be faked — either virtdisk.dll attached a real image or it did not — so the
/// tests that mount need a real one. It lives in the repo rather than under %TEMP%: a
/// machine-local fixture rots the moment that folder is cleaned, and the tests then fail for a
/// reason that has nothing to do with the code. Point OAN_TEST_ISO at another .iso to use it.
/// </summary>
internal static class TestImage
{
    public static string Iso { get; } =
        Environment.GetEnvironmentVariable("OAN_TEST_ISO")
        ?? System.IO.Path.Combine(Repo.Root, "tests", "Oan.Tests", "Fixtures", "test.iso");
}
