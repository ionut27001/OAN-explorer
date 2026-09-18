using Microsoft.Win32.SafeHandles;

namespace Oan.Core.Mount;

/// <summary>
/// One image OAN has attached. <see cref="DriveLetter"/> is null when it could not be
/// determined — the image is mounted either way.
/// </summary>
public sealed record MountedImage(string IsoPath, string? DriveLetter, string? PhysicalPath);

/// <summary>
/// What one mount attempt did. <see cref="Error"/> is null on success and carries the reason
/// otherwise; <see cref="DriveLetter"/> is null when it could not be determined.
/// </summary>
public sealed record MountResult
{
    public required string IsoPath { get; init; }
    public string? DriveLetter { get; init; }
    public string? PhysicalPath { get; init; }
    public string? Error { get; init; }

    public bool Ok => Error is null;

    public static MountResult Refused(string isoPath, string why) => new() { IsoPath = isoPath, Error = why };
}

/// <summary>
/// Mounts disk images through the VirtDisk API, so opening an ISO never runs a third-party
/// shell extension.
///
/// Only images this instance attached can be ejected through it. That is not a policy check
/// that could be argued with — a detach needs the handle the attach returned, and those are
/// the only handles here. This machine already has a virtual disk attached that Bitdefender
/// owns; unmounting the user's security software is the failure this shape rules out.
/// </summary>
public sealed class IsoMount : IDisposable
{
    /// <summary>How long to wait for the volume to surface with a drive letter before reporting
    /// null: the attach returns before the volume appears. One number, because there has only
    /// ever been one — a constructor argument nobody passes is a decision nobody made.</summary>
    private static readonly TimeSpan LetterDeadline = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, Attachment> _mounted = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Only what this instance attached.</summary>
    public IReadOnlyList<MountedImage> Mounted
    {
        get { lock (_mounted) return [.. _mounted.Values.Select(a => a.AsImage())]; }
    }

    public Task<MountResult> MountAsync(string isoPath, CancellationToken ct = default) =>
        Task.Run(() => Mount(isoPath, ct), ct);

    /// <summary>Null when the image is no longer mounted, otherwise why it still is.</summary>
    public Task<string?> UnmountAsync(string isoPath, CancellationToken ct = default) =>
        Task.Run(() => Unmount(isoPath), ct);

    public void Dispose()
    {
        Attachment[] ours;
        lock (_mounted)
        {
            // Without this, a mount still inside the wait for a letter when the window closes
            // finishes afterwards, stores itself here, and is never ejected by anybody.
            _disposed = true;
            ours = [.. _mounted.Values];
            _mounted.Clear();
        }
        foreach (var attachment in ours) Discard(attachment);
    }

    private MountResult Mount(string isoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(isoPath)) return MountResult.Refused(isoPath, "no path given");

        var path = Path.GetFullPath(isoPath);
        if (!File.Exists(path)) return MountResult.Refused(path, "there is no file at that path");

        lock (_mounted)
        {
            // Measured: a second attach of the same image succeeds, with a second drive letter.
            // Nothing in Windows stops it, so the answer to "mount this again" is the mount
            // we already have.
            if (_mounted.TryGetValue(path, out var already)) return already.Refreshed();
        }

        ct.ThrowIfCancellationRequested();

        var opened = VirtDisk.Open(path, out var handle);
        if (opened != 0)
        {
            handle.Dispose();
            return MountResult.Refused(path, VirtDisk.Explain(opened, "not a disk image Windows can mount"));
        }

        var attached = VirtDisk.Attach(handle);
        if (attached != 0)
        {
            handle.Dispose();
            return MountResult.Refused(path, VirtDisk.Explain(attached, "could not be attached"));
        }

        var mount = new Attachment(path, handle, VirtDisk.PhysicalPath(handle));
        try
        {
            mount.DriveLetter = WaitForLetter(mount.PhysicalPath, ct);
        }
        catch (OperationCanceledException)
        {
            Discard(mount);
            throw;
        }

        return Remember(mount);
    }

    private string? Unmount(string isoPath)
    {
        if (string.IsNullOrWhiteSpace(isoPath)) return "no path given";

        var path = Path.GetFullPath(isoPath);
        Attachment? ours;
        lock (_mounted)
        {
            if (!_mounted.TryGetValue(path, out ours)) return "OAN did not mount that image, so it will not eject it";
        }

        // Measured: ejecting with a file still open on the volume succeeds. There is no state
        // to refuse on here.
        //
        // Looked up rather than removed: a refused detach leaves the image attached, and an entry
        // dropped before the answer came back would be a drive still on the machine that OAN has
        // forgotten — listed in the sidebar, and answered with "OAN did not mount that image"
        // when the user presses Eject again.
        if (Eject(ours) is { } why) return why;

        lock (_mounted) _mounted.Remove(path);
        return null;
    }

    private MountResult Remember(Attachment mount)
    {
        lock (_mounted)
        {
            // Nobody is left to eject what is stored now, so this one goes back immediately.
            if (_disposed)
            {
                Discard(mount);
                return MountResult.Refused(mount.IsoPath, "OAN was closing");
            }

            // Two mounts of one path raced past the check above; keep the first and undo this one.
            if (_mounted.TryGetValue(mount.IsoPath, out var winner))
            {
                Discard(mount);
                return winner.Refreshed();
            }
            _mounted[mount.IsoPath] = mount;
            return mount.AsResult();
        }
    }

    /// <summary>
    /// Closing the handle would detach on its own; the explicit call is what turns a refusal into
    /// a reported reason instead of a silent one. Which is also why the handle survives a refusal:
    /// closing it there would detach the image anyway — see VirtDisk.Attach on the lifetime — and
    /// the caller would have been told the eject did not happen while it quietly did.
    /// </summary>
    private static string? Eject(Attachment mount)
    {
        var rc = VirtDisk.Detach(mount.Handle);
        if (rc is not (0 or VirtDisk.NotAttached)) return VirtDisk.Explain(rc, "could not be ejected");

        mount.Handle.Dispose();
        return null;
    }

    /// <summary>Eject, for an attachment nobody is going to retry: the handle goes whatever the
    /// detach said, because holding it open is what would leave the image on the machine.</summary>
    private static void Discard(Attachment mount)
    {
        Eject(mount);
        mount.Handle.Dispose();
    }

    private string? WaitForLetter(string? physicalPath, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LetterDeadline;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DriveLetters.For(physicalPath) is { } letter) return letter;
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(25);
        }
    }

    private sealed class Attachment(string isoPath, SafeFileHandle handle, string? physicalPath)
    {
        public string IsoPath { get; } = isoPath;
        public SafeFileHandle Handle { get; } = handle;
        public string? PhysicalPath { get; } = physicalPath;
        public string? DriveLetter { get; set; }

        /// <summary>A letter that was not there when the mount returned may be there now.</summary>
        public MountResult Refreshed()
        {
            DriveLetter ??= DriveLetters.For(PhysicalPath);
            return AsResult();
        }

        public MountResult AsResult() =>
            new() { IsoPath = IsoPath, DriveLetter = DriveLetter, PhysicalPath = PhysicalPath };

        public MountedImage AsImage() => new(IsoPath, DriveLetter, PhysicalPath);
    }
}
