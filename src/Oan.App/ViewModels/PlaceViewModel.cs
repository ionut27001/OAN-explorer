using Oan.Core.FileSystem;
using Oan.Core.Model;

namespace Oan.App.ViewModels;

/// <summary>
/// Unmeasured and Unknown are not the same fact and must not render the same: nothing has been
/// asked yet, versus we walked the folder and could not read it.
/// </summary>
public enum SizeState { Unmeasured, Measuring, Known, Unknown }

/// <summary>One sidebar entry: a quick-access folder, which can carry a measured size, or a drive.</summary>
public sealed class PlaceViewModel(Place place) : Observable
{
    private FolderSizeResult? _result;
    private SizeState _state = SizeState.Unmeasured;
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

    /// <summary>Drives: "85.0 GB free of 465 GB". A drive that is not ready knows neither number.</summary>
    public string CapacityText => place.IsOptical
        ? place.TotalBytes is { } bytes ? $"disc image, {Format.Size(bytes)}" : "disc image"
        : Capacity(place.FreeBytes, place.TotalBytes);

    public string SizeText => _state switch
    {
        SizeState.Measuring => "…",
        SizeState.Known => _result!.IsFloor ? $"≥ {Format.Size(_result.Bytes)}" : Format.Size(_result.Bytes),
        SizeState.Unknown => "—",
        _ => "",
    };

    public string Tip => _state switch
    {
        SizeState.Unmeasured => $"{Path}\nSize not measured",
        SizeState.Measuring => $"{Path}\nMeasuring…",
        SizeState.Known when _result!.IsFloor =>
            $"{Path}\nAt least {Format.Size(_result.Bytes)} — {_result.Skipped + _result.NotFollowed} item(s) were not counted",
        SizeState.Unknown => $"{Path}\nThe size could not be read",
        _ => Path,
    };

    public void Measuring()
    {
        _state = SizeState.Measuring;
        Changed();
    }

    public void Measured(FolderSizeResult result)
    {
        _result = result;
        _state = result.Bytes is null ? SizeState.Unknown : SizeState.Known;
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
