namespace Oan.Core.Operations;

public enum OperationKind { Copy, Move, Delete, Rename, CreateFolder }

public sealed record OperationRequest
{
    public required OperationKind Kind { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Target folder for Copy and Move; parent folder for CreateFolder.</summary>
    public string? Destination { get; init; }

    /// <summary>New name for Rename and CreateFolder.</summary>
    public string? NewName { get; init; }

    /// <summary>Delete only. False sends to the Recycle Bin.</summary>
    public bool Permanent { get; init; }
}

public sealed record FailedItem(string Path, int HResult, string Message);

public sealed record OperationResult
{
    public int Succeeded { get; init; }
    public IReadOnlyList<FailedItem> Failed { get; init; } = [];

    /// <summary>The user dismissed a confirmation or progress dialog.</summary>
    public bool Cancelled { get; init; }

    /// <summary>The operation could not start at all. Distinct from per-item failures.</summary>
    public string? Error { get; init; }

    public bool Ok => Error is null && !Cancelled && Failed.Count == 0;

    public static OperationResult Refused(string error) => new() { Error = error };
}

public interface IFileOperations
{
    /// <summary>
    /// Runs the request against the shell. <paramref name="owner"/> parents the progress and
    /// conflict dialogs; pass the main window handle.
    /// </summary>
    Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default);
}
