using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Oan.App.Controls;
using Oan.App.ViewModels;
using Oan.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// Renders the size map to a PNG so a change to how it looks can be looked at, rather than
/// described. Not part of the normal pass: it asserts almost nothing, it produces a picture.
///
///   dotnet test --filter "FullyQualifiedName~TreemapPreview"
/// </summary>
[Collection(DiskBound.Name)]
[Trait("Category", "Benchmark")]
public sealed class TreemapPreview(ITestOutputHelper output)
{
    [Fact]
    public void Draw()
    {
        var into = Environment.GetEnvironmentVariable("OAN_PREVIEW")
                   ?? Path.Combine(Path.GetTempPath(), "oan-treemap.png");

        var bytes = Sta(() =>
        {
            var map = new TreemapView { Width = 900, Height = 380, Rows = Rows() };
            map.Measure(new Size(900, 380));
            map.Arrange(new Rect(0, 0, 900, 380));
            map.UpdateLayout();

            var target = new RenderTargetBitmap(900, 380, 96, 96, PixelFormats.Pbgra32);
            target.Render(map);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        });

        File.WriteAllBytes(into, bytes);
        output.WriteLine($"preview {bytes.Length:N0} bytes -> {into}");
        Assert.True(bytes.Length > 1000, "nothing was drawn");
    }

    /// <summary>Shaped like a real profile: one folder carrying most of it, then a long tail.</summary>
    private static EntryViewModel[] Rows()
    {
        (string Name, long Size)[] folders =
        [
            ("AppData", 200_508_145_514), (".cache", 11_293_798_828), (".android", 5_500_000_000),
            (".gradle", 3_384_148_331), (".claude", 1_700_000_000), (".gemini", 1_400_000_000),
            (".local", 1_400_000_000), (".codex", 865_000_000), (".antigravity", 842_000_000),
            (".cargo", 616_000_000), (".config", 101_000_000), (".javacpp", 45_000_000),
            ("Downloads", 1_200_000_000), ("Pictures", 700_000_000), ("Desktop", 170_000_000),
            (".Ld9VirtualBox", 324_000), (".deepswarm", 18_000), (".cline", 3_400),
        ];

        return [.. folders.Select(f =>
        {
            var row = new EntryViewModel(new FileEntry
            {
                Name = f.Name,
                FullPath = $@"C:\Users\oance\{f.Name}",
                Attributes = FileAttributes.Directory,
            }, 16);

            row.Measured(new Oan.Core.FileSystem.FolderSizeResult
            {
                Path = $@"C:\Users\oance\{f.Name}",
                Bytes = f.Size,
            });
            return row;
        })];
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

        if (failure is not null) throw new InvalidOperationException("preview failed", failure);
        return result;
    }
}
