using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

public class DirectoryListingTests
{
    [Fact]
    public void A_clean_listing_is_complete() =>
        Assert.True(new DirectoryListing { Path = @"C:\", Entries = [] }.IsComplete);

    [Fact]
    public void Skipped_entries_make_the_listing_incomplete() =>
        Assert.False(new DirectoryListing { Path = @"C:\", Entries = [], Skipped = 1 }.IsComplete);

    [Fact]
    public void An_error_makes_the_listing_incomplete() =>
        Assert.False(new DirectoryListing { Path = @"C:\", Entries = [], Error = "denied" }.IsComplete);

    [Fact]
    public void Failed_carries_the_path_and_the_error()
    {
        var listing = DirectoryListing.Failed(@"C:\gone", "This folder no longer exists.");

        Assert.Equal(@"C:\gone", listing.Path);
        Assert.Equal("This folder no longer exists.", listing.Error);
        Assert.Empty(listing.Entries);
        Assert.False(listing.IsComplete);
    }
}
