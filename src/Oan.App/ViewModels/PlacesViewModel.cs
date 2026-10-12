using Oan.App.Platform;
using System.Windows.Input;
using Oan.Core.FileSystem;

namespace Oan.App.ViewModels;

/// <summary>The sidebar: the profile's folders, the machine's drives, and which of them the list is standing in.</summary>
public sealed class PlacesViewModel : Observable, IDisposable
{
    private readonly SizeQueue _sizes;
    private IReadOnlyList<PlaceViewModel> _quick = [];
    private IReadOnlyList<PlaceViewModel> _drives = [];
    private string _freeText = "";

    public PlacesViewModel()
    {
        _sizes = new SizeQueue(Measured);
        MeasureAllCommand = new RelayCommand(MeasureAll, () => Quick.Count > 0);
    }

    /// <summary>The sidebar's own "Sizes" button, which measures every quick-access folder.</summary>
    public ICommand MeasureAllCommand { get; }

    public IReadOnlyList<PlaceViewModel> Quick { get => _quick; private set => Set(ref _quick, value); }
    public IReadOnlyList<PlaceViewModel> Drives { get => _drives; private set => Set(ref _drives, value); }

    /// <summary>Free space on the drive the list is standing on. Empty when it is not known.</summary>
    public string FreeText { get => _freeText; private set => Set(ref _freeText, value); }

    /// <summary>True the first time, so the caller knows to read the quick-access folders.</summary>
    public bool NeedsQuick => _quick.Count == 0;

    public void ShowQuick(IReadOnlyList<Place> places)
    {
        Quick = [.. places.Select(p => new PlaceViewModel(p))];

        // Everything but the profile itself: measured here it is 1.1 million files and 36 seconds, so it waits to be asked.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var place in Quick.Where(p => p.Available && !Same(p.Path, home))) Measure(place);
    }

    public void ShowDrives(IReadOnlyList<Place> drives, Func<string, string?> imageAt)
    {
        var rows = drives.Select(d => new PlaceViewModel(d)).ToArray();
        foreach (var row in rows.Where(r => r.IsOptical)) row.Image = imageAt(row.Path);
        Drives = rows;
    }

    public void ShowFree(long? bytes) =>
        FreeText = bytes is null ? "" : $"{Oan.Core.Model.Format.Size(bytes)} free";

    public void Track(string currentPath)
    {
        foreach (var place in Quick) place.Track(currentPath);

        // Two lit rows read as two selections. The drive speaks only when no pinned place can.
        var pinned = Quick.Any(p => p.IsCurrent);
        foreach (var drive in Drives) drive.Track(pinned ? null : currentPath);
    }

    public void MeasureAll()
    {
        foreach (var place in Quick.Where(p => p.Available)) Measure(place);
    }

    public void Dispose() => _sizes.Dispose();

    private void Measure(PlaceViewModel place)
    {
        place.Measuring();
        _sizes.Add(place.Path);
    }

    private void Measured(FolderSizeResult result)
    {
        if (Quick.FirstOrDefault(p => Same(p.Path, result.Path)) is { } place) place.Measured(result);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
