using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Oan.App.Controls;

/// <summary>The selection rectangle. An adorner, so the list's layout, virtualisation and scrolling are untouched.</summary>
public sealed class RubberBand : Adorner
{
    private static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)), 1));
    private static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xC2, 0xFF)));

    private Rect _band;

    public RubberBand(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

    public void Update(Rect band)
    {
        _band = band;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_band.Width < 1 || _band.Height < 1) return;

        // Snapped to half-pixels so the 1px edge stays one crisp pixel instead of two grey ones.
        var snapped = new Rect(
            Math.Round(_band.X) + 0.5, Math.Round(_band.Y) + 0.5,
            Math.Max(0, Math.Round(_band.Width) - 1), Math.Max(0, Math.Round(_band.Height) - 1));

        dc.DrawRectangle(Fill, Edge, snapped);
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
