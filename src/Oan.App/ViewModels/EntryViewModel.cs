using System.ComponentModel;
using System.Windows.Media;
using Oan.App.Platform;
using Oan.Core.Model;

namespace Oan.App.ViewModels;

/// <summary>One row. The icon is requested the first time a realized row asks and arrives later.</summary>
public sealed class EntryViewModel(FileEntry entry) : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _asked;
    private string? _kind;

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
            IconLoader.Load(Entry.FullPath, Entry.IsDirectory, icon =>
            {
                _icon = icon;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            });

            return null;
        }
    }
}
