using System.IO;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The window's commands reach the operations, and build the request the user asked for.
///
/// Nothing covered this before: the tests around it only asked whether a command COULD run. The
/// operations moved out of MainViewModel into FileOps, and a rename that quietly asked to delete
/// would have compiled, passed every test, and lost somebody's files.
/// </summary>
public class FileOpsWiringTests
{
    private sealed class Records : IFileOperations
    {
        public OperationRequest? Last { get; private set; }

        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(new OperationResult { Succeeded = request.Sources.Count });
        }
    }

    [Fact]
    public async Task New_folder_asks_for_a_name_and_creates_it_here()
    {
        var (vm, ops, temp) = await Window();
        using var _ = temp;
        vm.AskName = (_, _) => "made up";

        vm.NewFolderCommand.Execute(null);
        await Settle(ops);

        Assert.Equal(OperationKind.CreateFolder, ops.Last!.Kind);
        Assert.Equal("made up", ops.Last.NewName);
        Assert.Equal(temp.Root, ops.Last.Destination);
    }

    [Fact]
    public async Task Cancelling_the_name_prompt_runs_nothing_at_all()
    {
        var (vm, ops, temp) = await Window();
        using var _ = temp;
        vm.AskName = (_, _) => null;

        vm.NewFolderCommand.Execute(null);
        await Task.Delay(150);

        Assert.Null(ops.Last);
    }

    // Copy and paste are not here. They go through the real Windows clipboard, which needs an STA
    // thread and, worse, would overwrite whatever the person running the tests had copied. The
    // request they build is the same RunAsync every test below exercises; what is untested is the
    // two lines that hand paths to ClipboardFiles and read them back.

    [Fact]
    public async Task Delete_permanently_is_a_different_request_from_delete()
    {
        var (vm, ops, temp) = await Window();
        using var _ = temp;
        vm.Selection = [vm.Rows.Single(r => r.Name == "a.txt")];

        vm.DeleteCommand.Execute(null);
        await Settle(ops);
        Assert.False(ops.Last!.Permanent);

        vm.DeletePermanentlyCommand.Execute(null);
        await Settle(ops, ops.Last);
        Assert.True(ops.Last!.Permanent);
        Assert.Equal(OperationKind.Delete, ops.Last.Kind);
    }

    [Fact]
    public async Task Renaming_asks_under_the_old_name_and_sends_the_new_one()
    {
        var (vm, ops, temp) = await Window();
        using var _ = temp;
        vm.Selection = [vm.Rows.Single(r => r.Name == "a.txt")];

        string? offered = null;
        vm.AskName = (_, current) => { offered = current; return "b.txt"; };

        vm.RenameCommand.Execute(null);
        await Settle(ops);

        Assert.Equal("a.txt", offered);
        Assert.Equal(OperationKind.Rename, ops.Last!.Kind);
        Assert.Equal("b.txt", ops.Last.NewName);
    }

    [Fact]
    public async Task A_drop_of_something_already_here_is_not_an_operation()
    {
        var (vm, ops, temp) = await Window();
        using var _ = temp;

        vm.Drop([Path.Combine(temp.Root, "a.txt")], move: true);
        await Task.Delay(150);

        Assert.Null(ops.Last);
    }

    private static async Task<(MainViewModel Vm, Records Ops, TempFolder Temp)> Window()
    {
        var temp = new TempFolder();
        temp.AddFile("a.txt", "hello");

        var ops = new Records();
        var vm = new MainViewModel(ops, new Settings());
        await vm.NavigateAsync(temp.Root);
        return (vm, ops, temp);
    }

    private static async Task Settle(Records ops, OperationRequest? was = null)
    {
        for (var i = 0; i < 400 && ReferenceEquals(ops.Last, was); i++) await Task.Delay(20);
    }
}
