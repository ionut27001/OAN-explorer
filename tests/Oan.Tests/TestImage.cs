using Xunit;

namespace Oan.Tests;

/// <summary>
/// Everything that attaches a real image belongs to this collection, so no two of those tests
/// run at once. They share one machine and one set of drive letters, and a test asserting the
/// letters are back where they started cannot tell its own mount from another test's.
/// </summary>
[CollectionDefinition(Mounting.Name)]
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
