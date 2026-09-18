using System.Windows;
using Microsoft.Win32;

namespace Oan.App.Platform;

internal static class Theme
{
    private const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark => Registry.GetValue(Personalize, "AppsUseLightTheme", 1) is int light && light == 0;

    public static void Follow(Application app, Window window)
    {
        Apply(app, window);

        void Changed(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General)
                window.Dispatcher.BeginInvoke(() => Apply(app, window));
        }

        // SystemEvents keeps its handlers in a static list, so an attached window would never
        // be collected and the handler would go on firing at a dispatcher that has shut down.
        SystemEvents.UserPreferenceChanged += Changed;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= Changed;
    }

    private static void Apply(Application app, Window window)
    {
        var dark = IsDark;
        var source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);

        // Slot 0 is the palette; App.xaml keeps the control styles after it so they can
        // reference the palette keys through DynamicResource.
        app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = source };
        WindowEffects.UseDarkTitleBar(window, dark);
    }
}
