using System.ComponentModel;
using System.Windows.Media;
using Oan.App.Platform;
using Oan.Core.FileSystem;
using Oan.Core.Model;

namespace Oan.App.ViewModels;

/// <summary>One row. The icon is requested the first time a realized row asks and arrives later.</summary>
/// <param name="measurable">False where a folder's size can never be found, whatever anyone
/// asks: inside an archive there is nothing to walk. That renders as "could not be read", not as
/// the blank that means "nobody has asked yet".</param>
public sealed class EntryViewModel(FileEntry entry, int iconSize, bool measurable = true)
    : INotifyPropertyChanged
{
    private readonly SizeCell? _folder = entry.IsDirectory ? Cell(entry, measurable) : null;
    private ImageSource? _icon;
    private bool _asked;
    private string? _kind;
    private int _size = iconSize;

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileEntry Entry { get; } = entry;

    public string Name => Entry.Name;
    /// <summary>
    /// A file knows its own size. A folder only knows one once something has walked it, and blank
    /// until then — an unmeasured folder is not a folder of no size, and neither is one we could
    /// not read, which is why those two look different.
    /// </summary>
    public string SizeText => _folder?.Text ?? Format.Size(Entry.Size);

    /// <summary>What to sort on: a file's own length, or a folder's measured total.</summary>
    public long? SortSize => _folder is null ? Entry.Size : _folder.Bytes;

    public string? SizeTip => _folder?.Tip(Entry.FullPath);
    public string ModifiedText => Format.Date(Entry.Modified);
    public bool IsHidden => Entry.IsHidden;

    /// <summary>Cached: sorting by kind compares this across every pair.</summary>
    public string KindText => _kind ??= Format.Kind(Entry);

    public ImageSource? Icon
    {
        get
        {
            if (_asked) return _icon;

            _asked = true;
            Request();
            return null;
        }
    }

    public void Measuring()
    {
        _folder?.Measuring();
        SizeChanged();
    }

    public void Measured(FolderSizeResult result)
    {
        _folder?.Measured(result);
        SizeChanged();
    }

    /// <summary>Back to blank: the column was switched off, or we navigated away and back.</summary>
    public void ForgetSize()
    {
        _folder?.Forget();
        SizeChanged();
    }

    private static SizeCell Cell(FileEntry entry, bool measurable)
    {
        var cell = new SizeCell();
        if (!measurable) cell.Measured(FolderSizeResult.Unknown(entry.FullPath));
        return cell;
    }

    private void SizeChanged()
    {
        if (_folder is null) return;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeTip)));
    }

    /// <summary>
    /// The icon already on screen stays until the new size arrives, so switching view does not
    /// blank every row. A row nobody has looked at yet simply asks at the new size when it does.
    /// </summary>
    public void SetIconSize(int size)
    {
        if (_size == size) return;

        _size = size;
        if (_asked) Request();
    }

    private void Request()
    {
        var size = _size;
        IconLoader.Load(Entry.FullPath, Entry.IsDirectory, size, icon =>
        {
            // A second size change while this one was in the queue wins; both callbacks land on
            // the dispatcher, so this cannot race with the field it reads.
            if (_size != size) return;

            _icon = icon;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        });
    }
}
