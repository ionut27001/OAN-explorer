using System.Runtime.InteropServices;

namespace Oan.App.Platform;

/// <summary>File name ordering as Explorer does it: "file2" before "file10".</summary>
internal sealed class NaturalSort : IComparer<string>
{
    public static readonly NaturalSort Instance = new();

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
}
