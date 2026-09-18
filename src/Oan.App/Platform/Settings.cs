using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oan.App.Platform;

public enum Backdrop { Mica, Acrylic, Solid }

/// <summary>
/// What the window remembers between runs. A file that is missing, unreadable or nonsense gives
/// the defaults: a settings file must never be what stops the app from opening.
/// </summary>
public sealed class Settings
{
    private static readonly int[] Sizes = [16, 32, 48, 256];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public int IconSize { get; set; } = 16;

    public Backdrop Backdrop { get; set; } = Backdrop.Mica;

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
        if (!Sizes.Contains(IconSize)) IconSize = 16;
        if (Backdrop is < Backdrop.Mica or > Backdrop.Solid) Backdrop = Backdrop.Mica;
        return this;
    }
}
