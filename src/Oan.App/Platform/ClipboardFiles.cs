using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Oan.App.Platform;

/// <summary>
/// The real Windows file clipboard, so a cut here pastes in Explorer and the other way round.
/// The paths travel as CF_HDROP; whether it was a copy or a cut travels in the shell's
/// "Preferred DropEffect" stream, which is a single DWORD of DROPEFFECT flags.
/// </summary>
internal static class ClipboardFiles
{
    private const string DropEffect = "Preferred DropEffect";
    private const int DropEffectCopy = 5; // DROPEFFECT_COPY | DROPEFFECT_LINK, exactly what Explorer writes
    private const int DropEffectMove = 2;

    /// <summary>False when another program is holding the clipboard open.</summary>
    public static bool Put(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return false;

        var files = new StringCollection();
        foreach (var path in paths) files.Add(path);

        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData(DropEffect, new MemoryStream(BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy)));

        try
        {
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    public static (IReadOnlyList<string> Paths, bool Cut) Get()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
                return ([], false);

            return (paths, IsCut(data));
        }
        catch (ExternalException)
        {
            return ([], false);
        }
    }

    public static bool HasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch (ExternalException) { return false; }
    }

    public static void Clear()
    {
        try { Clipboard.Clear(); }
        catch (ExternalException) { }
    }

    private static bool IsCut(IDataObject data)
    {
        if (data.GetData(DropEffect) is not MemoryStream stream) return false;

        var bytes = stream.ToArray();
        return bytes.Length >= 4 && (BitConverter.ToInt32(bytes, 0) & DropEffectMove) != 0;
    }
}
