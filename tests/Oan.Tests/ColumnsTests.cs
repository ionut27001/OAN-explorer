using System.Windows.Controls;
using Oan.App.Controls;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Model;
using Oan.Core.Shell;
using Xunit;

namespace Oan.Tests;

[Collection(DiskBound.Name)]
public class ColumnsTests
{
    [Fact]
    public void Switching_a_column_takes_it_out_of_the_view_and_puts_it_back_where_it_was()
    {
        var saved = 0;
        var settings = new Settings();
        var shown = new ColumnSet(settings, () => saved++);

        var seen = OnSta.Run(() =>
        {
            var view = new GridView();
            foreach (var key in new[] { "Name", "Size", "Kind", "Modified", "Created", "Attributes" })
                view.Columns.Add(new GridViewColumn { Header = key });

            string Now() => string.Join(' ', view.Columns.Select(c => c.Header));

            _ = new DetailsColumns(view, shown);
            var first = Now();
            shown["Created"] = true;
            var created = Now();
            shown["Size"] = false;
            var noSize = Now();
            shown["Name"] = false;
            return (first, created, noSize, nameStays: Now());
        });

        Assert.Equal("Name Size Kind Modified", seen.first);
        Assert.Equal("Name Size Kind Modified Created", seen.created);
        Assert.Equal("Name Kind Modified Created", seen.noSize);
        Assert.Equal("Name Kind Modified Created", seen.nameStays);
        Assert.Equal(new[] { "Kind", "Modified", "Created" }, settings.Columns);
        Assert.Equal(2, saved);
    }

    [Fact]
    public void The_header_menu_offers_every_column_the_details_view_has()
    {
        var (offered, columns) = OnSta.Run(() =>
        {
            var styles = FileColumns();
            var menu = (ContextMenu)styles["HeaderMenu"];
            var view = (GridView)styles["DetailsView"];
            return (menu.Items.OfType<MenuItem>().Count(i => i.IsCheckable), view.Columns.Count);
        });

        Assert.Equal(columns, offered);
    }

    [Fact]
    public void The_View_submenu_on_empty_space_offers_what_the_header_menu_does()
    {
        var header = OnSta.Run(() =>
        {
            var menu = ((ContextMenu)FileColumns()["HeaderMenu"]).Items.OfType<MenuItem>().ToArray();
            var views = menu.Single(i => (string)i.Header == "View").Items.OfType<MenuItem>();
            return views.Concat(menu.Where(i => i.IsCheckable)).Select(i => (string)i.Header).ToArray();
        });

        var lines = new ViewMenu(new AppearanceViewModel(new Settings(), () => { })).Item().Items!;

        Assert.Equal(header, lines.Where(line => line.Label.Length > 0).Select(line => line.Label));
    }

    [Fact]
    public void Picking_from_the_View_submenu_switches_the_view_or_one_column()
    {
        var settings = new Settings();
        var look = new AppearanceViewModel(settings, () => { });
        var menu = new ViewMenu(look);
        OwnMenuItem Line(string label) => menu.Item().Items!.Single(line => line.Label == label);

        Assert.True(Line("Details").Checked);
        Assert.True(Line("Size").Checked);
        Assert.False(Line("Date created").Checked);
        Assert.False(Line("Name").Enabled);

        menu.Run(Line("Large icons").Id);
        menu.Run(Line("Date created").Id);
        menu.Run(Line("Size").Id);

        Assert.Equal(96, look.View);
        Assert.Equal((true, false), (Line("Large icons").Checked, Line("Details").Checked));
        Assert.Equal(new[] { "Kind", "Modified", "Created" }, settings.Columns);

        // Below where the shell's numbering starts, so a pick reads as OAN's own.
        Assert.All(menu.Item().Items!, line => Assert.True(line.Id < MenuBuilder.FirstCommand));
    }

    private static System.Windows.ResourceDictionary FileColumns()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        return (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
            new Uri("/Oan;component/Themes/FileColumns.xaml", UriKind.Relative));
    }

    [Fact]
    public void A_settings_file_without_columns_shows_the_usual_three()
    {
        using var temp = new TempFolder();
        var path = temp.AddFile("settings.json", """{ "Columns": null }""");

        Assert.Equal(new[] { "Size", "Kind", "Modified" }, Settings.Load(path).Columns);
    }

    [Fact]
    public void Attributes_read_as_Explorer_writes_them()
    {
        Assert.Equal("RHA", Format.Attributes(FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly));
        Assert.Equal("D", Format.Attributes(FileAttributes.Directory));
        Assert.Equal("", Format.Attributes(FileAttributes.Normal));
    }
}
