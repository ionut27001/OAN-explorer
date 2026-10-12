using System.Windows;
using Microsoft.Win32;

namespace Oan.App.Platform;

internal static class Theme
{
    private const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static bool SystemDark => Registry.GetValue(Personalize, "AppsUseLightTheme", 1) is int light && light == 0;

    /// <summary>A theme from <see cref="UserThemes"/>, or null to follow Windows.</summary>
    public static string? Chosen { get; private set; }

    private static bool _glass;

    /// <summary>Whether what is on screen is dark. The user's theme decides when there is one.</summary>
    public static bool IsDark { get; private set; } = SystemDark;

    /// <summary>The parts of the theme that are not colours. Empty when following Windows.</summary>
    public static ThemeLook Look { get; private set; } = new();

    /// <summary>After the palette has been swapped, for whatever has to be moved by hand.</summary>
    public static event Action? Changed;

    /// <summary>The glass look while Glass is on in Settings; null while it is off.</summary>
    public static ThemeGlass? Glass { get; private set; }

    public static void Choose(string? name, bool glass)
    {
        Chosen = name;
        _glass = glass;
        if (Application.Current is { MainWindow: { } window } app) Apply(app, window);
    }

    public static void Follow(Application app, Window window)
    {
        Apply(app, window);

        void Changed(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General)
                window.Dispatcher.BeginInvoke(() => Apply(app, window));
        }

        // SystemEvents holds handlers statically: without the unhook the window is never collected.
        SystemEvents.UserPreferenceChanged += Changed;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= Changed;
    }

    private static void Apply(Application app, Window window)
    {
        // A theme that has gone missing or will not parse falls back to following Windows.
        var (palette, dark, look) = (Chosen is null ? null : UserThemes.Load(Chosen))
            ?? (UserThemes.BuiltIn(SystemDark), SystemDark, new ThemeLook());
        (IsDark, Look, Glass) = (dark, look, _glass ? look.Glass ?? new ThemeGlass() : null);
        if (_glass) UserThemes.Thin(palette);

        // Slot 0 is the palette; the control styles come after it in App.xaml.
        app.Resources.MergedDictionaries[0] = palette;
        WindowEffects.UseDarkTitleBar(window, dark);

        // Shell context menus follow the process's mode, not the window's: without this they open white.
        DarkMenus.Apply(dark);
        Changed?.Invoke();
    }
}
