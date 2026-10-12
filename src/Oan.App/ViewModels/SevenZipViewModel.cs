using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Oan.Core.Archives;

namespace Oan.App.ViewModels;

/// <summary>
/// Whether the user's 7-Zip was found, and the way to get one when it was not. OAN carries none of it:
/// winget fetches the official package, or the download page opens.
/// </summary>
public sealed class SevenZipViewModel : Observable
{
    private const string DownloadPage = "https://www.7-zip.org/download.html";

    private bool _busy;
    private string? _note;

    public SevenZipViewModel() =>
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => Missing && !_busy);

    public ICommand InstallCommand { get; }

    public bool Missing => ArchiveReader.SevenZipPath is null;

    public string Status => ArchiveReader.SevenZipPath is { } path
        ? $"7-Zip found: {path}"
        : "7-Zip is not installed. Zip, 7z, rar and tar still open; passwords, split archives, cab, wim, zst and the rest need it.";

    public string? Note
    {
        get => _note;
        private set { if (Set(ref _note, value)) OnChanged(nameof(HasNote)); }
    }

    public bool HasNote => _note is not null;

    private async Task InstallAsync()
    {
        _busy = true;
        Note = "Installing — winget shows its progress in its own window.";
        try
        {
            using var winget = Process.Start(new ProcessStartInfo("winget", "install --id 7zip.7zip --exact --source winget")
            {
                UseShellExecute = true,
            });
            if (winget is not null) await winget.WaitForExitAsync();

            ArchiveReader.FindSevenZipAgain();
            Note = Missing ? "7-Zip was not installed. Nothing has changed." : null;
        }
        catch (Win32Exception)
        {
            // No winget on this machine.
            Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true });
            Note = "This Windows has no winget, so the 7-Zip download page was opened instead. OAN finds it once it is installed.";
        }
        finally
        {
            _busy = false;
            OnChanged(nameof(Missing));
            OnChanged(nameof(Status));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
