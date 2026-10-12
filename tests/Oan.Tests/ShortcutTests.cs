using Oan.Core.Links;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// What a shortcut says, and what a file manager is entitled to conclude from it. The shortcuts
/// are written by the test, so nothing here depends on what this machine happens to have
/// installed — except the two that say so in their names.
///
/// Disk-bound: every one of these writes a .lnk through the shell and reads it back through the
/// engine's single STA apartment, so two classes doing it at once queue rather than run.
/// </summary>
[Collection(DiskBound.Name)]
public class ShortcutTests
{
    [Fact]
    public void A_shortcut_to_a_folder_is_a_folder_OAN_can_enter()
    {
        using var temp = new TempFolder();
        var folder = temp.AddFolder("target");
        var link = ShellLinkWriter.At(Path.Combine(temp.Root, "to-folder.lnk"), folder);

        var resolved = Shortcuts.Resolve(link);

        Assert.Equal(LinkLeadsTo.Folder, resolved.LeadsTo);
        Assert.Equal(folder, resolved.FolderToEnter);
        Assert.True(resolved.TargetExists);
        Assert.Null(resolved.Refusal);
    }

    [Fact]
    public void A_shortcut_to_a_file_is_not_somewhere_to_navigate()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("readme.txt", "hello");
        var link = ShellLinkWriter.At(Path.Combine(temp.Root, "to-file.lnk"), file);

        var resolved = Shortcuts.Resolve(link);

        Assert.Equal(LinkLeadsTo.File, resolved.LeadsTo);
        Assert.Null(resolved.FolderToEnter);
        Assert.Equal(file, resolved.Target);
        Assert.True(resolved.TargetExists);
    }

    [Fact]
    public void Arguments_and_the_working_directory_come_back()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("run.txt");
        var link = ShellLinkWriter.At(Path.Combine(temp.Root, "with-args.lnk"), file,
            arguments: "--one \"two three\"", workingDirectory: temp.Root);

        var resolved = Shortcuts.Resolve(link);

        Assert.Equal("--one \"two three\"", resolved.Arguments);
        Assert.Equal(temp.Root, resolved.WorkingDirectory);
    }

    [Fact]
    public void An_icon_that_was_never_set_is_null_and_not_index_zero()
    {
        using var temp = new TempFolder();
        var link = ShellLinkWriter.At(Path.Combine(temp.Root, "plain.lnk"), temp.AddFile("plain.txt"));

        var resolved = Shortcuts.Resolve(link);

        Assert.Null(resolved.IconFile);
        Assert.Null(resolved.IconIndex);
    }

    [Fact]
    public void A_named_icon_comes_back_with_its_index()
    {
        using var temp = new TempFolder();
        var link = ShellLinkWriter.At(Path.Combine(temp.Root, "iconned.lnk"), temp.AddFile("a.txt"),
            iconFile: @"C:\Windows\System32\shell32.dll", iconIndex: 4);

        var resolved = Shortcuts.Resolve(link);

        Assert.Equal(@"C:\Windows\System32\shell32.dll", resolved.IconFile);
        Assert.Equal(4, resolved.IconIndex);
    }

    [Fact]
    public void A_shortcut_whose_target_is_gone_still_names_it_and_still_knows_it_was_a_folder()
    {
        using var temp = new TempFolder();
        var folder = temp.AddFolder("doomed");
        var link = ShellLinkWriter.At(Path.Combine(temp.Root, "broken.lnk"), folder);
        Directory.Delete(folder);

        var resolved = Shortcuts.Resolve(link);

        Assert.Equal(folder, resolved.Target);
        Assert.False(resolved.TargetExists);
        Assert.True(resolved.IsBroken);

        // The .lnk recorded what the target was, so a dead shortcut can still be shown as a
        // folder rather than as an unknown.
        Assert.True(resolved.TargetWasDirectory);
        Assert.Equal(LinkLeadsTo.Folder, resolved.LeadsTo);
    }

    [Fact]
    public void A_url_file_names_an_address_and_does_not_go_and_look()
    {
        using var temp = new TempFolder();
        var link = ShellLinkWriter.Url(Path.Combine(temp.Root, "site.url"), "https://example.com/");

        var resolved = Shortcuts.Resolve(link);

        Assert.Equal(LinkKind.InternetShortcut, resolved.Kind);
        Assert.Equal(LinkLeadsTo.Url, resolved.LeadsTo);
        Assert.Equal("https://example.com/", resolved.Target);

        // Finding out would mean a network request, and a folder listing does not make those.
        Assert.Null(resolved.TargetExists);
        Assert.Null(resolved.FolderToEnter);
    }

    [Fact]
    public void A_url_holding_a_query_string_keeps_all_of_it()
    {
        using var temp = new TempFolder();
        var address = "https://example.com/search?a=b&c=d#frag";
        var link = ShellLinkWriter.Url(Path.Combine(temp.Root, "query.url"), address);

        Assert.Equal(address, Shortcuts.Resolve(link).Target);
    }

    [Fact]
    public void A_url_that_names_a_local_folder_is_a_folder_OAN_can_enter()
    {
        using var temp = new TempFolder();
        var folder = temp.AddFolder("local");
        var link = ShellLinkWriter.Url(Path.Combine(temp.Root, "local.url"), new Uri(folder).AbsoluteUri);

        var resolved = Shortcuts.Resolve(link);

        Assert.Equal(LinkLeadsTo.Folder, resolved.LeadsTo);
        Assert.Equal(folder, resolved.FolderToEnter);
        Assert.True(resolved.TargetExists);
    }

    [Fact]
    public void A_url_file_with_no_address_says_so_rather_than_coming_back_empty()
    {
        using var temp = new TempFolder();
        var link = ShellLinkWriter.Url(Path.Combine(temp.Root, "empty.url"), url: null);

        var resolved = Shortcuts.Resolve(link);

        Assert.Null(resolved.Target);
        Assert.Equal(LinkLeadsTo.Unknown, resolved.LeadsTo);
        Assert.NotNull(resolved.Refusal);
    }

    [Fact]
    public void Keys_outside_the_InternetShortcut_section_are_not_read()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, "sections.url");
        File.WriteAllLines(path, ["[DEFAULT]", "URL=https://wrong.example/", "[InternetShortcut]", "URL=https://right.example/"]);

        Assert.Equal("https://right.example/", Shortcuts.Resolve(path).Target);
    }

    [Fact]
    public void Something_that_is_not_a_shortcut_is_refused_by_name()
    {
        using var temp = new TempFolder();
        var resolved = Shortcuts.Resolve(temp.AddFile("notalink.txt"));

        Assert.NotNull(resolved.Refusal);
        Assert.Null(resolved.Target);
        Assert.Equal(LinkLeadsTo.Unknown, resolved.LeadsTo);
    }

    [Fact]
    public void A_lnk_that_is_not_a_lnk_is_a_refusal_and_not_a_crash()
    {
        using var temp = new TempFolder();
        var fake = Path.Combine(temp.Root, "garbage.lnk");
        File.WriteAllText(fake, "this is not a shell link");

        var resolved = Shortcuts.Resolve(fake);

        Assert.NotNull(resolved.Refusal);
        Assert.Null(resolved.Target);
    }

    [Fact]
    public void A_shortcut_that_is_not_there_is_a_refusal()
    {
        using var temp = new TempFolder();
        var resolved = Shortcuts.Resolve(Path.Combine(temp.Root, "never-existed.lnk"));

        Assert.NotNull(resolved.Refusal);
        Assert.Null(resolved.Target);
    }

    [Theory]
    [InlineData("a.lnk", true)]
    [InlineData("A.LNK", true)]
    [InlineData("a.url", true)]
    [InlineData("a.URL", true)]
    [InlineData("a.txt", false)]
    [InlineData("lnk", false)]
    public void What_counts_as_a_shortcut(string name, bool expected) =>
        Assert.Equal(expected, Shortcuts.IsShortcut(name));

    /// <summary>
    /// The apartment is shared and reused. Two hundred reads must not become two hundred threads,
    /// and — the part that would actually break — a second read must not inherit the first's
    /// answer.
    /// </summary>
    [Fact]
    public void Many_shortcuts_in_a_row_each_get_their_own_answer()
    {
        using var temp = new TempFolder();
        var links = Enumerable.Range(0, 40)
            .Select(i => ShellLinkWriter.At(Path.Combine(temp.Root, $"link{i}.lnk"), temp.AddFile($"file{i}.txt")))
            .ToArray();

        var resolved = links.Select(Shortcuts.Resolve).ToArray();

        Assert.All(resolved, r => Assert.True(r.TargetExists));
        Assert.Equal(40, resolved.Select(r => r.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
