using System.IO;
using Oan.App.Platform;
using Oan.Core.Operations;
using Oan.App.ViewModels;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The whole path the switch takes, through the real window model: navigate, switch on, and the
/// rows fill. The unit tests cover the pieces; this covers them being wired to each other.
/// </summary>
public class FolderSizesEndToEndTests
{
    private sealed class NoOps : IFileOperations
    {
        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default) =>
            Task.FromResult(OperationResult.Refused("not used here"));
    }

    [Fact]
    public async Task Turning_the_switch_on_fills_the_folder_rows_of_the_listing()
    {
        using var temp = new TempFolder();
        temp.AddFolder("one");
        temp.AddFolder("two");
        File.WriteAllText(Path.Combine(temp.Root, "one", "a.bin"), new string('x', 700));
        File.WriteAllText(Path.Combine(temp.Root, "two", "b.bin"), new string('x', 40));

        var vm = new MainViewModel(new NoOps(), new Settings());
        await vm.NavigateAsync(temp.Root);

        vm.FolderSizes.On = true;

        Assert.True(Settles(() => Row(vm, "one").SortSize == 700 && Row(vm, "two").SortSize == 40),
            $"one showed '{Row(vm, "one").SizeText}', two showed '{Row(vm, "two").SizeText}'");
    }

    private static EntryViewModel Row(MainViewModel vm, string name) => vm.Rows.Single(r => r.Name == name);

    private static bool Settles(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) Thread.Sleep(20);
        return done();
    }
}
