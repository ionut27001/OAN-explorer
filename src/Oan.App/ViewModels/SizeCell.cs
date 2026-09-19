using Oan.Core.FileSystem;
using Oan.Core.Model;

namespace Oan.App.ViewModels;

/// <summary>
/// Unmeasured and Unknown are not the same fact and must not render the same: nothing has been
/// asked yet, versus we walked the folder and could not read it.
/// </summary>
public enum SizeState { Unmeasured, Measuring, Known, Unknown }

/// <summary>
/// One folder's measured size and how far the measurement got.
///
/// Shared by the sidebar and the list, so the "at least" wording lives in one place. That wording
/// is the whole honesty of the feature: measured on this machine, ten of the profile's sixty-five
/// folders cannot be read at all and four more can only be given a floor, so a size column that
/// rendered those as numbers would be lying about a sixth of the rows.
/// </summary>
public sealed class SizeCell
{
    private FolderSizeResult? _result;

    public SizeState State { get; private set; } = SizeState.Unmeasured;

    /// <summary>The number, for sorting. Null until there is one, including while measuring.</summary>
    public long? Bytes => State == SizeState.Known ? _result!.Bytes : null;

    public string Text => State switch
    {
        SizeState.Measuring => "…",
        SizeState.Known => _result!.IsFloor ? $"≥ {Format.Size(_result.Bytes)}" : Format.Size(_result.Bytes),
        SizeState.Unknown => "—",
        _ => "",
    };

    public string Tip(string path) => State switch
    {
        SizeState.Unmeasured => $"{path}\nSize not measured",
        SizeState.Measuring => $"{path}\nMeasuring…",
        SizeState.Known when _result!.IsFloor =>
            $"{path}\nAt least {Format.Size(_result.Bytes)} — {_result.Skipped + _result.NotFollowed} item(s) were not counted",
        SizeState.Unknown => $"{path}\nThe size could not be read",
        _ => path,
    };

    public void Measuring() => State = SizeState.Measuring;

    public void Measured(FolderSizeResult result)
    {
        _result = result;
        State = result.Bytes is null ? SizeState.Unknown : SizeState.Known;
    }

    /// <summary>Back to knowing nothing — what switching the column off means.</summary>
    public void Forget()
    {
        _result = null;
        State = SizeState.Unmeasured;
    }
}
