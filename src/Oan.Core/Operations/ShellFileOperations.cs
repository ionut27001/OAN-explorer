using System.IO;
using System.Runtime.CompilerServices;
using Oan.Core.Interop;

[assembly: InternalsVisibleTo("Oan.Tests")]

namespace Oan.Core.Operations;

/// <summary>
/// Runs copy, move, delete, rename and new-folder through the shell's IFileOperation — the same
/// engine Explorer uses, so deletes land in the Recycle Bin, undo joins Explorer's Ctrl+Z stack,
/// and the progress, conflict and in-use dialogs are the ones the user already knows.
/// </summary>
public sealed class ShellFileOperations : IFileOperations
{
    /// <remarks>
    /// <paramref name="ct"/> is honoured only before the operation starts. IFileOperation offers no
    /// external cancel, so once the shell owns the work the user cancels at its own progress dialog.
    /// </remarks>
    public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default)
    {
        var refusal = Validate(request);
        if (refusal is not null) return Task.FromResult(OperationResult.Refused(refusal));
        if (ct.IsCancellationRequested) return Task.FromResult(new OperationResult { Cancelled = true });

        var completion = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // PerformOperations blocks and puts up windows, so it needs an STA thread of its own.
        // Nothing may escape it: an unhandled exception here would kill the process and leave the
        // caller awaiting a task that is never completed.
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Execute(request, owner)); }
            catch (Exception ex) { completion.TrySetResult(new OperationResult { Error = ex.Message }); }
        })
        {
            IsBackground = true,
            Name = "Oan file operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    internal static string? Validate(OperationRequest request)
    {
        if (request.Kind == OperationKind.CreateFolder)
        {
            if (request.Sources.Count != 0) return "Creating a folder takes no source items.";
        }
        else if (request.Sources.Count == 0)
        {
            return "Nothing was selected.";
        }

        if (request.Kind == OperationKind.Rename && request.Sources.Count != 1)
            return "Rename works on one item at a time.";

        switch (request.Kind)
        {
            case OperationKind.Copy when string.IsNullOrWhiteSpace(request.Destination):
                return "Copy needs a destination folder.";
            case OperationKind.Move when string.IsNullOrWhiteSpace(request.Destination):
                return "Move needs a destination folder.";
            case OperationKind.CreateFolder when string.IsNullOrWhiteSpace(request.Destination):
                return "Creating a folder needs a parent folder.";
        }

        if (request.Kind is OperationKind.Rename or OperationKind.CreateFolder)
        {
            if (string.IsNullOrWhiteSpace(request.NewName)) return "A new name is required.";

            var badName = CheckName(request.NewName);
            if (badName is not null) return badName;
        }

        foreach (var source in request.Sources)
        {
            if (string.IsNullOrWhiteSpace(source)) return "An item without a path was given.";
            if (!Path.IsPathFullyQualified(source)) return $"Not a full path: {source}";
        }

        if (request.Destination is not null && !Path.IsPathFullyQualified(request.Destination))
            return $"Not a full path: {request.Destination}";

        return null;
    }

    /// <summary>
    /// A name the file system cannot store has to be refused here: NewItem strips the offending
    /// characters and reports success, so "Notes: Q4" would quietly become a folder called " Q4".
    /// </summary>
    internal static string? CheckName(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return """A name cannot contain any of these characters: \ / : * ? " < > |""";

        if (name.EndsWith(' ') || name.EndsWith('.'))
            return "A name cannot end with a space or a full stop.";

        var dot = name.IndexOf('.');
        var bare = dot < 0 ? name : name[..dot];
        if (ReservedNames.Contains(bare, StringComparer.OrdinalIgnoreCase))
            return $"\"{bare}\" is a name Windows keeps for hardware devices.";

        return null;
    }

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// PerformOperations returns S_OK when the user cancelled and when individual items failed,
    /// so the HRESULT alone never means success: <paramref name="aborted"/> comes from
    /// GetAnyOperationsAborted and <paramref name="failed"/> from the progress sink.
    /// </summary>
    internal static OperationResult Compose(int hr, bool aborted, int succeeded, IReadOnlyList<FailedItem> failed)
    {
        if (hr is HResult.Cancelled or HResult.Abort)
            return new OperationResult { Succeeded = succeeded, Failed = failed, Cancelled = true };

        if (hr < 0)
            return new OperationResult { Succeeded = succeeded, Failed = failed, Error = ShellApi.Describe(hr) };

        return new OperationResult { Succeeded = succeeded, Failed = failed, Cancelled = aborted };
    }

    private static OperationResult Execute(OperationRequest request, nint owner)
    {
        var sink = new ProgressSink(request.Sources);
        IFileOperation? operation = null;
        uint cookie = 0;

        try
        {
            operation = ShellApi.CreateFileOperation();
            operation.Advise(sink, out cookie);
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(FlagsFor(request));
            Queue(operation, request);

            var hr = operation.PerformOperations();

            // A failed call must not read as "nothing was aborted": say something was unclear.
            if (operation.GetAnyOperationsAborted(out var aborted) < 0) aborted = true;

            return Compose(hr, aborted, sink.Succeeded, sink.Failed);
        }
        catch (Exception ex)
        {
            return new OperationResult { Succeeded = sink.Succeeded, Failed = sink.Failed, Error = ex.Message };
        }
        finally
        {
            if (operation is not null && cookie != 0) _ = operation.Unadvise(cookie);
        }
    }

    private static void Queue(IFileOperation operation, OperationRequest request)
    {
        switch (request.Kind)
        {
            case OperationKind.Copy:
            {
                var destination = ShellApi.Item(request.Destination!);
                foreach (var source in request.Sources)
                    operation.CopyItem(ShellApi.Item(source), destination, null, null);
                break;
            }
            case OperationKind.Move:
            {
                var destination = ShellApi.Item(request.Destination!);
                foreach (var source in request.Sources)
                    operation.MoveItem(ShellApi.Item(source), destination, null, null);
                break;
            }
            case OperationKind.Delete:
                foreach (var source in request.Sources) operation.DeleteItem(ShellApi.Item(source), null);
                break;
            case OperationKind.Rename:
                operation.RenameItem(ShellApi.Item(request.Sources[0]), request.NewName!, null);
                break;
            case OperationKind.CreateFolder:
                operation.NewItem(ShellApi.Item(request.Destination!), (uint)FileAttributes.Directory,
                    request.NewName!, null, null);
                break;
        }
    }

    private static uint FlagsFor(OperationRequest request)
    {
        const uint common = FileOperationFlags.NoConfirmMkDir
            | FileOperationFlags.ShowElevationPrompt
            | FileOperationFlags.AddUndoRecord;

        if (request.Kind != OperationKind.Delete)
            return common | FileOperationFlags.AllowUndo;

        // A permanent delete deliberately leaves FOF_NOCONFIRMATION unset: the shell's own
        // confirmation runs in another process and nothing here can spoof or suppress it.
        // WantNukeWarning covers the other half — an item the Recycle Bin cannot take is
        // warned about rather than silently destroyed.
        return request.Permanent
            ? common
            : common | FileOperationFlags.AllowUndo | FileOperationFlags.RecycleOnDelete | FileOperationFlags.WantNukeWarning;
    }
}
