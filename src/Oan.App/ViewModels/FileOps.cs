using System.IO;
using Oan.App.Platform;
using Oan.Core.Operations;
using Oan.Core.Shell;

namespace Oan.App.ViewModels;

/// <summary>What an operation needs to know about the window it is running in.</summary>
public interface IFileOpsHost
{
    string CurrentPath { get; }
    nint OwnerHandle { get; }
    IReadOnlyList<EntryViewModel> Rows { get; }
    IReadOnlyList<EntryViewModel> Selection { get; }

    /// <summary>False where nothing may be created or changed — inside an archive, for one.</summary>
    bool CanWriteHere { get; }

    /// <summary>Asks for a name and returns it, or null if the window is not showing yet.</summary>
    Func<string, string, string?>? AskName { get; }

    /// <summary>Asks for the password of the named archive; null when the user would rather not.</summary>
    Func<string, string?>? AskPassword { get; }

    Action<IReadOnlyList<EntryViewModel>>? Reselect { get; }
    string? Notice { set; }

    /// <summary>Something that went right. Setting <see cref="Notice"/> clears it.</summary>
    string? Info { set; }

    Task ReloadAsync();
    void RefreshClipboardState();
}

/// <summary>Copy, cut, paste, rename, delete and create: everything that changes the disk.</summary>
public sealed class FileOps(IFileOperations ops, IFileOpsHost host)
{
    public void PutOnClipboard(bool cut)
    {
        var paths = Selected();
        if (paths.Count == 0) return;

        host.Notice = ClipboardFiles.Put(paths, cut) ? null : "The clipboard is in use by another program.";
        host.RefreshClipboardState();
    }

    /// <summary>Explorer's "Copy as path": quoted, one to a line, ready for a command line.</summary>
    public void CopyPaths()
    {
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Selected().Select(p => $"\"{p}\"")));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            host.Notice = "The clipboard is in use by another program.";
        }
    }

    public Task PasteAsync()
    {
        var (paths, cut) = ClipboardFiles.Get();
        if (paths.Count == 0) return Task.CompletedTask;

        return RunAsync(new OperationRequest
        {
            Kind = cut ? OperationKind.Move : OperationKind.Copy,
            Sources = paths,
            Destination = host.CurrentPath,
        }, clearClipboard: cut);
    }

    public Task DeleteAsync(bool permanent) =>
        RunAsync(new OperationRequest
        {
            Kind = OperationKind.Delete,
            Sources = Selected(),
            Permanent = permanent,
        });

    public Task RenameAsync()
    {
        var row = host.Selection[0];
        var name = host.AskName?.Invoke("Rename", row.Name);
        if (string.IsNullOrWhiteSpace(name) || name == row.Name) return Task.CompletedTask;

        return RunAsync(new OperationRequest
        {
            Kind = OperationKind.Rename,
            Sources = [row.Entry.FullPath],
            NewName = name,
        });
    }

    public Task NewFolderAsync()
    {
        var name = host.AskName?.Invoke("New folder", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;

        return RunAsync(new OperationRequest
        {
            Kind = OperationKind.CreateFolder,
            Destination = host.CurrentPath,
            NewName = name,
        });
    }

    /// <param name="into">The folder the drop was aimed at, or null for the folder on screen.</param>
    public Task DropAsync(IReadOnlyList<string> paths, bool move, string? into = null)
    {
        if (!host.CanWriteHere)
        {
            host.Notice = "This folder cannot be written to.";
            return Task.CompletedTask;
        }

        var destination = into ?? host.CurrentPath;

        // Compared against where it is going, not where we are standing: dropping a row onto a folder in the
        // same listing is the commonest drag there is.
        var incoming = paths.Where(p => !Same(p, destination) && !Same(Path.GetDirectoryName(p), destination)).ToArray();

        // A drag that ends where it began is a cancelled gesture, and the one no-op passed over in silence.
        if (incoming.Length == 0) return Task.CompletedTask;

        return RunAsync(new OperationRequest
        {
            Kind = move ? OperationKind.Move : OperationKind.Copy,
            Sources = incoming,
            Destination = destination,
        });
    }

    private static bool Same(string? one, string? other) =>
        string.Equals(one, other, StringComparison.OrdinalIgnoreCase);

    public Task NewAsync(NewItem item) =>
        item.Template is null ? NewFolderAsync() : NewFileAsync(item.Template);

    /// <summary>Explorer's flow: the file appears under its type's default name, then the name is asked for.</summary>
    private async Task NewFileAsync(NewFileTemplate template)
    {
        string path;
        try
        {
            path = ShellNew.Create(host.CurrentPath, template);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            host.Notice = ex.Message;
            return;
        }

        await host.ReloadAsync();

        var row = host.Rows.FirstOrDefault(
            r => string.Equals(r.Entry.FullPath, path, StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            host.Notice = $"\"{Path.GetFileName(path)}\" was created — the filter is hiding it.";
            return;
        }

        host.Reselect?.Invoke([row]);
        if (host.Selection.Count == 1) await RenameAsync();
    }

    private async Task RunAsync(OperationRequest request, bool clearClipboard = false)
    {
        var result = await ops.RunAsync(request, host.OwnerHandle);

        var notice =
            result.Error is not null ? result.Error :
            result.Failed.Count > 0 ? $"{result.Failed.Count} item(s) failed: {result.Failed[0].Message}" :
            result.Cancelled ? Cancellation(result.Succeeded) : null;

        if (clearClipboard && result.Ok)
        {
            ClipboardFiles.Clear();
            host.RefreshClipboardState();
        }

        // Message last, so the outcome of the operation is what the user is left looking at.
        await host.ReloadAsync();
        host.Notice = notice;
    }

    private static string Cancellation(int succeeded) =>
        succeeded > 0 ? $"Cancelled after {succeeded} item(s)." : "Cancelled.";

    private IReadOnlyList<string> Selected() => [.. host.Selection.Select(r => r.Entry.FullPath)];
}
