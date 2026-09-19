using Xunit;

namespace Oan.Tests;


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
