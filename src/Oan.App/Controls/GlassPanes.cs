using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Oan.App.Platform;

namespace Oan.App.Controls;

/// <summary>
/// The window as separate slabs of glass. The gaps between them are real holes: the window's
/// region is cut down to the slabs, so the desktop shows through sharp and can be clicked.
/// "acrylic" slabs are Windows' own blur with a painted bevel; "liquid" ones are the wallpaper
/// bent at the edge by a shader. Everything drawn here sits under or over the real controls.
/// </summary>
internal sealed class GlassPanes
{
    private readonly MainWindow _window;
    private readonly Grid _root;
    private readonly Canvas _lenses = new() { IsHitTestVisible = false };
    private readonly Rectangle _grain = new() { IsHitTestVisible = false };
    private readonly Canvas _rims = new() { IsHitTestVisible = false };
    private ThemeGlass? _glass;
    private ImageSource? _behind;
    private bool _cut;

    public GlassPanes(MainWindow window)
    {
        _window = window;
        _root = (Grid)window.Content;

        _root.Children.Insert(1, _lenses);
        _root.Children.Insert(2, _grain);
        _root.Children.Add(_rims);

        foreach (var part in Slabs().SelectMany(s => s)) part.SizeChanged += (_, _) => Draw();
        window.StateChanged += (_, _) => Draw();
        window.LocationChanged += (_, _) => Aim();

        // WindowChrome sets its own region after every resize; ours goes back on once it has.
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(WindowEffects.Handle(window))?.AddHook(
            (nint _, int message, nint _, nint position, ref bool _) =>
            {
                // Only on a real resize (no SWP_NOSIZE): SetWindowRgn raises this message too.
                const int windowPosChanged = 0x0047, noSize = 0x0001;
                if (message == windowPosChanged && _glass is not null
                    && (Marshal.ReadInt32(position, 2 * nint.Size + 16) & noSize) == 0)
                    window.Dispatcher.BeginInvoke(Cut, DispatcherPriority.Render);
                return 0;
            });
    }

    /// <summary>The title band and the address bar are one slab: a gap would cut the selected tab off the bar.</summary>
    private FrameworkElement[][] Slabs() =>
        [[_window.TopBar, _window.AddressBar], [_window.Tools], [_window.Side], [_window.Page], [_window.StatusBar]];

    private double Radius => Math.Clamp(_glass?.Radius ?? 3, 0, 24);

    public void Apply(ThemeGlass? glass)
    {
        _glass = glass;
        var gap = glass is null ? 0 : Math.Clamp(glass.Gap, 0, 40) / 2;

        // Half a gap round every part; the frame's negative margin keeps the slabs at the window edge.
        _window.Frame.Margin = new Thickness(-gap);
        foreach (var part in Slabs().SelectMany(s => s)) part.Margin = new Thickness(gap);
        _window.TopBar.Margin = new Thickness(gap, gap, gap, 0);
        _window.AddressBar.Margin = new Thickness(gap, 0, gap, gap);

        if (glass is null)
        {
            _window.Side.ClearValue(Control.BackgroundProperty);
            _window.SideSplitter.SetResourceReference(Control.BackgroundProperty, "Surface");
        }
        else
        {
            _window.Side.SetResourceReference(Control.BackgroundProperty, "Surface");
            _window.SideSplitter.Background = Brushes.Transparent;
            _window.Page.BorderThickness = new Thickness(0);
        }

        // Liquid needs the picture behind it; without one it falls back to acrylic.
        _behind = glass is { Style: var style } && string.Equals(style, "liquid", StringComparison.OrdinalIgnoreCase)
            ? GlassBrushes.Behind(glass.Blur)
            : null;

        _grain.Fill = glass is { Grain: > 0 } ? GlassBrushes.Surface : null;
        _grain.Opacity = Math.Clamp(glass?.Grain ?? 0, 0, 1);

        Draw();
    }

    private void Draw()
    {
        _rims.Children.Clear();
        _lenses.Children.Clear();
        if (_glass is null)
        {
            Cut();
            return;
        }

        if (_behind is not null)
        {
            Lenses();
            return;
        }

        var panes = new GeometryGroup { FillRule = FillRule.Nonzero };
        var (lit, shaded) = GlassBrushes.Bevels(_glass);

        foreach (var box in Boxes())
        {
            panes.Children.Add(new RectangleGeometry(box, Radius, Radius));

            var slab = Bevelled(box, lit, shaded);
            Canvas.SetLeft(slab, box.X);
            Canvas.SetTop(slab, box.Y);
            _rims.Children.Add(slab);
        }

        panes.Freeze();
        _grain.Clip = panes;
        Cut();
    }

    /// <summary>The painted slab of the acrylic style: a rim, and a bevel on all four sides.</summary>
    private Border Bevelled(Rect box, Color lit, Color shaded)
    {
        // No wider than a third of the slab, or on the status bar the bevel meets itself.
        var depth = Math.Min(Math.Clamp(_glass!.Edge, 0, 60), Math.Min(box.Width, box.Height) / 3);
        return new Border
        {
            Width = box.Width, Height = box.Height,
            CornerRadius = new CornerRadius(Radius),
            BorderThickness = new Thickness(1),
            BorderBrush = GlassBrushes.Rim(_glass!.Rim),
            ClipToBounds = true,
            Child = new Grid
            {
                Children =
                {
                    // Lit from the top left, shaded bottom right; the middle is left alone.
                    new Rectangle { Height = depth, VerticalAlignment = VerticalAlignment.Top, Fill = GlassBrushes.Fade(lit, 0, 1) },
                    new Rectangle { Width = depth, HorizontalAlignment = HorizontalAlignment.Left, Fill = GlassBrushes.Fade(lit, 1, 0) },
                    new Rectangle { Height = depth * 1.4, VerticalAlignment = VerticalAlignment.Bottom, Fill = GlassBrushes.Fade(shaded, 0, -1) },
                    new Rectangle { Width = depth * 1.4, HorizontalAlignment = HorizontalAlignment.Right, Fill = GlassBrushes.Fade(shaded, -1, 0) },

                    new Border { CornerRadius = new CornerRadius(Math.Max(0, Radius - 1)), BorderThickness = new Thickness(1), BorderBrush = GlassBrushes.Bevel(_glass!.Rim) },
                },
            },
        };
    }

    /// <summary>One lens per slab. No bevel or rim on top: the shader draws the edge.</summary>
    private void Lenses()
    {
        var panes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var box in Boxes())
        {
            panes.Children.Add(new RectangleGeometry(box, Radius, Radius));

            var lens = new Rectangle
            {
                Width = box.Width, Height = box.Height, Tag = box,
                Fill = new ImageBrush(_behind) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill },
                Effect = new LiquidGlassEffect
                {
                    Size = new Point(box.Width, box.Height),
                    Radius = Radius,
                    Edge = Math.Min(Math.Clamp(_glass!.Edge, 1, 80), Math.Min(box.Width, box.Height) / 2),
                    Bend = Math.Clamp(_glass.Bend, 0, 120),
                    Shine = Math.Clamp(_glass.Rim, 0, 1),
                },
            };
            Canvas.SetLeft(lens, box.X);
            Canvas.SetTop(lens, box.Y);
            _lenses.Children.Add(lens);
        }

        panes.Freeze();
        _grain.Clip = panes;
        Aim();
        Cut();
    }

    /// <summary>Points every lens at the part of the wallpaper that is behind it right now.</summary>
    private void Aim()
    {
        if (_behind is null || PresentationSource.FromVisual(_window) is not { CompositionTarget: { } target }) return;

        // The desktop shows the picture as "fill", centred: invert that to find what is behind a slab.
        var (screenW, screenH) = (SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var scale = Math.Max(screenW / _behind.Width, screenH / _behind.Height);
        var (left, top) = ((screenW - _behind.Width * scale) / 2, (screenH - _behind.Height * scale) / 2);
        var corner = target.TransformFromDevice.Transform(_window.PointToScreen(new Point()));

        foreach (var lens in _lenses.Children.OfType<Rectangle>())
        {
            var box = (Rect)lens.Tag;
            ((ImageBrush)lens.Fill).Viewbox = new Rect(
                (corner.X + box.X - left) / scale, (corner.Y + box.Y - top) / scale, box.Width / scale, box.Height / scale);
        }
    }

    private IEnumerable<Rect> Boxes()
    {
        foreach (var slab in Slabs())
        {
            var box = Rect.Empty;
            foreach (var part in slab.Where(p => p.IsVisible && p.ActualWidth >= 1))
                box.Union(part.TransformToAncestor(_root).TransformBounds(new Rect(part.RenderSize)));
            if (!box.IsEmpty) yield return box;
        }
    }

    /// <summary>Cuts the window down to its slabs. Maximised it stays whole, or the desktop shows through mid-screen.</summary>
    private void Cut()
    {
        var hwnd = WindowEffects.Handle(_window);
        if (hwnd == 0) return;

        var wanted = _glass is { Gap: > 0 } && _window.WindowState == WindowState.Normal
            && PresentationSource.FromVisual(_window) is { CompositionTarget: not null };
        if (!wanted)
        {
            if (_cut) SetWindowRgn(hwnd, 0, true);
            _cut = false;
            return;
        }

        var toDevice = PresentationSource.FromVisual(_window)!.CompositionTarget!.TransformToDevice;
        var corner = (int)Math.Round(Radius * 2 * toDevice.M11);
        var whole = CreateRectRgn(0, 0, 0, 0);

        foreach (var box in Boxes())
        {
            var (a, b) = (toDevice.Transform(box.TopLeft), toDevice.Transform(box.BottomRight));
            var piece = CreateRoundRectRgn((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X) + 1, (int)Math.Round(b.Y) + 1, corner, corner);
            CombineRgn(whole, whole, piece, 2);
            DeleteObject(piece);
        }

        // The system owns the region from here on.
        SetWindowRgn(hwnd, whole, true);
        _cut = true;
    }

    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint into, nint one, nint other, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
}
