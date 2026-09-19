using System.Windows;
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

    /// <summary>
    /// The rule the rubber band starts on. Headers and scroll bars are inside the list, and the
    /// band used to begin on both — which captured the mouse, so the header never raised its own
    /// click and dragging the scroll bar drew a selection box across the window.
    /// </summary>
    [Fact]
    public void The_band_starts_only_where_the_rows_are_drawn()
    {
        var (onRow, onHeader, onScrollBar) = Sta(() =>
        {
            // Measured and arranged, because a presenter's content only becomes its visual child
            // once it has been through a layout pass.
            var rowLabel = new TextBlock { Text = "notes.txt" };
            var presenter = new ScrollContentPresenter { Content = rowLabel };
            presenter.Measure(new Size(200, 200));
            presenter.Arrange(new Rect(0, 0, 200, 200));

            var headerLabel = new TextBlock { Text = "Size" };
            _ = new GridViewColumnHeader { Content = headerLabel };

            // The scroll bar's grip, as its own template builds it.
            var grip = new Thumb();
            _ = new Track { Thumb = grip };

            return (BandArea.Starts(rowLabel), BandArea.Starts(headerLabel), BandArea.Starts(grip));
        });

        Assert.True(onRow, "a press on a row is where the band belongs");
        Assert.False(onHeader, "a press on a column header must not start a band");
        Assert.False(onScrollBar, "a press on the scroll bar must not start a band");
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
