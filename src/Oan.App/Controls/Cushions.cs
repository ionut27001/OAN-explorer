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
    /// A colour per file type. Every .vhdx the same magenta, every .dll the same blue — which is
    /// what turns the picture into an answer: you see that one colour is most of the disk, then
    /// read one of its labels to learn what it is.
    ///
    /// By type only works once the leaves are files. The flat map, where every tile is a folder,
    /// has exactly one type in it and came out a single blue — there, position decides instead.
    /// </summary>
    private static readonly Color[] Hues =
    [
        Color.FromRgb(0x4C, 0xC2, 0xFF), Color.FromRgb(0x7A, 0xD1, 0x8A), Color.FromRgb(0xFF, 0xB4, 0x5C),
        Color.FromRgb(0xE4, 0x7B, 0xB8), Color.FromRgb(0x9B, 0x8C, 0xFF), Color.FromRgb(0x5F, 0xD4, 0xC4),
        Color.FromRgb(0xFF, 0x8A, 0x8A), Color.FromRgb(0xC9, 0xD1, 0x62), Color.FromRgb(0x6F, 0xA8, 0xFF),
        Color.FromRgb(0xD9, 0x9A, 0x6C), Color.FromRgb(0xB8, 0xE0, 0x6A), Color.FromRgb(0xFF, 0xD1, 0x6A),
        Color.FromRgb(0x8A, 0xC8, 0xE8), Color.FromRgb(0xCF, 0x92, 0xE8), Color.FromRgb(0x6A, 0xE0, 0xA8),
        Color.FromRgb(0xE8, 0xA0, 0x7A),
    ];

    /// <summary>
    /// Which colour a name gets. The extension, folded to a hue — the same extension always lands
    /// on the same one, which is the whole point, and a name without one shares the last slot.
    /// </summary>
    public static int HueOf(string name)
    {
        var dot = name.LastIndexOf(Dot);
        if (dot < 0 || dot == name.Length - 1) return Hues.Length - 1;

        var hash = 17;
        for (var i = dot + 1; i < name.Length; i++) hash = (hash * 31) + char.ToLowerInvariant(name[i]);

        return Math.Abs(hash) % (Hues.Length - 1);
    }

    private const char Dot = '.';

    /// <summary>
    /// One cushion per colour, not per tile. The gradient is in relative coordinates, so the same
    /// frozen brush stretches to any rectangle it is given — ten brushes for the whole map, built
    /// once, rather than an allocation per tile on every redraw.
    /// </summary>
    private static readonly Brush[] Fills = Build(colour => new LinearGradientBrush(
        Mix(colour, Colors.White, 0.26), Mix(colour, Colors.Black, 0.30),
        new Point(0.1, 0), new Point(0.9, 1)));

    /// <summary>
    /// The line round a folder's children. A line drawn over them rather than a margin taken from
    /// them: a margin per level adds up, and six levels deep the picture was more gap than tile.
    /// Neutral, because colour now means file type and a coloured frame would claim a type.
    /// </summary>
    private static readonly Pen Frame =
        Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x18, 0x1C, 0x22)), 1));

    /// <summary>The plate a folder's own name is written on, over its children.</summary>
    private static readonly Brush Plate = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x18, 0x1E)));

    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));

    /// <summary>
    /// A darker line along the bottom and right of each tile. With the cushion above it, that is
    /// what a raised surface looks like, and it means the edges read without a border drawn round
    /// every block — borders at this density eat the small tiles alive.
    /// </summary>
    private static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), 1));

    public static int Count => Hues.Length;

    public static Brush Fill(int hue) => Fills[hue % Fills.Length];

    /// <summary>
    /// A folder: the line round its children, and its own name on a plate at the top when there
    /// is room. The name is what the frame alone could not say, and it is what makes the picture
    /// answer "what is all that" rather than only "how much".
    /// </summary>
    /// <summary>
    /// How tall a folder's name band is, or zero when there is no room for one. The caller keeps
    /// that much off the top before laying the children out — the plate is drawn over them, and
    /// without the gap it covers the first one's own name.
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

    /// <summary>
    /// The name, and the size under it when there is room for both. Drawn dark rather than in the
    /// theme's foreground: the fills are light in either theme, and a light-on-light label is the
    /// same unreadable picture in a different way. Top left, which is also the cushion's lightest
    /// corner, so the contrast holds by construction.
    /// </summary>
    public static void Label(DrawingContext dc, string what, string much, Rect rect, double dpi)
    {
        if (rect.Width < LabelWide || rect.Height < LabelTall) return;

        var name = Text(what, 12, dpi, rect.Width - 8, Brushes.Black);
        var size = Text(much, 11, dpi, rect.Width - 8, Brushes.Black);

        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawText(name, new Point(rect.X + 4, rect.Y + 3));
        if (rect.Height >= LabelTall + name.Height) dc.DrawText(size, new Point(rect.X + 4, rect.Y + 3 + name.Height));
        dc.Pop();
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
