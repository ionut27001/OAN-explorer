namespace Oan.Tests;

/// <summary>A real folder under %TEMP%, removed when the test finishes.</summary>
sealed class TempFolder : IDisposable
{
    public string Root { get; }

    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "oan-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string AddFile(string name, string content = "")
    {
        var full = Path.Combine(Root, name);
        File.WriteAllText(full, content);
        return full;
    }

    public string AddHiddenFile(string name, string content = "")
    {
        var full = AddFile(name, content);
        File.SetAttributes(full, FileAttributes.Hidden);
        return full;
    }

    public string AddFolder(string name)
    {
        var full = Path.Combine(Root, name);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>A handle a test left open loses the race once; if it survives a retry, the failure is real and escapes.</summary>
    public void Dispose()
    {
        try
        {
            Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Thread.Sleep(50);
            Delete();
        }
    }

    void Delete()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
