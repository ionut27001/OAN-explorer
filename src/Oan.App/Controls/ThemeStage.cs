using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Oan.App.Platform;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>What a theme needs moved by hand: the picture, the layout, the glass, the arrival animation.</summary>
internal sealed class ThemeStage
{
    private readonly MainWindow _window;
    private readonly GlassPanes _glass;
    private readonly AppearanceViewModel _look;
    private GridLength _sidebarWidth;

    public ThemeStage(MainWindow window, MainViewModel vm)
    {
        _window = window;
        _look = vm.Look;
        _glass = new GlassPanes(window);
        _sidebarWidth = window.SidebarColumn.Width;

        Theme.Changed += Dress;
        window.Closed += (_, _) => Theme.Changed -= Dress;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPath)) Arrive();
        };
    }

    private void Dress()
    {
        var look = Theme.Look;
        Picture(look.Background);
        Arrange(look.Layout ?? new ThemeLayout());
        _glass.Apply(Theme.Glass);

        // Glass needs acrylic; without it the chosen material comes back.
        _look.Apply?.Invoke(Theme.Glass is null ? _look.Backdrop : Backdrop.Acrylic);
    }

    private void Picture(ThemeBackground? background)
    {
        var image = _window.Wallpaper;
        image.Visibility = Visibility.Collapsed;
        image.Source = null;

        if (UserThemes.ImagePath(background) is not { } path) return;

        try
        {
            // OnLoad, or the file stays open as long as the window lives.
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            image.Source = bitmap;
        }
        catch (Exception e) when (e is NotSupportedException or System.IO.IOException or ArgumentException)
        {
            return;
        }

        image.Opacity = Math.Clamp(background!.Opacity, 0, 1);
        image.Stretch = Enum.TryParse<Stretch>(background.Stretch, ignoreCase: true, out var stretch) ? stretch : Stretch.UniformToFill;
        image.Effect = background.Blur > 0 ? new BlurEffect { Radius = Math.Min(background.Blur, 40) } : null;
        image.Visibility = Visibility.Visible;
    }

    private void Arrange(ThemeLayout layout)
    {
        var hidden = Is(layout.Sidebar, "hidden");
        var column = _window.SidebarColumn;

        if (!hidden && column.Width.Value == 0) column.Width = _sidebarWidth;
        if (hidden && column.Width.Value > 0) _sidebarWidth = column.Width;

        column.MinWidth = hidden ? 0 : 150;
        if (hidden) column.Width = new GridLength(0);
        _window.Side.Visibility = _window.SideSplitter.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;

        // Mirroring the grid swaps its columns without touching a width; the panes are turned back.
        _window.Body.FlowDirection = Is(layout.Sidebar, "right") ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _window.Side.FlowDirection = _window.Page.FlowDirection = FlowDirection.LeftToRight;
        _window.Page.BorderThickness = Is(layout.Sidebar, "right") ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);

        _window.StatusBar.Visibility = layout.StatusBar ? Visibility.Visible : Visibility.Collapsed;
        _window.Tools.Visibility = Is(layout.Toolbar, "hidden") ? Visibility.Collapsed : Visibility.Visible;

        // DockPanel docks outermost first: to sit above the status bar the toolbar must come after it.
        var bottom = Is(layout.Toolbar, "bottom");
        var children = _window.Frame.Children;
        children.Remove(_window.Tools);
        children.Insert(children.IndexOf(_window.StatusBar) + (bottom ? 1 : 0), _window.Tools);
        DockPanel.SetDock(_window.Tools, bottom ? Dock.Bottom : Dock.Top);
    }

    private void Arrive()
    {
        var motion = Theme.Look.Animation;
        if (motion is null || Is(motion.Navigate, "none")) return;

        var list = _window.List;
        var time = TimeSpan.FromMilliseconds(Math.Clamp(motion.Milliseconds, 40, 600));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        list.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });

        if (Is(motion.Navigate, "slide"))
        {
            var shift = new TranslateTransform();
            list.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, 0, time) { EasingFunction = ease });
        }
        else if (Is(motion.Navigate, "zoom"))
        {
            var scale = new ScaleTransform();
            list.RenderTransformOrigin = new Point(0.5, 0.5);
            list.RenderTransform = scale;
            var grow = new DoubleAnimation(0.97, 1, time) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private static bool Is(string? value, string name) => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
}
