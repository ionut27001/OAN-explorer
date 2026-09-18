using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

public class ShellFileOperationsTests
{
    private static readonly ShellFileOperations Operations = new();

    private static Task<OperationResult> Run(OperationRequest request) => Operations.RunAsync(request, 0);

    [Fact]
    public async Task Copy_without_destination_is_refused()
    {
        var result = await Run(new OperationRequest { Kind = OperationKind.Copy, Sources = [@"C:\a.txt"] });

        Assert.False(result.Ok);
        Assert.Contains("destination", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Failed);
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.Succeeded);
    }

    [Fact]
    public async Task Move_without_destination_is_refused()
    {
        var result = await Run(new OperationRequest { Kind = OperationKind.Move, Sources = [@"C:\a.txt"] });

        Assert.False(result.Ok);
        Assert.Contains("destination", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFolder_without_parent_is_refused()
    {
        var result = await Run(new OperationRequest { Kind = OperationKind.CreateFolder, NewName = "New folder" });

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Rename_without_new_name_is_refused()
    {
        var result = await Run(new OperationRequest { Kind = OperationKind.Rename, Sources = [@"C:\a.txt"] });

        Assert.False(result.Ok);
        Assert.Contains("name", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFolder_without_new_name_is_refused()
    {
        var result = await Run(new OperationRequest { Kind = OperationKind.CreateFolder, Destination = @"C:\" });

        Assert.False(result.Ok);
        Assert.Contains("name", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rename_of_several_items_is_refused()
    {
        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.Rename,
            Sources = [@"C:\a.txt", @"C:\b.txt"],
            NewName = "c.txt",
        });

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CreateFolder_with_sources_is_refused()
    {
        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.CreateFolder,
            Sources = [@"C:\a.txt"],
            Destination = @"C:\",
            NewName = "New folder",
        });

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(OperationKind.Delete)]
    [InlineData(OperationKind.Copy)]
    [InlineData(OperationKind.Move)]
    [InlineData(OperationKind.Rename)]
    public async Task Empty_selection_is_refused(OperationKind kind)
    {
        var result = await Run(new OperationRequest
        {
            Kind = kind,
            Destination = @"C:\target",
            NewName = "c.txt",
        });

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Relative_source_is_refused()
    {
        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.Copy,
            Sources = [@"docs\a.txt"],
            Destination = @"C:\target",
        });

        Assert.False(result.Ok);
        Assert.Contains("full path", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Relative_destination_is_refused()
    {
        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.Copy,
            Sources = [@"C:\a.txt"],
            Destination = "target",
        });

        Assert.False(result.Ok);
        Assert.Contains("full path", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Aborted_beats_a_success_hresult()
    {
        var result = ShellFileOperations.Compose(HResultOk, aborted: true, succeeded: 2, failed: []);

        Assert.True(result.Cancelled);
        Assert.False(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Succeeded);
    }

    [Fact]
    public void Item_failures_survive_a_success_hresult()
    {
        var denied = new FailedItem(@"C:\locked.txt", AccessDenied, "Access is denied.");

        var result = ShellFileOperations.Compose(HResultOk, aborted: false, succeeded: 37, failed: [denied]);

        Assert.False(result.Ok);
        Assert.False(result.Cancelled);
        Assert.Null(result.Error);
        Assert.Equal(37, result.Succeeded);
        Assert.Equal(denied, Assert.Single(result.Failed));
    }

    [Fact]
    public void Everything_succeeded_is_ok()
    {
        var result = ShellFileOperations.Compose(HResultOk, aborted: false, succeeded: 3, failed: []);

        Assert.True(result.Ok);
        Assert.Equal(3, result.Succeeded);
    }

    [Theory]
    [InlineData(unchecked((int)0x800704C7))]
    [InlineData(unchecked((int)0x80004004))]
    public void Cancel_hresults_are_not_errors(int hr)
    {
        var result = ShellFileOperations.Compose(hr, aborted: false, succeeded: 0, failed: []);

        Assert.True(result.Cancelled);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_hresult_becomes_an_error()
    {
        var result = ShellFileOperations.Compose(AccessDenied, aborted: false, succeeded: 0, failed: []);

        Assert.False(result.Ok);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Missing_source_fails_before_anything_runs()
    {
        using var temp = new TempDir();

        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.Copy,
            Sources = [Path.Combine(temp.Root, "not-here.txt")],
            Destination = temp.Root,
        });

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Failed);
        Assert.Equal(0, result.Succeeded);
    }

    [Fact]
    public async Task CreateFolder_creates_the_folder()
    {
        using var temp = new TempDir();

        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.CreateFolder,
            Destination = temp.Root,
            NewName = "Reports",
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.Succeeded);
        Assert.True(Directory.Exists(Path.Combine(temp.Root, "Reports")));
    }

    [Fact]
    public async Task Rename_renames_the_file()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Root, "before.txt");
        await File.WriteAllTextAsync(source, "hello");

        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.Rename,
            Sources = [source],
            NewName = "after.txt",
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.Succeeded);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(temp.Root, "after.txt")));
    }

    [Fact]
    public async Task Move_moves_the_file()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Root, "notes.txt");
        var target = Directory.CreateDirectory(Path.Combine(temp.Root, "out")).FullName;
        await File.WriteAllTextAsync(source, "hello");

        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.Move,
            Sources = [source],
            Destination = target,
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.Succeeded);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(target, "notes.txt")));
    }

    [Fact]
    public async Task Copy_counts_every_item_in_the_batch()
    {
        using var temp = new TempDir();
        var target = Directory.CreateDirectory(Path.Combine(temp.Root, "out")).FullName;
        var sources = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var source = Path.Combine(temp.Root, $"file{i}.txt");
            await File.WriteAllTextAsync(source, $"body {i}");
            sources.Add(source);
        }

        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.Copy,
            Sources = sources,
            Destination = target,
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, result.Succeeded);
        Assert.Equal(3, Directory.GetFiles(target).Length);
    }

    [Fact]
    public async Task Copy_copies_the_file_and_counts_it()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Root, "notes.txt");
        var target = Directory.CreateDirectory(Path.Combine(temp.Root, "out")).FullName;
        await File.WriteAllTextAsync(source, "hello");

        var result = await Run(new OperationRequest
        {
            Kind = OperationKind.Copy,
            Sources = [source],
            Destination = target,
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(target, "notes.txt")));
    }

    private const int HResultOk = 0;
    private const int AccessDenied = unchecked((int)0x80070005);

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("oan-tests-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
