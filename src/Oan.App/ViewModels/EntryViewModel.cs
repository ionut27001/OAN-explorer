using System.ComponentModel;
using System.Windows.Media;
using Oan.App.Platform;
using Oan.Core.FileSystem;
using Oan.Core.Model;
using Oan.Core.Shell;

namespace Oan.App.ViewModels;

/// <summary>One row. The icon is requested the first time a realized row asks and arrives later.</summary>
/// <param name="measurable">False where a folder's size can never be found, as inside an archive.</param>
public sealed class EntryViewModel(FileEntry entry, int iconSize, bool measurable = true)
    : INotifyPropertyChanged
{
    private readonly SizeCell? _folder = entry.IsDirectory ? Cell(entry, measurable) : null;
    private ImageSource? _icon;
    private ImageSource? _thumbnail;
    private ImageSource? _badge;
    private bool _badgeAsked;
    private bool _asked;
    private string? _kind;
    private int _size = iconSize;

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileEntry Entry { get; } = entry;

    public string Name => Entry.Name;
    /// <summary>A file knows its own size. A folder is blank until something has walked it.</summary>
    public string SizeText => _folder?.Text ?? Format.Size(Entry.Size);

    /// <summary>What to sort on: a file's own length, or a folder's measured total.</summary>
    public long? SortSize => _folder is null ? Entry.Size : _folder.Bytes;

    public string? SizeTip => _folder?.Tip(Entry.FullPath);
    public string ModifiedText => Format.Date(Entry.Modified);
    public string CreatedText => Format.Date(Entry.Created);
    public string AttributesText => Format.Attributes(Entry.Attributes);
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

    /// <summary>The file's own picture if it has one, else its type's icon.</summary>
    public ImageSource? Picture => _thumbnail ?? Icon;

    /// <summary>The overlay badge (shortcut arrow, cloud status) or null. Drawn over Picture, not composited into it.</summary>
    public ImageSource? Badge
    {
        get
        {
            if (_badgeAsked) return _badge;

            _badgeAsked = true;
            var size = _size;
            ThumbnailPump.LoadBadge(Entry.FullPath, size, badge =>
            {
                if (_size != size) return;

                _badge = badge;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Badge)));
            });
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

    /// <summary>The icon on screen stays until the new size arrives, so switching view does not blank every row.</summary>
    public void SetIconSize(int size)
    {
        if (_size == size) return;

        _size = size;

        // A small picture stretched to the new size would be a blur.
        _thumbnail = null;
        _badge = null;
        _badgeAsked = false;

        if (_asked) Request();
    }

    private void Request()
    {
        var size = _size;
        IconLoader.Load(Entry.FullPath, Entry.IsDirectory, size, icon =>
        {
            // A second size change while this one was queued wins. Both callbacks land on the dispatcher.
            if (_size != size) return;

            _icon = icon;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
        });

        // Below this the shell has no picture worth the call. Nor for a folder: Windows composites its thumbnail
        // from the contents, and for anything but pictures that is a blank white page leaning out of the folder.
        if (size < Thumbnails.Smallest || Entry.IsDirectory) return;

        ThumbnailPump.Load(Entry.FullPath, Entry.IsDirectory, size, found =>
        {
            if (_size != size || found.Image is null) return;

            _thumbnail = found.Image;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
        });
    }
}
