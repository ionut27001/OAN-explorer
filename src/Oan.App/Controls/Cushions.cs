using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Oan.App.Controls;

/// <summary>How a tile on the size map is painted: colour, cushion and label.</summary>
internal static class Cushions
{
    /// <summary>Under this even the smallest label would be clipped to nonsense.</summary>
    public const double LabelWide = 34;
    public const double LabelTall = 13;

    /// <summary>
    /// A colour per file type, saturated or cool throughout: muted warm neighbours read as one colour.
    /// The last is the slot for a name with no extension.
    /// </summary>
    private static readonly Color[] Hues =
    [
        Color.FromRgb(0x4C, 0xC2, 0xFF), Color.FromRgb(0x7A, 0xD1, 0x8A), Color.FromRgb(0xF5, 0x82, 0x20),
        Color.FromRgb(0xE4, 0x7B, 0xB8), Color.FromRgb(0x9B, 0x8C, 0xFF), Color.FromRgb(0x5F, 0xD4, 0xC4),
        Color.FromRgb(0xE8, 0x5D, 0x5D), Color.FromRgb(0xC9, 0xD1, 0x62), Color.FromRgb(0x6F, 0xA8, 0xFF),
        Color.FromRgb(0xB8, 0xE0, 0x6A), Color.FromRgb(0xF5, 0xC5, 0x18), Color.FromRgb(0x8A, 0xC8, 0xE8),
        Color.FromRgb(0xCF, 0x92, 0xE8), Color.FromRgb(0x6A, 0xE0, 0xA8), Color.FromRgb(0x5B, 0x7D, 0xB1),
        Color.FromRgb(0xA0, 0xA8, 0xB8),
    ];

    /// <summary>The extension folded to a hue, so the same extension always gets the same colour.</summary>
    public static int HueOf(string name)
    {
        var dot = name.LastIndexOf(Dot);
        if (dot < 0 || dot == name.Length - 1) return Hues.Length - 1;

        var hash = 17;
        for (var i = dot + 1; i < name.Length; i++) hash = (hash * 31) + char.ToLowerInvariant(name[i]);

        return Math.Abs(hash) % (Hues.Length - 1);
    }

    private const char Dot = '.';

    /// <summary>One frozen brush per colour: the gradient is in relative coordinates, so it fits any tile.</summary>
    private static readonly Brush[] Fills = Build(colour => new LinearGradientBrush(
        Mix(colour, Colors.White, 0.26), Mix(colour, Colors.Black, 0.30),
        new Point(0.1, 0), new Point(0.9, 1)));

    /// <summary>The line round a folder's children. Drawn over them: a margin per level adds up to more gap than tile.</summary>
    private static readonly Pen Frame =
        Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x18, 0x1C, 0x22)), 1));

    /// <summary>The plate a folder's own name is written on, over its children.</summary>
    private static readonly Brush Plate = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x18, 0x1E)));

    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));

    /// <summary>A darker line along the bottom and right of each tile, instead of a border round every block.</summary>
    private static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), 1));

    public static int Count => Hues.Length;

    public static Brush Fill(int hue) => Fills[hue % Fills.Length];

    /// <summary>
    /// How tall a folder's name band is, or zero when there is no room. The caller keeps that much
    /// off the top before laying out the children, or the plate covers the first one's name.
    /// </summary>
    public static double Header(Rect rect) => rect.Width < 70 || rect.Height < 40 ? 0 : 16;

    public static void Group(DrawingContext dc, Rect rect, string name, string much, double dpi)
    {
        if (rect.Width < 3 || rect.Height < 3) return;

        // Half a pixel in, so a one pixel pen lands on the boundary instead of straddling it.
        dc.DrawRectangle(null, Frame, new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));

        var band = Header(rect);
        if (band <= 0) return;

        var label = Text(name + "  (" + much + ")", 11, dpi, rect.Width - 8, Ink);
        var plate = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, band - 1);

        dc.DrawRectangle(Plate, null, plate);
        dc.PushClip(new RectangleGeometry(plate));
        dc.DrawText(label, new Point(rect.X + 4, rect.Y + 2));
        dc.Pop();
    }

    /// <summary>Two strokes, bottom and right, which is the cheap half of a lit cushion.</summary>
    public static void Shade(DrawingContext dc, Rect rect)
    {
        if (rect.Width < 4 || rect.Height < 4) return;

        var bottom = rect.Bottom - 0.5;
        var right = rect.Right - 0.5;
        dc.DrawLine(Edge, new Point(rect.X, bottom), new Point(right, bottom));
        dc.DrawLine(Edge, new Point(right, rect.Y), new Point(right, bottom));
    }

    /// <summary>The name, and the size under it when there is room. Dark ink: the fills are light in either theme.</summary>
    public static void Label(DrawingContext dc, string what, string much, Rect rect, double dpi)
    {
        if (rect.Width < LabelWide || rect.Height < LabelTall) return;

        // The type size is chosen for the tile: the alternative to shrinking the name is no name.
        var points = rect.Width >= 90 && rect.Height >= 30 ? 12 : rect.Width >= 60 ? 10 : 8;
        var pad = points >= 12 ? 4.0 : 2;

        var name = Text(what, points, dpi, rect.Width - pad * 2, Brushes.Black);
        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawText(name, new Point(rect.X + pad, rect.Y + pad - 1));

        if (rect.Height >= name.Height * 2 + pad * 2)
        {
            var size = Text(much, Math.Max(8, points - 1), dpi, rect.Width - pad * 2, Brushes.Black);
            dc.DrawText(size, new Point(rect.X + pad, rect.Y + pad - 1 + name.Height));
        }
        dc.Pop();
    }

    /// <summary>A sentence in the middle of an empty map, where a tile would have been.</summary>
    public static void Middle(DrawingContext dc, string text, Rect area, double dpi, Brush ink)
    {
        var label = Text(text, 13, dpi, area.Width, ink);
        dc.DrawText(label, new Point(area.X + (area.Width - label.Width) / 2,
                                     area.Y + (area.Height - label.Height) / 2));
    }

    private static FormattedText Text(string text, double size, double dpi, double room, Brush ink) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, ink, dpi)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Math.Max(1, room),
        };

    private static Brush[] Build(Func<Color, Brush> make)
    {
        var brushes = new Brush[Hues.Length];
        for (var i = 0; i < Hues.Length; i++) brushes[i] = Frozen(make(Hues[i]));
        return brushes;
    }

    private static Color Mix(Color colour, Color towards, double amount) => Color.FromRgb(
        (byte)(colour.R + (towards.R - colour.R) * amount),
        (byte)(colour.G + (towards.G - colour.G) * amount),
        (byte)(colour.B + (towards.B - colour.B) * amount));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
