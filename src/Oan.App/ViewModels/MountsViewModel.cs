using System.IO;
using System.Windows.Input;
using Oan.Core.Mount;

namespace Oan.App.ViewModels;

/// <summary>One image OAN has attached. The letter is null until Windows gives the volume one.</summary>
public sealed class MountViewModel(string isoPath, string? driveLetter)
{
    public string IsoPath { get; } = isoPath;
    public string Name { get; } = Path.GetFileName(isoPath);
    public string? DriveLetter { get; } = driveLetter;

    public bool HasLetter => DriveLetter is not null;
    public string LetterText => DriveLetter ?? "—";

    /// <summary>Empty when there is no letter: there is nothing to open yet.</summary>
    public string Root => DriveLetter is null ? "" : DriveLetter + Path.DirectorySeparatorChar;

    public string Tip => DriveLetter is null
        ? $"{IsoPath}\nMounted — Windows has not given it a drive letter"
        : $"{IsoPath}\nMounted as {DriveLetter}";
}

/// <summary>
/// The images OAN has mounted, attached through the VirtDisk API and never through a shell handler,
/// which can hang. Only images this object mounted are listed: those are the only ones the Core will detach.
/// </summary>
public sealed class MountsViewModel : Observable, IDisposable
{
    private static readonly string[] Extensions = [".iso", ".img"];

    private readonly IsoMount? _owned;
    private readonly Func<string, Task<MountResult>> _mount;
    private readonly Func<string, Task<string?>> _unmount;

    private Action<string>? _go;
    private Action<string>? _report;
    private Func<string>? _here;

    /// <summary>Paths with a mount in flight. The Core only learns a path after the attach, up to five seconds later.</summary>
    private readonly HashSet<string> _mounting = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<MountViewModel> _images = [];
    private string? _selected;

    public MountsViewModel() : this(new IsoMount()) { }

    private MountsViewModel(IsoMount iso) : this(p => iso.MountAsync(p), p => iso.UnmountAsync(p)) => _owned = iso;

    /// <summary>The two calls that touch virtdisk.dll are what the tests replace.</summary>
    internal MountsViewModel(Func<string, Task<MountResult>> mount, Func<string, Task<string?>> unmount)
    {
        _mount = mount;
        _unmount = unmount;

        MountCommand = new RelayCommand(() => Guarded(ToggleAsync), () => _selected is not null);
        UnmountCommand = new RelayCommand(p => Guarded(() => UnmountAsync((string)p!)), p => p is string);
    }

    /// <summary>Where a mounted drive is opened, where a message is put, and where the list is standing.</summary>
    public void Bind(Action<string> go, Action<string> report, Func<string> here) =>
        (_go, _report, _here) = (go, report, here);

    public ICommand MountCommand { get; }
    public ICommand UnmountCommand { get; }

    public IReadOnlyList<MountViewModel> Images
    {
        get => _images;
        private set
        {
            _images = value;
            OnChanged();
            OnChanged(nameof(Any));
            OnChanged(nameof(MountHeader));
        }
    }

    /// <summary>The sidebar section exists only while something is mounted.</summary>
    public bool Any => _images.Count > 0;

    public bool ShowMount => _selected is not null;

    public string MountHeader => _selected is not null && Find(_selected) is not null ? "Unmount" : "Mount";

    /// <summary>The image OAN attached at this drive root, or null. Lets the drive's own row carry Eject.</summary>
    public MountViewModel? At(string driveRoot) =>
        _images.FirstOrDefault(i => i.Root.Length > 0 && Same(i.Root, driveRoot));

    /// <summary>True only for images OAN attached: those are the only ones it will detach.</summary>
    public bool IsMounted(string isoPath) => _images.Any(i => Same(i.IsoPath, isoPath));

    public static bool IsImage(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The list's selection. Mount is offered for exactly one image and nothing else.</summary>
    public void Selected(IReadOnlyList<string> paths)
    {
        _selected = paths.Count == 1 && IsImage(paths[0]) ? paths[0] : null;

        OnChanged(nameof(ShowMount));
        OnChanged(nameof(MountHeader));
    }

    /// <summary>What the context menu item does; its header says which of the two it is.</summary>
    public Task ToggleAsync() =>
        _selected is not { } path ? Task.CompletedTask
        : Find(path) is null ? OpenAsync(path)
        : UnmountAsync(path);

    /// <summary>
    /// Attaches the image if it is not ours already, then opens its drive. An image held without a letter is
    /// asked for again: that is the only way a letter that arrived after the five-second wait is picked up.
    /// </summary>
    public async Task OpenAsync(string isoPath)
    {
        if (Find(isoPath) is { HasLetter: true } already)
        {
            Show(already);
            return;
        }

        // Two double-clicks would both pass every guard above, and virtdisk would attach the image twice.
        if (!_mounting.Add(isoPath)) return;

        var name = Path.GetFileName(isoPath);
        if (Find(isoPath) is null) _report?.Invoke($"Mounting {name}…");

        MountResult result;
        try
        {
            result = await _mount(isoPath);
        }
        finally
        {
            _mounting.Remove(isoPath);
        }

        if (!result.Ok)
        {
            _report?.Invoke($"{name} could not be mounted — {result.Error}");
            return;
        }

        var image = new MountViewModel(result.IsoPath, result.DriveLetter);
        Images = [.. _images.Where(i => !Same(i.IsoPath, image.IsoPath)), image];
        Show(image);
    }

    public async Task UnmountAsync(string isoPath)
    {
        if (Find(isoPath) is not { } image) return;

        if (await _unmount(isoPath) is { } why)
        {
            _report?.Invoke($"{image.Name} — {why}");
            return;
        }

        Images = [.. _images.Where(i => i != image)];
        Leave(image);
    }

    public void Dispose() => _owned?.Dispose();

    /// <summary>Goes to the drive. A mount with no letter is still a mount, and is said out loud.</summary>
    private void Show(MountViewModel image)
    {
        if (image.HasLetter) _go?.Invoke(image.Root);
        else _report?.Invoke($"{image.Name} is mounted — Windows has not given it a drive letter.");
    }

    /// <summary>Standing inside a drive that was just ejected, go to the folder the image came from instead.</summary>
    private void Leave(MountViewModel image)
    {
        var here = _here?.Invoke() ?? "";
        if (image.HasLetter && here.StartsWith(image.Root, StringComparison.OrdinalIgnoreCase))
            _go?.Invoke(Path.GetDirectoryName(image.IsoPath) ?? image.Root);
        else
            _report?.Invoke($"{image.Name} is unmounted.");
    }

    /// <summary>The menu item reaches these commands directly, past the window's guard, so a throw becomes a notice here.</summary>
    private async void Guarded(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _report?.Invoke(ex.Message);
        }
    }

    private MountViewModel? Find(string isoPath) => _images.FirstOrDefault(i => Same(i.IsoPath, isoPath));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
