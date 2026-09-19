using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Oan.App.Controls;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// Clicking a column header to sort. It did nothing for as long as band-select has existed: the
/// headers live inside the list, the rubber band starts on a tunnelling press, and it captured
/// the mouse and marked the event handled before the header ever saw it.
/// </summary>
public class SortByColumnTests
{
    private sealed class NoOps : IFileOperations
    {
        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default) =>
            Task.FromResult(OperationResult.Refused("not used here"));
    }

    [Fact]
    public void A_press_on_a_column_header_is_recognised_as_one_and_not_as_empty_space()
    {
        var (inHeader, inRow) = Sta(() =>
        {
            var label = new TextBlock { Text = "Size" };
            var header = new GridViewColumnHeader { Content = label };

            var rowLabel = new TextBlock { Text = "notes.txt" };
            var row = new ListViewItem { Content = rowLabel };

            return (Ancestor.Of<GridViewColumnHeader>(label) is not null,
                    Ancestor.Of<GridViewColumnHeader>(rowLabel) is not null);
        });

        Assert.True(inHeader, "a click on the header's own label has to read as the header");
        Assert.False(inRow, "a click on a row must not read as the header");
    }

    [Fact]
    public async Task Every_column_header_names_a_sort_key()
    {
        var headers = new[] { "Name", "Size", "Kind", "Modified" };

        foreach (var header in headers)
            Assert.True(Enum.TryParse<SortKey>(header, out _), $"header '{header}' parses to no sort key");

        // And the markup still uses exactly those, since the handler matches on the header text.
        var xaml = await File.ReadAllTextAsync(Path.Combine(Repo.Root, "src/Oan.App/MainWindow.xaml"));
        foreach (var header in headers)
            Assert.Contains($"<GridViewColumn Header=\"{header}\"", xaml);
    }

    [Fact]
    public async Task Sorting_by_a_column_reorders_the_rows()
    {
        using var temp = new TempFolder();
        temp.AddFile("b.txt", new string('x', 10));
        temp.AddFile("a.txt", new string('x', 900));

        var vm = new MainViewModel(new NoOps(), new Settings());
        await vm.NavigateAsync(temp.Root);

        Assert.Equal(["a.txt", "b.txt"], vm.Rows.Select(r => r.Name));

        vm.SortCommand.Execute(SortKey.Size);

        Assert.Equal(["b.txt", "a.txt"], vm.Rows.Select(r => r.Name));
    }

    private static T Sta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new InvalidOperationException("failed on the STA thread", failure);
        return result;
    }
}
