using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Oan.App.Platform;

namespace Oan.App.Controls;

/// <summary>The brushes and the frosted wallpaper <see cref="GlassPanes"/> paints with.</summary>
internal static class GlassBrushes
{
    /// <summary>Lit from the top left, dark at the bottom right.</summary>
    public static Brush Rim(double strength)
    {
        var light = (byte)(255 * Math.Clamp(strength, 0, 1));
        return Frozen(new LinearGradientBrush(
        [
            new GradientStop(Color.FromArgb(light, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb((byte)(light / 6), 255, 255, 255), 0.45),
            new GradientStop(Color.FromArgb((byte)(light / 2), 0, 0, 0), 1),
        ], new Point(0.2, 0), new Point(0.8, 1)));
    }

    /// <summary>The inner line of the rim.</summary>
    public static Brush Bevel(double strength)
    {
        var depth = (byte)(120 * Math.Clamp(strength, 0, 1));
        return Frozen(new LinearGradientBrush(
        [
            new GradientStop(Color.FromArgb(depth, 0, 0, 0), 0),
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.5),
            new GradientStop(Color.FromArgb((byte)(depth / 2), 255, 255, 255), 1),
        ], new Point(0, 0), new Point(0, 1)));
    }

    /// <summary>The bevel's two colours. The glass tint goes in the shaded side, never as a painted band.</summary>
    public static (Color Lit, Color Shaded) Bevels(ThemeGlass glass)
    {
        var tint = Colour(glass.Tint) is SolidColorBrush { Color: var c } ? c : Color.FromArgb(0x50, 0x40, 0xC0, 0xB0);
        var strength = Math.Clamp(glass.Rim, 0, 1);

        static byte Mix(byte a, byte b, double t) => (byte)(a + (b - a) * t);
        return (
            Color.FromArgb((byte)(70 * strength), Mix(255, tint.R, 0.25), Mix(255, tint.G, 0.25), Mix(255, tint.B, 0.25)),
            Color.FromArgb((byte)Math.Min(255, tint.A * 1.2 * strength + 40 * strength), (byte)(tint.R * 0.35), (byte)(tint.G * 0.35), (byte)(tint.B * 0.35)));
    }

    /// <summary>From the colour to nothing. Eased: a linear ramp reads as a flat stripe.</summary>
    public static Brush Fade(Color colour, int x, int y)
    {
        var clear = Color.FromArgb(0, colour.R, colour.G, colour.B);
        var start = new Point(x < 0 ? 1 : 0, y < 0 ? 1 : 0);
        var end = new Point(x == 0 ? start.X : 1 - start.X, y == 0 ? start.Y : 1 - start.Y);

        return Frozen(new LinearGradientBrush(
        [
            new GradientStop(colour, 0),
            new GradientStop(Color.FromArgb((byte)(colour.A * 0.45), colour.R, colour.G, colour.B), 0.25),
            new GradientStop(Color.FromArgb((byte)(colour.A * 0.12), colour.R, colour.G, colour.B), 0.6),
            new GradientStop(clear, 1),
        ], start, end));
    }

    /// <summary>Fine grain over slow swells, on a coarse grid that wraps so the tile has no seam.</summary>
    public static readonly Brush Surface = MakeSurface();

    public static Brush MakeSurface()
    {
        const int size = 256, cells = 6;
        var random = new Random(7);
        var coarse = new double[cells, cells];
        for (var i = 0; i < cells; i++)
            for (var j = 0; j < cells; j++) coarse[i, j] = random.NextDouble() * 2 - 1;

        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (u, v) = (x * (double)cells / size, y * (double)cells / size);
                var (i, j) = ((int)u, (int)v);
                var (fu, fv) = (Smooth(u - i), Smooth(v - j));
                double At(int a, int b) => coarse[(i + a) % cells, (j + b) % cells];

                var swell = (At(0, 0) * (1 - fu) + At(1, 0) * fu) * (1 - fv) + (At(0, 1) * (1 - fu) + At(1, 1) * fu) * fv;
                var value = swell * 0.6 + (random.NextDouble() * 2 - 1) * 0.4;

                var at = (y * size + x) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = (byte)(value > 0 ? 255 : 0);
                pixels[at + 3] = (byte)(Math.Abs(value) * 38);
            }
        }

        var tile = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        return Frozen(new ImageBrush(tile)
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size * 2, size * 2), ViewportUnits = BrushMappingMode.Absolute,
        });

        static double Smooth(double t) => t * t * (3 - 2 * t);
    }

    public static Brush? Colour(string? text)
    {
        try { return text is { Length: > 0 } ? Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(text))) : null; }
        catch (FormatException) { return null; }
    }

    public static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>The wallpaper, frosted once: a live blur on five screen-sized elements is five blurs a frame.</summary>
    public static ImageSource? Behind(double blur)
    {
        if (UserThemes.ImagePath(Theme.Look.Background ?? new ThemeBackground(UserThemes.Desktop)) is not { } path) return null;

        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 1920;
            bitmap.EndInit();

            var (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
            var picture = new Image { Source = bitmap, Stretch = Stretch.Fill, Effect = blur > 0 ? new System.Windows.Media.Effects.BlurEffect { Radius = Math.Min(blur, 40) } : null };
            picture.Measure(new Size(width, height));
            picture.Arrange(new Rect(0, 0, width, height));

            var frosted = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            frosted.Render(picture);
            frosted.Freeze();
            return frosted;
        }
        catch (Exception e) when (e is NotSupportedException or System.IO.IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
