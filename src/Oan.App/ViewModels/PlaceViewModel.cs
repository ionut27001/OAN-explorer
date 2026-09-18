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

    public string Name => place.Name;
    public string Path => place.Path;
    public bool Available => place.Available;
    public double? UsedFraction => place.UsedFraction;

    /// <summary>Drives: "85.0 GB free of 465 GB". A drive that is not ready knows neither number.</summary>
    public string CapacityText => Capacity(place.FreeBytes, place.TotalBytes);

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
