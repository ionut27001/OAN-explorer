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

    /// <summary>
    /// True while the list is showing this place. Matched on the path, and by prefix for a
    /// drive: standing in C:\Users\oance is standing on C:, and an exact match would leave the
    /// sidebar looking as if nothing were selected almost always.
    /// </summary>
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

    /// <summary>
    /// The shell's own icon for this place, which is the whole point: Desktop, Downloads,
    /// Documents, Pictures, Music and Videos each have one in Windows, and a folder someone gave
    /// a custom icon has one too. Drawing the same folder glyph for all of them threw that away.
    /// Requested on first bind and arriving later, like a row's; until then the glyph shows.
    /// </summary>
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
    /// Under a tenth left, which is where Windows itself turns the bar red. A flat fraction
    /// rather than a fixed number of gigabytes, because that is Explorer's rule and a sidebar
    /// disagreeing with the window next to it would just look wrong.
    ///
    /// Said in whole bytes rather than as a fraction: exactly a tenth left is not yet a warning,
    /// and deciding that on doubles puts the boundary on whichever way 0.9 happens to round.
    /// </summary>
    public bool NearlyFull =>
        place.TotalBytes is { } total && total > 0 && place.FreeBytes is { } free && free * 10 < total;

    /// <summary>
    /// Drives: "85.0 GB free of 465 GB". A drive that is not ready knows neither number.
    ///
    /// A nearly full drive says so in words as well as in the red bar. This file already argues,
    /// about the current-place mark, that a signal needs a second half which survives for someone
    /// who cannot tell two hues apart; a bar whose only difference is its colour fails that.
    /// </summary>
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
