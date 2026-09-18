using Oan.App.Platform;
using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

public class FileKindsTests
{
    static FileEntry File(string name) =>
        new() { Name = name, FullPath = @"C:\x\" + name, Attributes = FileAttributes.Normal };

    static FileEntry Folder(string name) =>
        new() { Name = name, FullPath = @"C:\x\" + name, Attributes = FileAttributes.Directory };

    [Fact]
    public void All_hides_nothing()
    {
        Assert.True(FileKinds.Matches(File("notes.txt"), FileKind.All));
        Assert.True(FileKinds.Matches(File("setup.exe"), FileKind.All));
        Assert.True(FileKinds.Matches(Folder("Pictures"), FileKind.All));
    }

    [Fact]
    public void A_folder_is_only_ever_a_folder()
    {
        var folder = Folder("Reports");

        Assert.True(FileKinds.Matches(folder, FileKind.Folders));
        Assert.False(FileKinds.Matches(folder, FileKind.Documents));
    }

    [Fact]
    public void A_folder_whose_name_ends_in_an_extension_is_still_a_folder()
    {
        var folder = Folder("holiday.jpg");

        Assert.True(FileKinds.Matches(folder, FileKind.Folders));
        Assert.False(FileKinds.Matches(folder, FileKind.Images));
    }

    [Fact]
    public void A_file_is_never_a_folder()
    {
        Assert.False(FileKinds.Matches(File("notes.txt"), FileKind.Folders));
    }

    [Theory]
    [InlineData("report.pdf", FileKind.Documents)]
    [InlineData("holiday.JPG", FileKind.Images)]
    [InlineData("song.flac", FileKind.Audio)]
    [InlineData("clip.mkv", FileKind.Video)]
    [InlineData("backup.7z", FileKind.Archives)]
    [InlineData("Program.cs", FileKind.Code)]
    public void Each_category_recognises_its_own_whatever_the_case(string name, FileKind kind)
    {
        Assert.True(FileKinds.Matches(File(name), kind));
    }

    [Fact]
    public void An_extension_in_no_category_is_shown_by_All_and_by_nothing_else()
    {
        var file = File("driver.sys");

        Assert.Null(FileKinds.Of(".sys"));
        Assert.True(FileKinds.Matches(file, FileKind.All));

        foreach (var kind in Enum.GetValues<FileKind>())
            if (kind != FileKind.All)
                Assert.False(FileKinds.Matches(file, kind));
    }

    [Fact]
    public void A_file_with_no_extension_belongs_to_no_category()
    {
        Assert.True(FileKinds.Matches(File("LICENSE"), FileKind.All));
        Assert.False(FileKinds.Matches(File("LICENSE"), FileKind.Documents));
    }
}
