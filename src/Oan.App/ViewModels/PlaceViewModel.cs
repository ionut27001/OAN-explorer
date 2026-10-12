using System.Windows.Media;
using Oan.App.Platform;
using Oan.Core.FileSystem;
using Oan.Core.Model;

namespace Oan.App.ViewModels;

/// <summary>One sidebar entry: a quick-access folder, which can carry a measured size, or a drive.</summary>
public sealed class PlaceViewModel(Place place) : Observable
{
    private readonly SizeCell _cell = new();
    private ImageSource? _icon;
    private bool _asked;
    private bool _isCurrent;
    private string? _image;

    /// <summary>True while the list is showing this place. By prefix for a drive: anywhere on C: is standing on C:.</summary>
    public bool IsCurrent { get => _isCurrent; private set => Set(ref _isCurrent, value); }

    public void Track(string? currentPath)
    {
        if (string.IsNullOrEmpty(currentPath) || Path.Length == 0)
        {
            IsCurrent = false;
            return;
        }

        IsCurrent = place.Kind == PlaceKind.Drive
            ? currentPath.StartsWith(Path, StringComparison.OrdinalIgnoreCase)
            : string.Equals(
                currentPath.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                Path.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The shell's own icon for this place, arriving later; until then the glyph shows.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (_asked || Path.Length == 0) return _icon;

            _asked = true;
            IconLoader.Load(Path, isDirectory: true, 16, icon =>
            {
                _icon = icon;
                OnChanged(nameof(Icon));
            });
            return null;
        }
    }

    public string Name => place.Name;
    public string Path => place.Path;
    public bool Available => place.Available;
    public bool IsOptical => place.IsOptical;

    /// <summary>The image OAN mounted here, or null — an optical drive with a real disc in it.</summary>
    public string? Image
    {
        get => _image;
        set { if (Set(ref _image, value)) OnChanged(nameof(IsOurs)); }
    }

    /// <summary>Only ours can be ejected; the Core refuses anything it did not attach.</summary>
    public bool IsOurs => _image is not null;
    public double? UsedFraction => place.UsedFraction;

    /// <summary>
    /// Under a tenth left, which is where Explorer turns the bar red. In whole bytes: on doubles the boundary
    /// would land on whichever way 0.9 happens to round.
    /// </summary>
    public bool NearlyFull =>
        place.TotalBytes is { } total && total > 0 && place.FreeBytes is { } free && free * 10 < total;

    /// <summary>"85.0 GB free of 465 GB". A nearly full drive says so in words too, not by the bar's colour alone.</summary>
    public string CapacityText => place.IsOptical
        ? place.TotalBytes is { } bytes ? $"disc image, {Format.Size(bytes)}" : "disc image"
        : NearlyFull
            ? $"{Capacity(place.FreeBytes, place.TotalBytes)} — nearly full"
            : Capacity(place.FreeBytes, place.TotalBytes);

    public string SizeText => _cell.Text;

    public string Tip => _cell.Tip(Path);

    public void Measuring()
    {
        _cell.Measuring();
        Changed();
    }

    public void Measured(FolderSizeResult result)
    {
        _cell.Measured(result);
        Changed();
    }

    public static string Capacity(long? free, long? total) =>
        free is null || total is null ? "—" : $"{Format.Size(free)} free of {Format.Size(total)}";

    private void Changed()
    {
        OnChanged(nameof(SizeText));
        OnChanged(nameof(Tip));
    }
}
