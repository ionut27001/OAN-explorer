namespace Oan.Core.Links;

/// <summary>Paths past MAX_PATH. Measured: SHGetFileInfo, SHParseDisplayName and SHCreateItemFromParsingName read a 331-character
/// plain path, and refuse any <c>\\?\</c>-prefixed path with E_INVALIDARG. So a prefix is taken off before a shell call, never put on.
/// <c>longPathAware</c> and LongPathsEnabled govern the Win32 file layer, not shell32. See docs/shortcuts-and-links.md.</summary>
public static class LongPath
{
    /// <summary>Including the terminating null, so 260 characters is already one too many.</summary>
    public const int MaxPath = 260;

    private const string ExtendedPrefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>Whether this process escapes MAX_PATH in the Win32 layer; needs both the registry policy and the app manifest.</summary>
    public static bool EnabledForThisProcess { get; } = Enabled();

    /// <summary>True when this path is at or past the limit a Win32 call has without a prefix.</summary>
    public static bool IsLong(string path) => path.Length >= MaxPath;

    public static bool IsExtended(string path) =>
        path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal);

    /// <summary>The <c>\\?\</c> form, for a raw Win32 call; never for shell32. The prefix also turns off normalisation,
    /// so the path is fully qualified first: <c>..</c> would otherwise name a literal directory.</summary>
    public static string Extended(string path)
    {
        if (string.IsNullOrEmpty(path) || IsExtended(path)) return path;

        var full = Path.GetFullPath(path);

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? UncPrefix + full[2..]
            : ExtendedPrefix + full;
    }

    /// <summary>The path with any extended prefix taken off.</summary>
    public static string Plain(string path)
    {
        if (path.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path[UncPrefix.Length..];
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) return path[ExtendedPrefix.Length..];
        return path;
    }

    /// <summary>What to hand a shell call: the plain path, prefix removed. It does not shorten to 8.3.</summary>
    public static string? ForShell(string path) =>
        string.IsNullOrEmpty(path) ? null : Plain(path);

    /// <summary>A shell answer put back into the caller's form. Measured: for a long path the shell answers in 8.3 or
    /// <c>\\?\</c>-prefixed form, so compare against a path OAN asked for only after this.</summary>
    public static string FromShell(string path) =>
        string.IsNullOrEmpty(path) ? path : Long(Plain(path)) ?? Plain(path);

    /// <summary>The 8.3 form. Null when the volume has 8.3 generation off, the default off the system volume since Windows 8.</summary>
    public static string? Short(string path) => Named(path, LinkNative.GetShortPathName);

    /// <summary>The full form of a path whose components may be 8.3 names. Null when the item is not there.</summary>
    public static string? Long(string path) => Named(path, LinkNative.GetLongPathName);

    /// <summary>The two-call buffer dance. A volume with 8.3 generation off returns its input unchanged, which is an answer.</summary>
    private static string? Named(string path, Func<string, char[]?, uint, uint> call)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // Long input goes in prefixed; these are kernel32, so unlike shell32 they take one.
        var input = IsLong(path) ? Extended(path) : path;

        var size = call(input, null, 0);
        if (size == 0) return null;

        var buffer = new char[size];
        var written = call(input, buffer, size);
        if (written == 0 || written >= size) return null;

        return Plain(new string(buffer, 0, (int)written));
    }

    private static bool Enabled()
    {
        try
        {
            return LinkNative.RtlAreLongPathsEnabled();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
