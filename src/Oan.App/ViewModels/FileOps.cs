using System.IO;
using Oan.App.Platform;
using Oan.Core.Operations;
using Oan.Core.Shell;

namespace Oan.App.ViewModels;

/// <summary>
/// What an operation needs to know about the window it is running in. An interface rather than a
/// handful of delegates, because every member of it already exists on the window under this name
/// — the window implements it by declaring that it does.
/// </summary>
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

    Action<IReadOnlyList<EntryViewModel>>? Reselect { get; }
    string? Notice { set; }

    Task ReloadAsync();
    void RefreshClipboardState();
}

/// <summary>
/// Copy, cut, paste, rename, delete and create — everything that changes the disk.
///
/// Out of MainViewModel because it is a concern of its own, not because of its size: it is the
/// only part of the window that can lose someone's files, and it reads better as one piece than
/// interleaved with navigation and filtering. The window keeps the commands, so what the markup
/// binds to is unchanged; only the bodies moved.
/// </summary>
public sealed class FileOps(IFileOperations ops, IFileOpsHost host)
{
    public void PutOnClipboard(bool cut)
    {
        var paths = Selected();
        if (paths.Count == 0) return;

        host.Notice = ClipboardFiles.Put(paths, cut) ? null : "The clipboard is in use by another program.";
        host.RefreshClipboardState();
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

    public Task DropAsync(IReadOnlyList<string> paths, bool move)
    {
        if (!host.CanWriteHere)
        {
            host.Notice = "This folder cannot be written to.";
            return Task.CompletedTask;
        }

        var incoming = paths
            .Where(p => !string.Equals(Path.GetDirectoryName(p), host.CurrentPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // A drag that ends where it began is a cancelled gesture, not a failure. This is the one
        // no-op the window is allowed to pass over in silence: the user asked for nothing, and an
        // orange warning for a slipped mouse reads as something having gone wrong.
        if (incoming.Length == 0) return Task.CompletedTask;

        return RunAsync(new OperationRequest
        {
            Kind = move ? OperationKind.Move : OperationKind.Copy,
            Sources = incoming,
            Destination = host.CurrentPath,
        });
    }

    public Task NewAsync(NewItem item) =>
        item.Template is null ? NewFolderAsync() : NewFileAsync(item.Template);

    /// <summary>
    /// Explorer's flow: the file appears under the name its type advertises and the name is the
    /// next thing you are asked for, rather than before there is anything to name.
    /// </summary>
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
