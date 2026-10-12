using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Oan.App.Platform;

/// <summary>Everything in a theme that is not a colour. Every part is optional.</summary>
public record ThemeLook
{
    public string? Font { get; init; }
    public double? FontSize { get; init; }
    public double? RowHeight { get; init; }
    public double? CornerRadius { get; init; }
    public ThemeBackground? Background { get; init; }
    public ThemeGlass? Glass { get; init; }
    public ThemeAnimation? Animation { get; init; }
    public ThemeLayout? Layout { get; init; }
}

/// <summary>A picture from the themes folder, behind the window. It shows through whatever colours the theme gives an alpha.</summary>
public sealed record ThemeBackground(string? Image, double Opacity = 0.3, string Stretch = "UniformToFill", double Blur = 0);

/// <summary>
/// The slabs when Glass is on in Settings. Style "acrylic" is Windows' blur of what is really behind,
/// with a painted bevel (Rim, Edge, Tint); "liquid" bends the desktop wallpaper at the edge with a
/// shader (Bend, Blur) — a shader can only bend a picture OAN has, so not other windows.
/// </summary>
public sealed record ThemeGlass(
    string Style = "liquid", double Gap = 8, double Radius = 4, double Rim = 0.45, double Edge = 18, double Grain = 0.4,
    string Tint = "#6640B8C8", double Bend = 26, double Blur = 5);

/// <summary>What the list does when the folder changes: "fade", "slide", "zoom" or "none".</summary>
public sealed record ThemeAnimation(string Navigate = "none", int Milliseconds = 160);

/// <summary>Sidebar "left", "right" or "hidden"; toolbar "top", "bottom" or "hidden".</summary>
public sealed record ThemeLayout(string Sidebar = "left", string Toolbar = "top", bool StatusBar = true);

/// <summary>
/// User themes: a .json file in %AppData%\OAN\themes (Example.json lists every key).
/// Data only, never XAML: loose XAML is code that runs on load. Unknown keys, bad colours and
/// out-of-range numbers are skipped, so a bad theme looks like its base instead of blocking startup.
/// </summary>
internal static class UserThemes
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OAN", "themes");

    private sealed record ThemeFile : ThemeLook
    {
        public string? Base { get; init; }
        public Dictionary<string, string>? Colors { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<string> Names()
    {
        try
        {
            return [.. Directory.EnumerateFiles(Folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Null when there is no such theme, or it is not JSON.</summary>
    public static (ResourceDictionary Palette, bool Dark, ThemeLook Look)? Load(string name)
    {
        ThemeFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ThemeFile>(File.ReadAllText(Path.Combine(Folder, name + ".json")), Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
        if (file is null) return null;

        var dark = !string.Equals(file.Base, "light", StringComparison.OrdinalIgnoreCase);
        var palette = BuiltIn(dark, file);

        foreach (var (key, text) in file.Colors ?? [])
        {
            if (palette[key] is not SolidColorBrush) continue;

            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
                brush.Freeze();
                palette[key] = brush;
            }
            catch (FormatException) { }
        }
        return (palette, dark, file);
    }

    /// <summary>The built-in colours plus the measurements the control styles ask for by name.</summary>
    public static ResourceDictionary BuiltIn(bool dark, ThemeLook? look = null)
    {
        var palette = new ResourceDictionary { Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };

        palette["UiFont"] = new FontFamily(look?.Font is { Length: > 0 } font ? font + ", Segoe UI" : "Segoe UI");
        palette["UiFontSize"] = Math.Clamp(look?.FontSize ?? 13, 9, 24);
        palette["RowHeight"] = Math.Clamp(look?.RowHeight ?? 28, 18, 64);
        palette["Corner"] = new CornerRadius(Math.Clamp(look?.CornerRadius ?? 4, 0, 16));
        return palette;
    }

    /// <summary>Acrylic shows only through what is not opaque: opaque surfaces are thinned, translucent ones left as written.</summary>
    public static void Thin(ResourceDictionary palette)
    {
        foreach (var (key, alpha) in new[] { ("Surface", 0xB8), ("FieldBg", 0x99) })
        {
            if (palette[key] is not SolidColorBrush { Color: { A: 255 } colour }) continue;

            var brush = new SolidColorBrush(Color.FromArgb((byte)alpha, colour.R, colour.G, colour.B));
            brush.Freeze();
            palette[key] = brush;
        }

        // The title band and the address bar are one slab, so one colour.
        palette["Caption"] = palette["Surface"];
    }

    /// <summary>What a theme writes as its image to mean "whatever the desktop is showing".</summary>
    public const string Desktop = "desktop";

    /// <summary>The user's wallpaper, or a file from the themes folder and nowhere else.</summary>
    public static string? ImagePath(ThemeBackground? background)
    {
        if (background?.Image is not { Length: > 0 } image) return null;

        if (string.Equals(image, Desktop, StringComparison.OrdinalIgnoreCase))
        {
            var wallpaper = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
            return File.Exists(wallpaper) ? wallpaper : null;
        }

        var path = Path.Combine(Folder, Path.GetFileName(image));
        return File.Exists(path) ? path : null;
    }

    /// <summary>Creates the folder with Example.json: every key, with the value it has now.</summary>
    public static void EnsureFolder(bool dark)
    {
        Directory.CreateDirectory(Folder);
        var example = Path.Combine(Folder, "Example.json");
        if (File.Exists(example)) return;

        var palette = BuiltIn(dark);
        var file = new ThemeFile
        {
            Base = dark ? "dark" : "light",
            Colors = palette.Keys.Cast<object>()
                .Where(k => palette[k] is SolidColorBrush)
                .OrderBy(k => k.ToString())
                .ToDictionary(k => k.ToString()!, k => ((SolidColorBrush)palette[k]).Color.ToString()),
            Font = "Segoe UI",
            FontSize = 13,
            RowHeight = 28,
            CornerRadius = 4,
            Background = new ThemeBackground(Desktop),
            Glass = new ThemeGlass(),
            Animation = new ThemeAnimation(),
            Layout = new ThemeLayout(),
        };
        File.WriteAllText(example, JsonSerializer.Serialize(file, Json));
    }
}
