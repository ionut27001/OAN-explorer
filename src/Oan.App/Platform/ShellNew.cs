using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Oan.App.Platform;

public enum NewFileSource { Empty, Template, Data }

/// <summary>One entry of the shell's New menu, as the registry advertises it.</summary>
public sealed record NewFileTemplate
{
    public required string Label { get; init; }
    public required string Extension { get; init; }
    public required NewFileSource Source { get; init; }

    /// <summary>The file to copy. Set for <see cref="NewFileSource.Template"/>, and it exists.</summary>
    public string? TemplatePath { get; init; }

    /// <summary>The bytes to write. Set for <see cref="NewFileSource.Data"/>.</summary>
    public byte[]? Data { get; init; }

    public string DefaultName => $"New {Label}{Extension}";
}

/// <summary>
/// The file types the shell itself offers under New, read from HKEY_CLASSES_ROOT. Read only:
/// nothing here opens a key for writing, and what a type advertises is the shell's business.
///
/// Four forms are documented. NullFile, FileName and Data we can carry out ourselves. Command
/// runs a program and Handler hands the job to a COM object — neither is offered, because an
/// entry that is shown and then creates a broken empty file is worse than one that is absent.
/// </summary>
public static class ShellNew
{
    private static readonly NewFileTemplate[] BuiltIn =
    [
        new() { Label = "Text Document", Extension = ".txt", Source = NewFileSource.Empty },
        new() { Label = "Markdown File", Extension = ".md", Source = NewFileSource.Empty },
    ];

    public static IReadOnlyList<NewFileTemplate> Templates()
    {
        var found = new Dictionary<string, NewFileTemplate>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var classes = Registry.ClassesRoot;
            foreach (var name in classes.GetSubKeyNames())
            {
                if (name.Length < 2 || name[0] != '.') continue;

                using var extension = classes.OpenSubKey(name);
                if (extension is null) continue;

                // Both documented places: HKCR\.ext\ShellNew and HKCR\.ext\<ProgID>\ShellNew.
                if (Read(classes, extension, name, null) is { } direct) found.TryAdd(name, direct);

                foreach (var progId in extension.GetSubKeyNames())
                    if (Read(classes, extension, name, progId) is { } nested) found.TryAdd(name, nested);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A registry we cannot read means an empty New menu, not a window that fails to open.
        }

        // What the registry has is whatever Windows and its installed apps put there — and on a
        // machine where the context menu has been cleaned up it can be almost nothing. Measured
        // here: only .lnk had a ShellNew key, not even .txt. A New menu offering one entry is not
        // worth opening, so these are always offered; the registry's own entry wins if it exists.
        foreach (var built in BuiltIn) found.TryAdd(built.Extension, built);

        return [.. found.Values.OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Creates the file and returns its path. Throws if the folder refuses it.</summary>
    public static string Create(string folder, NewFileTemplate template)
    {
        var path = Unique(folder, template.DefaultName);

        switch (template.Source)
        {
            case NewFileSource.Template:
                File.Copy(template.TemplatePath!, path);
                break;

            case NewFileSource.Data:
                // CreateNew rather than WriteAllBytes: between choosing a free name and writing
                // it, something else may have taken it, and the answer to that is an error.
                using (var file = new FileStream(path, FileMode.CreateNew))
                    file.Write(template.Data!);
                break;

            default:
                using (new FileStream(path, FileMode.CreateNew)) { }
                break;
        }

        return path;
    }

    private static NewFileTemplate? Read(RegistryKey classes, RegistryKey extensionKey, string extension, string? progId)
    {
        using var key = extensionKey.OpenSubKey(progId is null ? "ShellNew" : $@"{progId}\ShellNew");
        if (key is null) return null;
        if (key.GetValue("Handler") is not null || key.GetValue("Command") is not null) return null;

        var source = SourceOf(key, out var templatePath, out var data);
        if (source is null) return null;

        return new NewFileTemplate
        {
            Label = Label(classes, key, extensionKey, extension, progId),
            Extension = extension,
            Source = source.Value,
            TemplatePath = templatePath,
            Data = data,
        };
    }

    private static NewFileSource? SourceOf(RegistryKey key, out string? templatePath, out byte[]? data)
    {
        templatePath = null;
        data = null;

        if (key.GetValue("FileName") is string file && file.Length > 0)
        {
            // A template file that is gone would fail at the moment of the click, so the entry
            // is dropped here instead.
            templatePath = TemplateFile(file);
            return templatePath is null ? null : NewFileSource.Template;
        }

        if (key.GetValue("Data") is byte[] { Length: > 0 } bytes)
        {
            data = bytes;
            return NewFileSource.Data;
        }

        return key.GetValue("NullFile") is not null ? NewFileSource.Empty : null;
    }

    /// <summary>A bare name means the shell's own template folder, or the user's.</summary>
    private static string? TemplateFile(string file)
    {
        if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;

        string[] folders =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew"),
            Environment.GetFolderPath(Environment.SpecialFolder.Templates),
        ];

        return folders.Select(f => Path.Combine(f, file)).FirstOrDefault(File.Exists);
    }

    private static string Label(RegistryKey classes, RegistryKey key, RegistryKey extensionKey, string extension, string? progId)
    {
        if (Plain(key.GetValue("MenuText")) is { } menu) return menu;
        if (Plain(key.GetValue("ItemName")) is { } item) return item;

        var id = progId ?? extensionKey.GetValue("") as string;
        if (id is { Length: > 0 })
        {
            using var type = classes.OpenSubKey(id);
            if (Plain(type?.GetValue("")) is { } friendly) return friendly;
        }

        return $"{extension.TrimStart('.').ToUpperInvariant()} file";
    }

    /// <summary>
    /// Labels are often a resource reference — "@shell32.dll,-30397" — which needs the string
    /// table to read. Rather than show the user a DLL and an index, those fall through to the
    /// friendly name of the type.
    /// </summary>
    private static string? Plain(object? value) =>
        value is string text && text.Length > 0 && text[0] != '@' ? text.Replace("&", "") : null;

    private static string Unique(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        var path = Path.Combine(folder, name);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");

        return path;
    }
}
