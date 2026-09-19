using Oan.App.Platform;
using Oan.Core.Shell;

namespace Oan.App.ViewModels;

/// <summary>
/// What the window looks like: how big the icons are, and what is behind it.
///
/// Both are settings, both are remembered, and neither has anything to do with which folder is
/// open — which is why they are not in MainViewModel any more.
/// </summary>
public sealed class AppearanceViewModel(Settings settings, Action remember) : Observable
{
    private bool _backdropSupported = true;

    /// <summary>Set by the window. Returns false when Windows refused the attribute.</summary>
    public Func<Backdrop, bool>? Apply { get; set; }

    /// <summary>Called when every row needs its icon re-fetched at the new size.</summary>
    public Action? IconSizeChanged { get; set; }

    /// <summary>
    /// One of the four sizes the shell keeps an image list for. 16 is the details view; anything
    /// larger switches to the tile grid, because a details row with a 48px icon is mostly gap.
    /// </summary>
    public int IconSize
    {
        get => settings.IconSize;
        set
        {
            if (settings.IconSize == value) return;

            settings.IconSize = value;

            // Each size is its own cache entry and a 256px bitmap is a quarter of a megabyte, so
            // the size being left behind is dropped rather than kept for a switch back.
            ShellIcons.Clear();
            IconSizeChanged?.Invoke();

            OnChanged();
            OnChanged(nameof(IsDetails));
            OnChanged(nameof(TileWidth));
            OnChanged(nameof(TileHeight));
            remember();
        }
    }

    public bool IsDetails => IconSize == 16;

    /// <summary>The tile a grid item is laid out in: the icon, then room for two lines of name.</summary>
    public double TileWidth => IconSize + 48;

    public double TileHeight => IconSize + 60;

    public Backdrop Backdrop
    {
        get => settings.Backdrop;
        set
        {
            if (settings.Backdrop == value) return;

            settings.Backdrop = value;
            Supported = Apply?.Invoke(value) ?? false;

            OnChanged();
            OnChanged(nameof(ChromeOverAcrylic));
            remember();
        }
    }

    /// <summary>False when Windows refused the attribute; the window is solid whatever is chosen.</summary>
    public bool Supported
    {
        get => _backdropSupported;
        set
        {
            if (!Set(ref _backdropSupported, value)) return;

            OnChanged(nameof(Note));
            OnChanged(nameof(ChromeOverAcrylic));
        }
    }

    /// <summary>
    /// Acrylic blurs whatever window is behind this one, so the chrome cannot know how bright its
    /// own background is: measured over a white window, the muted status text came out at 1.31:1.
    /// In that one mode the bars that carry text are given a scrim. Mica tints from the wallpaper
    /// alone and measured the same over a white window as over the desktop, so it is left clear.
    /// </summary>
    public bool ChromeOverAcrylic => Supported && Backdrop == Backdrop.Acrylic;

    public string? Note =>
        Supported ? null : "Not supported on this Windows build — the window stays solid.";
}
