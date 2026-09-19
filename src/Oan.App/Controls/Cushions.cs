using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Oan.App.Controls;

/// <summary>
/// How a tile on the size map is painted: its colour, its cushion, and the two lines of text on
/// it. Separate from the map, whose job is deciding which rectangle goes where — these answer
/// what it looks like once it is there, and the two change for different reasons.
/// </summary>
internal static class Cushions
{
    /// <summary>Under this a label would be clipped to nonsense, so none is drawn.</summary>
    public const double LabelWide = 54;
    public const double LabelTall = 30;

    /// <summary>
    /// A colour per tile, cycled by position. Not by file type: in a folder of folders every type
    /// is the same and the picture came out one flat blue, which is what made it unreadable.
    /// Adjacent tiles differing is the whole job of colour here — the area already says how big,
    /// the label already says what.
    /// </summary>
    private static readonly Color[] Hues =
    [
        Color.FromRgb(0x4C, 0xC2, 0xFF), Color.FromRgb(0x7A, 0xD1, 0x8A), Color.FromRgb(0xFF, 0xB4, 0x5C),
        Color.FromRgb(0xE4, 0x7B, 0xB8), Color.FromRgb(0x9B, 0x8C, 0xFF), Color.FromRgb(0x5F, 0xD4, 0xC4),
        Color.FromRgb(0xFF, 0x8A, 0x8A), Color.FromRgb(0xC9, 0xD1, 0x62), Color.FromRgb(0x6F, 0xA8, 0xFF),
        Color.FromRgb(0xD9, 0x9A, 0x6C),
    ];

    /// <summary>
    /// One cushion per colour, not per tile. The gradient is in relative coordinates, so the same
    /// frozen brush stretches to any rectangle it is given — ten brushes for the whole map, built
    /// once, rather than an allocation per tile on every redraw.
    /// </summary>
    private static readonly Brush[] Fills = Build(colour => new LinearGradientBrush(
        Mix(colour, Colors.White, 0.26), Mix(colour, Colors.Black, 0.30),
        new Point(0.1, 0), new Point(0.9, 1)));

    /// <summary>The surround a folder's children sit on: its own hue, much darker.</summary>
    private static readonly Brush[] Surrounds =
        Build(colour => new SolidColorBrush(Mix(colour, Colors.Black, 0.62)));

    /// <summary>
    /// A darker line along the bottom and right of each tile. With the cushion above it, that is
    /// what a raised surface looks like, and it means the edges read without a border drawn round
    /// every block — borders at this density eat the small tiles alive.
    /// </summary>
    private static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), 1));

    public static int Count => Hues.Length;

    public static Brush Fill(int hue) => Fills[hue % Fills.Length];

    public static Brush Surround(int hue) => Surrounds[hue % Surrounds.Length];

    /// <summary>Two strokes, bottom and right, which is the cheap half of a lit cushion.</summary>
    public static void Shade(DrawingContext dc, Rect rect)
    {
        if (rect.Width < 4 || rect.Height < 4) return;

        var bottom = rect.Bottom - 0.5;
        var right = rect.Right - 0.5;
        dc.DrawLine(Edge, new Point(rect.X, bottom), new Point(right, bottom));
        dc.DrawLine(Edge, new Point(right, rect.Y), new Point(right, bottom));
    }

    /// <summary>
    /// The name, and the size under it when there is room for both. Drawn dark rather than in the
    /// theme's foreground: the fills are light in either theme, and a light-on-light label is the
    /// same unreadable picture in a different way. Top left, which is also the cushion's lightest
    /// corner, so the contrast holds by construction.
    /// </summary>
    public static void Label(DrawingContext dc, string what, string much, Rect rect, double dpi)
    {
        if (rect.Width < LabelWide || rect.Height < LabelTall) return;

        var name = Text(what, 12, dpi, rect.Width - 8);
        var size = Text(much, 11, dpi, rect.Width - 8);

        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawText(name, new Point(rect.X + 4, rect.Y + 3));
        if (rect.Height >= LabelTall + name.Height) dc.DrawText(size, new Point(rect.X + 4, rect.Y + 3 + name.Height));
        dc.Pop();
    }

    private static FormattedText Text(string text, double size, double dpi, double room) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.Black, dpi)
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
