using Oan.App.Platform;
using Oan.Core.Shell;

namespace Oan.App.ViewModels;

/// <summary>What the window looks like: the view, the icons, the theme and what is behind it.</summary>
public sealed class AppearanceViewModel(Settings settings, Action remember) : Observable
{
    private bool _backdropSupported = true;

    public string? ThemeName => settings.Theme;

    /// <summary>Glass is acrylic cut into slabs, so while it is on the material is not a choice.</summary>
    public bool Glass
    {
        get => settings.Glass;
        set
        {
            if (settings.Glass == value) return;

            settings.Glass = value;
            Theme.Choose(settings.Theme, value);
            OnChanged();
            OnChanged(nameof(CanPickMaterial));
            remember();
        }
    }

    public bool CanPickMaterial => Supported && !Glass;

    public System.Windows.Input.ICommand ViewCommand => new RelayCommand(p => View = int.Parse((string)p!));

    public ColumnSet Columns { get; } = new(settings, remember);

    /// <summary>"Follow Windows" first, then whatever is in the themes folder right now.</summary>
    public IReadOnlyList<ThemeChoice> Themes =>
        [new(null, settings.Theme is null), .. UserThemes.Names().Select(n => new ThemeChoice(n, n == settings.Theme))];

    public System.Windows.Input.ICommand ChooseThemeCommand => new RelayCommand(p =>
    {
        settings.Theme = p as string;
        Theme.Choose(settings.Theme, settings.Glass);
        OnChanged(nameof(Themes));
        remember();
    });

    public System.Windows.Input.ICommand OpenThemesCommand => new RelayCommand(() =>
    {
        UserThemes.EnsureFolder(Theme.IsDark);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UserThemes.Folder) { UseShellExecute = true });
        OnChanged(nameof(Themes));
    });

    /// <summary>Set by the window. Returns false when Windows refused the attribute.</summary>
    public Func<Backdrop, bool>? Apply { get; set; }

    /// <summary>Called when every row needs its icon re-fetched at the new size.</summary>
    public Action? IconSizeChanged { get; set; }

    /// <summary>0 is the details table, 1 is the plain list, anything larger is the icon grid at that many pixels.</summary>
    public int View
    {
        get => settings.View;
        set
        {
            if (settings.View == value) return;

            var was = IconSize;
            settings.View = value;

            // Each size is its own cache entry and a 256px bitmap is a quarter of a megabyte, so the old size is dropped.
            if (IconSize != was)
            {
                ShellIcons.Clear();
                IconSizeChanged?.Invoke();
            }

            OnChanged();
            OnChanged(nameof(IconSize));
            OnChanged(nameof(IsDetails));
            OnChanged(nameof(IsList));
            OnChanged(nameof(TileWidth));
            OnChanged(nameof(TileHeight));
            remember();
        }
    }

    /// <summary>Ctrl+wheel: one view larger or smaller, stopping at either end.</summary>
    public void Step(int by)
    {
        var next = Array.IndexOf(Settings.Views, View) + by;
        if (next >= 0 && next < Settings.Views.Length) View = Settings.Views[next];
    }

    /// <summary>How big an icon the rows ask the shell for. Only the grid wants more than 16.</summary>
    public int IconSize => View > Settings.Views_List ? View : 16;

    public bool IsDetails => View == Settings.Views_Details;

    public bool IsList => View == Settings.Views_List;

    public IconStyle Icons
    {
        get => settings.Icons;
        set
        {
            if (settings.Icons == value) return;

            settings.Icons = value;
            OnChanged();
            OnChanged(nameof(PlainIcons));
            remember();
        }
    }

    public bool PlainIcons => settings.Icons == IconStyle.Simple;

    public bool Treemap
    {
        get => settings.Treemap;
        set
        {
            if (settings.Treemap == value) return;

            settings.Treemap = value;
            OnChanged();
            remember();
        }
    }

    /// <summary>
    /// The box one item is laid out in. The list wraps into columns across and scrolls down, where Explorer's
    /// fills a column down and scrolls sideways: the panel scrolls one way.
    /// </summary>
    public double TileWidth => IsList ? 240 : IconSize + 48;

    public double TileHeight => IsList ? 24 : IconSize + 60;

    public Backdrop Backdrop
    {
        get => settings.Backdrop;
        set
        {
            if (settings.Backdrop == value) return;

            settings.Backdrop = value;
            Supported = Apply?.Invoke(value) ?? false;

            OnChanged();
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
            OnChanged(nameof(CanPickMaterial));
        }
    }

    public string? Note =>
        Supported ? null : "Not supported on this Windows build — the window stays solid.";
}

/// <summary>One line of the theme picker. A null name is "Follow Windows".</summary>
public sealed record ThemeChoice(string? Name, bool Current)
{
    public string Label => Name ?? "Follow Windows";
}
