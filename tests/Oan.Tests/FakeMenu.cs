using System.Runtime.InteropServices;
using Oan.Core.Shell;

namespace Oan.Tests;

/// <summary>
/// The shell's IContextMenu in miniature: a real COM object, so the app's Marshal.ReleaseComObject reaches its Release,
/// which notes the thread it came from. It says it is agile, so the runtime calls it where it is instead of carrying the
/// call back to the apartment that made it — a release from the wrong thread shows as one.
/// </summary>
sealed class FakeMenu
{
    private delegate int QueryInterfaceCall(nint self, in Guid iid, out nint found);
    private delegate int CountCall(nint self);
    private delegate int MenuCall(nint self, nint a, nint b, nint c, nint d, nint e);

    private static readonly Guid[] Answers =
        [new("00000000-0000-0000-c000-000000000046"), Native.IID_IContextMenu, new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")];

    // Kept for the life of the run: the runtime may call a wrapper's Release late, from its finalizer.
    // The three IContextMenu slots are never called by these tests.
    private static readonly Delegate[] Calls =
        [(QueryInterfaceCall)QueryInterface, (CountCall)AddRef, (CountCall)Release, (MenuCall)NotImplemented, (MenuCall)NotImplemented, (MenuCall)NotImplemented];

    private static readonly nint Table = Marshal.AllocHGlobal(Calls.Length * IntPtr.Size);

    private readonly nint _self = Marshal.AllocHGlobal(2 * IntPtr.Size);
    private int _count = 1;

    static FakeMenu()
    {
        for (var i = 0; i < Calls.Length; i++)
            Marshal.WriteIntPtr(Table, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(Calls[i]));
    }

    private FakeMenu()
    {
        Marshal.WriteIntPtr(_self, Table);
        Marshal.WriteIntPtr(_self, IntPtr.Size, GCHandle.ToIntPtr(GCHandle.Alloc(this)));
    }

    /// <summary>The managed thread whose call let go of the last reference; null while something still holds it.</summary>
    public int? ReleasedOn { get; private set; }

    /// <summary>A new one and the runtime's wrapper for it, the way MenuBuilder hands one back: made on the thread that asks.</summary>
    public static (FakeMenu Fake, Native.IContextMenu Menu) Make()
    {
        var fake = new FakeMenu();
        var menu = (Native.IContextMenu)Marshal.GetObjectForIUnknown(fake._self);
        Marshal.Release(fake._self);
        return (fake, menu);
    }

    private static FakeMenu Of(nint self) => (FakeMenu)GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, IntPtr.Size)).Target!;

    private static int QueryInterface(nint self, in Guid iid, out nint found)
    {
        found = Answers.Contains(iid) ? self : 0;
        if (found == 0) return unchecked((int)0x80004002);

        AddRef(self);
        return 0;
    }

    private static int AddRef(nint self) => Interlocked.Increment(ref Of(self)._count);

    private static int Release(nint self)
    {
        var fake = Of(self);
        var left = Interlocked.Decrement(ref fake._count);
        if (left == 0) fake.ReleasedOn = Environment.CurrentManagedThreadId;
        return left;
    }

    private static int NotImplemented(nint self, nint a, nint b, nint c, nint d, nint e) => unchecked((int)0x80004001);
}
