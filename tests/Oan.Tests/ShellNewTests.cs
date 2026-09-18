using Microsoft.Win32;
using Oan.App.Platform;
using Xunit;

namespace Oan.Tests;

public class ShellNewTests
{
    [Fact]
    public void Every_offered_type_is_one_we_can_actually_create()
    {
        foreach (var template in ShellNew.Templates())
        {
            Assert.StartsWith(".", template.Extension);
            Assert.NotEqual("", template.Label);

            // A label straight out of the registry can be a resource reference or carry a menu
            // accelerator; neither is a thing to show a user.
            Assert.DoesNotContain("@", template.Label);
            Assert.DoesNotContain("&", template.Label);

            switch (template.Source)
            {
                case NewFileSource.Template:
                    Assert.True(File.Exists(template.TemplatePath),
                        $"{template.Label} copies {template.TemplatePath}, which is not there.");
                    break;
                case NewFileSource.Data:
                    Assert.NotEmpty(template.Data!);
                    break;
            }
        }
    }

    [Fact]
    public void An_entry_the_shell_creates_through_a_COM_handler_is_not_offered()
    {
        var handled = ExtensionsAdvertising("Handler");
        Assert.True(handled.Count > 0,
            "No ShellNew entry on this machine uses a Handler, so this test proves nothing.");

        var offered = ShellNew.Templates().Select(t => t.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in handled)
            Assert.DoesNotContain(extension, offered);
    }

    [Fact]
    public void An_entry_that_runs_a_program_is_not_offered()
    {
        var commanded = ExtensionsAdvertising("Command");
        Assert.True(commanded.Count > 0,
            "No ShellNew entry on this machine uses a Command, so this test proves nothing.");

        var offered = ShellNew.Templates().Select(t => t.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in commanded)
            Assert.DoesNotContain(extension, offered);
    }

    [Fact]
    public void An_empty_type_creates_an_empty_file_under_the_name_the_type_advertises()
    {
        using var temp = new TempFolder();
        var template = new NewFileTemplate
        {
            Label = "Text Document", Extension = ".txt", Source = NewFileSource.Empty,
        };

        var path = ShellNew.Create(temp.Root, template);

        Assert.Equal(Path.Combine(temp.Root, "New Text Document.txt"), path);
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void A_data_type_is_written_byte_for_byte()
    {
        using var temp = new TempFolder();
        byte[] bytes = [0x50, 0x4B, 0x05, 0x06, 0x00];
        var template = new NewFileTemplate
        {
            Label = "Compressed Folder", Extension = ".zip", Source = NewFileSource.Data, Data = bytes,
        };

        var path = ShellNew.Create(temp.Root, template);

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_template_type_is_a_copy_of_the_template_file()
    {
        using var temp = new TempFolder();
        var source = temp.AddFile("template.rtf", "{\\rtf1}");
        var template = new NewFileTemplate
        {
            Label = "Rich Text Document", Extension = ".rtf", Source = NewFileSource.Template, TemplatePath = source,
        };

        var path = ShellNew.Create(temp.Root, template);

        Assert.Equal("{\\rtf1}", File.ReadAllText(path));
        Assert.True(File.Exists(source), "The template itself was moved instead of copied.");
    }

    [Fact]
    public void A_second_one_is_created_beside_the_first_and_never_over_it()
    {
        using var temp = new TempFolder();
        var template = new NewFileTemplate
        {
            Label = "Text Document", Extension = ".txt", Source = NewFileSource.Empty,
        };

        var first = ShellNew.Create(temp.Root, template);
        File.WriteAllText(first, "typed into the first one");
        var second = ShellNew.Create(temp.Root, template);

        Assert.Equal(Path.Combine(temp.Root, "New Text Document (2).txt"), second);
        Assert.Equal("typed into the first one", File.ReadAllText(first));
    }

    [Fact]
    public void A_name_already_taken_by_a_folder_is_not_reused_either()
    {
        using var temp = new TempFolder();
        temp.AddFolder("New Text Document.txt");
        var template = new NewFileTemplate
        {
            Label = "Text Document", Extension = ".txt", Source = NewFileSource.Empty,
        };

        var path = ShellNew.Create(temp.Root, template);

        Assert.Equal(Path.Combine(temp.Root, "New Text Document (2).txt"), path);
    }

    /// <summary>
    /// The extensions whose ShellNew key carries the named value, in either documented place.
    /// Read straight from the registry so the test states the machine's facts, not the code's.
    /// </summary>
    static List<string> ExtensionsAdvertising(string value)
    {
        var found = new List<string>();
        var classes = Registry.ClassesRoot;

        foreach (var name in classes.GetSubKeyNames())
        {
            if (name.Length < 2 || name[0] != '.') continue;

            using var extension = classes.OpenSubKey(name);
            if (extension is null) continue;

            var places = new List<string> { "ShellNew" };
            places.AddRange(extension.GetSubKeyNames().Select(sub => $@"{sub}\ShellNew"));

            foreach (var place in places)
            {
                using var key = extension.OpenSubKey(place);
                if (key?.GetValue(value) is not null) found.Add(name);
            }
        }

        return found;
    }
}
