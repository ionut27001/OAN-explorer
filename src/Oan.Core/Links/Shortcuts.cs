using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Oan.Core.Shell;

namespace Oan.Core.Links;

/// <summary>Reads what a shortcut points at, without following it. A .lnk is COM, so it runs on an STA thread on a deadline:
/// a shortcut into a dead share blocks for the SMB timeout. IShellLink::Resolve is never called (it shows a search dialog).
/// A .url is read as an INI file; no network request is made.</summary>
public static class Shortcuts
{
    /// <summary>How long reading one shortcut gets. Measured: the slowest of 202 Start Menu links is 100x inside this.</summary>
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromMilliseconds(1500);

    /// <summary>Deadline for asking whether the target is still there; only a share that has gone away makes it slow.</summary>
    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromMilliseconds(500);

    /// <summary>IShellLink documents MAX_PATH for these; room to spare.</summary>
    private const int Room = 1024;

    /// <summary>Arguments are capped at INFOTIPSIZE, which is larger than a path.</summary>
    private const int ArgumentRoom = 4096;

    private static readonly object Gate = new();
    private static StaThread? _apartment;

    public static bool IsShortcut(string path) => KindOf(path) is not null;

    /// <summary>What <paramref name="path"/> points at. Never throws; failure comes back in <see cref="LinkTarget.Refusal"/>.</summary>
    public static LinkTarget Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Refused("", LinkKind.ShellLink, "No shortcut was given.");

        return KindOf(path) switch
        {
            LinkKind.ShellLink => ShellLink(path),
            LinkKind.InternetShortcut => Internet(path),
            _ => Refused(path, LinkKind.ShellLink, "That is not a shortcut — OAN reads .lnk and .url."),
        };
    }

    public static Task<LinkTarget> ResolveAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Resolve(path), ct);

    private static LinkKind? KindOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return LinkKind.ShellLink;
        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase)) return LinkKind.InternetShortcut;
        return null;
    }

    /// <summary>One apartment for all shortcuts, not one per call; a lost one is replaced.</summary>
    private static StaThread Apartment()
    {
        if (_apartment is null or { Lost: true }) _apartment = new StaThread("OAN shortcuts");
        return _apartment;
    }

    private static LinkTarget ShellLink(string path)
    {
        lock (Gate)
        {
            var apartment = Apartment();
            LinkTarget? read = null;

            if (!apartment.Run(() => read = Read(path), ReadDeadline) || read is null)
                return Refused(path, LinkKind.ShellLink,
                    "This shortcut is not answering — it may point into a share that is not there.");

            return read.Target is null || read.LeadsTo == LinkLeadsTo.Url ? read : Probed(apartment, read);
        }
    }

    /// <summary>Adds whether the target is still there. Read back only when the deadline was met.</summary>
    private static LinkTarget Probed(StaThread apartment, LinkTarget link)
    {
        bool exists = false, isDirectory = false;

        // abandon: false — a probe that will not come back is a dead share; the apartment must survive it.
        var answered = apartment.Run(() => (exists, isDirectory) = Look(link.Target!), ProbeDeadline, abandon: false);

        if (!answered)
            return link with
            {
                Refusal = $"OAN could not reach {link.Target} to see whether it is still there.",
            };

        return link with
        {
            TargetExists = exists,
            LeadsTo = Leads(exists ? isDirectory : link.TargetWasDirectory),
        };
    }

    private static (bool Exists, bool IsDirectory) Look(string target) =>
        Directory.Exists(target) ? (true, true) : (File.Exists(target), false);

    private static LinkLeadsTo Leads(bool? isDirectory) =>
        isDirectory switch { true => LinkLeadsTo.Folder, false => LinkLeadsTo.File, null => LinkLeadsTo.Unknown };

    /// <summary>Runs on the STA thread; everything it creates is released before it returns.</summary>
    private static LinkTarget Read(string path)
    {
        object? created = null;
        try
        {
            var hr = LinkNative.CoCreateInstance(in LinkNative.CLSID_ShellLink, 0,
                LinkNative.CLSCTX_INPROC_SERVER, in LinkNative.IID_IShellLinkW, out created);

            var link = created as LinkNative.IShellLinkW;
            var persist = created as LinkNative.IPersistFile;
            if (hr < 0 || link is null || persist is null)
                return Refused(path, LinkKind.ShellLink, $"Windows would not start its shortcut reader — {Explain(hr)}");

            var loaded = persist.Load(path, LinkNative.STGM_READ);

            return loaded < 0
                ? Refused(path, LinkKind.ShellLink, $"This shortcut could not be read — {Explain(loaded)}")
                : Fields(path, link);
        }
        finally
        {
            if (created is not null) Marshal.ReleaseComObject(created);
        }
    }

    private static LinkTarget Fields(string path, LinkNative.IShellLinkW link)
    {
        var found = default(LinkNative.WIN32_FIND_DATAW);
        var buffer = new StringBuilder(Room);

        // Flags 0, not SLGP_RAWPATH, which would hand back "%windir%\explorer.exe" unexpanded. The find data comes out of
        // the .lnk itself, so a shortcut whose target is gone can still say whether it was a folder.
        var target = link.GetPath(buffer, buffer.Capacity, ref found, 0) >= 0 && buffer.Length > 0
            ? buffer.ToString()
            : null;

        var stored = found.dwFileAttributes == 0
            ? (bool?)null
            : ((FileAttributes)found.dwFileAttributes & FileAttributes.Directory) != 0;

        var (icon, index) = IconOf(link);

        return new LinkTarget
        {
            LinkPath = path,
            Kind = LinkKind.ShellLink,
            Target = target,
            Arguments = Text(text => link.GetArguments(text, text.Capacity), ArgumentRoom),
            WorkingDirectory = Text(text => link.GetWorkingDirectory(text, text.Capacity), Room),
            IconFile = icon,
            IconIndex = index,
            TargetWasDirectory = stored,
            LeadsTo = Leads(stored),

            // A shortcut to Control Panel or the Recycle Bin has an ID list and no path at all.
            Refusal = target is null
                ? "This shortcut points at a place with no file path, so only Windows can open it."
                : null,
        };
    }

    private static (string? File, int? Index) IconOf(LinkNative.IShellLinkW link)
    {
        var buffer = new StringBuilder(Room);
        if (link.GetIconLocation(buffer, buffer.Capacity, out var index) < 0 || buffer.Length == 0)
            return (null, null);

        return (buffer.ToString(), index);
    }

    private static string? Text(Func<StringBuilder, int> call, int room)
    {
        var buffer = new StringBuilder(room);
        return call(buffer) >= 0 && buffer.Length > 0 ? buffer.ToString() : null;
    }

    private static LinkTarget Internet(string path)
    {
        var (url, icon, index, error) = ReadIni(path);

        if (error is not null)
            return Refused(path, LinkKind.InternetShortcut, $"This shortcut could not be read — {error}");
        if (url is null)
            return Refused(path, LinkKind.InternetShortcut, "This .url file has no address in it.");

        // A file:// .url can name a local folder, which OAN can enter.
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) && parsedUrl.IsFile)
        {
            var local = new LinkTarget
            {
                LinkPath = path,
                Kind = LinkKind.InternetShortcut,
                Target = parsedUrl.LocalPath,
                IconFile = icon,
                IconIndex = icon is null ? null : index,
            };

            lock (Gate) return Probed(Apartment(), local);
        }

        return new LinkTarget
        {
            LinkPath = path,
            Kind = LinkKind.InternetShortcut,
            Target = url,
            IconFile = icon,
            IconIndex = icon is null ? null : index,
            LeadsTo = LinkLeadsTo.Url,

            // TargetExists stays null: knowing would take a network request.
        };
    }

    /// <summary>Split on the first '=' only: a URL has its own.</summary>
    private static (string? Url, string? Icon, int? Index, string? Error) ReadIni(string path)
    {
        string? url = null, icon = null;
        int? index = null;
        var inside = false;

        try
        {
            // UTF-8 with BOM detection. A legacy code page keeps the URL (ASCII) and can lose accents in IconFile.
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inside = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inside) continue;

                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();
                if (value.Length == 0) continue;

                if (key.Equals("URL", StringComparison.OrdinalIgnoreCase)) url = value;
                else if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) icon = value;
                else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    index = parsed;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null, null, e.Message);
        }

        return (url, icon, index, null);
    }

    private static LinkTarget Refused(string path, LinkKind kind, string why) =>
        new() { LinkPath = path, Kind = kind, Refusal = why };

    private static string Explain(int hresult) =>
        Marshal.GetExceptionForHR(hresult)?.Message ?? $"0x{hresult:X8}";
}
