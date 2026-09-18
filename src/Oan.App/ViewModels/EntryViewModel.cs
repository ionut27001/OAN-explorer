using System.ComponentModel;
using System.Windows.Media;
using Oan.App.Platform;
using Oan.Core.Model;

namespace Oan.App.ViewModels;

/// <summary>One row. The icon is requested the first time a realized row asks and arrives later.</summary>
public sealed class EntryViewModel(FileEntry entry, int iconSize) : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _asked;
    private string? _kind;
    private int _size = iconSize;

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileEntry Entry { get; } = entry;

    public string Name => Entry.Name;
    public string SizeText => Format.Size(Entry.Size);
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
