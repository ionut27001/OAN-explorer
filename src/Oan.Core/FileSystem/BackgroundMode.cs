using System.Runtime.InteropServices;

namespace Oan.Core.FileSystem;

/// <summary>For the size column's walks: in background mode their disk reads go at very low priority, behind whatever the user is waiting on.
/// Measured on the hard disk, twelve cold reads: 0.16 s alone, 0.8-10.9 s beside a normal walk, 0.3-0.4 s beside one in this mode.</summary>
internal static class BackgroundMode
{
    private const int ThreadModeBackgroundBegin = 0x0001_0000;

    /// <summary>On a thread of the walk's own only: the mode lasts until the thread ends, and a pool thread would carry it into other work.
    /// The cost: the reads go one at a time behind every other read on the disk, and the thread runs at priority 4. Measured: an idle walk up to
    /// a fifth slower, cold reads beside a copy 50 times slower, a warm walk beside a busy CPU 7 times. So not for the Sizes view, which the user watches.</summary>
    public static void Enter() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();
}
