using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oan.App.Platform;

/// <summary>MicaAlt is DWMSBT_TABBEDWINDOW, the material Windows 11 made for tabbed windows and Explorer uses.</summary>
public enum Backdrop { Mica, MicaAlt, Acrylic, Solid }

/// <summary>
/// Where the sidebar and the tabs get their icons: the shell's own, or one flat glyph per kind.
/// The file list always uses the shell's.
/// </summary>
public enum IconStyle { Windows, Simple }

/// <summary>
/// What the window remembers between runs. A file that is missing, unreadable or nonsense gives
/// the defaults: a settings file must never be what stops the app from opening.
/// </summary>
public sealed class Settings
{
    internal static readonly int[] Views = [Views_Details, Views_List, 32, 48, 96, 256];

    internal const int Views_Details = 0;
    internal const int Views_List = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 0 is the details table, 1 is the plain list, anything larger is the icon grid at that many pixels.
    /// One number, because a mode plus a size would be free to disagree.
    /// </summary>
    public int View { get; set; } = Views_Details;

    public Backdrop Backdrop { get; set; } = Backdrop.MicaAlt;

    /// <summary>Measure folder sizes in the listing.</summary>
    public bool FolderSizes { get; set; } = true;

    /// <summary>Show the size map of the folder. Off by default.</summary>
    public bool Treemap { get; set; }

    public IconStyle Icons { get; set; } = IconStyle.Windows;

    /// <summary>The window as separate slabs of acrylic, with the desktop showing between them.</summary>
    public bool Glass { get; set; }

    /// <summary>A file in the themes folder, by name. Null follows Windows between light and dark.</summary>
    public string? Theme { get; set; }

    /// <summary>The details columns shown besides Name, by their sort key.</summary>
    public List<string> Columns { get; set; } = ["Size", "Kind", "Modified"];

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OAN", "settings.json");

    public static Settings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json);
            return settings is null ? new Settings() : settings.Checked();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                      or ArgumentException or NotSupportedException)
        {
            return new Settings();
        }
    }

    /// <summary>Why it could not be written, or null. Never throws: a setting is not worth a crash.</summary>
    public string? Save(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// A value the shell has no image list for would render as an upscaled 16px icon, and a
    /// backdrop outside the enum would leave the window painting nothing at all.
    /// </summary>
    private Settings Checked()
    {
        if (!Views.Contains(View)) View = Views_Details;
        if (!Enum.IsDefined(Backdrop)) Backdrop = Backdrop.MicaAlt;
        if (!Enum.IsDefined(Icons)) Icons = IconStyle.Windows;
        Columns ??= new Settings().Columns;
        return this;
    }
}
