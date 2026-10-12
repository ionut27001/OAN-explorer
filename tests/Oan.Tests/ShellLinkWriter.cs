using System.Runtime.InteropServices;
using Oan.Core.Links;

namespace Oan.Tests;

/// <summary>
/// Writes a .lnk, so the tests have shortcuts of their own rather than the machine's. Uses the
/// same two interfaces the engine reads with, from the other end — and on its own STA thread,
/// because that is the apartment a shell link lives in.
/// </summary>
static class ShellLinkWriter
{
    public static string At(string linkPath, string target, string? arguments = null,
        string? workingDirectory = null, string? iconFile = null, int iconIndex = 0)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { Write(linkPath, target, arguments, workingDirectory, iconFile, iconIndex); }
            catch (Exception e) { failure = e; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw failure;
        return linkPath;
    }

    /// <summary>An .url is an INI file, so writing one needs nothing but text.</summary>
    public static string Url(string path, string? url, string? iconFile = null, int? iconIndex = null)
    {
        var lines = new List<string> { "[InternetShortcut]" };
        if (url is not null) lines.Add($"URL={url}");
        if (iconFile is not null) lines.Add($"IconFile={iconFile}");
        if (iconIndex is not null) lines.Add($"IconIndex={iconIndex}");

        File.WriteAllLines(path, lines);
        return path;
    }

    static void Write(string linkPath, string target, string? arguments,
        string? workingDirectory, string? iconFile, int iconIndex)
    {
        var hr = LinkNative.CoCreateInstance(in LinkNative.CLSID_ShellLink, 0,
            LinkNative.CLSCTX_INPROC_SERVER, in LinkNative.IID_IShellLinkW, out var created);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var link = (LinkNative.IShellLinkW)created;
            Marshal.ThrowExceptionForHR(link.SetPath(target));

            if (arguments is not null) Marshal.ThrowExceptionForHR(link.SetArguments(arguments));
            if (workingDirectory is not null) Marshal.ThrowExceptionForHR(link.SetWorkingDirectory(workingDirectory));
            if (iconFile is not null) Marshal.ThrowExceptionForHR(link.SetIconLocation(iconFile, iconIndex));

            Marshal.ThrowExceptionForHR(((LinkNative.IPersistFile)created).Save(linkPath, true));
        }
        finally
        {
            Marshal.ReleaseComObject(created);
        }
    }
}
