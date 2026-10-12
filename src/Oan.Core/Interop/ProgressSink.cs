using Oan.Core.Operations;

using Oan.Core.Links;

namespace Oan.Core.Interop;

/// <summary>Registered with <see cref="IFileOperation.Advise"/>. The per-item HRESULT in the Post* callbacks is the only place the shell
/// says which item failed: PerformOperations returns S_OK even when items were denied.</summary>
internal sealed class ProgressSink : IFileOperationProgressSink
{
    private readonly HashSet<string> _requested;
    private readonly List<FailedItem> _failed = [];

    /// <param name="requested">The shell reports every descendant of a folder, so only these count towards <see cref="Succeeded"/>.
    /// An empty list counts every callback, which a new item needs: its path is unknown until created.</param>
    public ProgressSink(IEnumerable<string> requested) =>
        _requested = new HashSet<string>(requested.Select(Normalise), StringComparer.OrdinalIgnoreCase);

    /// <summary>How many of the requested items the shell handled.</summary>
    public int Succeeded { get; private set; }

    /// <summary>Every item that failed, including ones found inside a requested folder.</summary>
    public IReadOnlyList<FailedItem> Failed => _failed;

    public void PostRenameItem(uint flags, IShellItem item, string? newName, int result, IShellItem? created) =>
        Record(item, result);

    public void PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int result, IShellItem? created) =>
        Record(item, result);

    public void PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int result, IShellItem? created) =>
        Record(item, result);

    public void PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) =>
        Record(item, result);

    public void PostNewItem(uint flags, IShellItem destinationFolder, string? newName, string? templateName, uint attributes, int result, IShellItem? created) =>
        Record(created ?? destinationFolder, result);

    public void StartOperations() { }
    public void FinishOperations(int result) { }
    public void PreRenameItem(uint flags, IShellItem item, string? newName) { }
    public void PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) { }
    public void PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) { }
    public void PreDeleteItem(uint flags, IShellItem item) { }
    public void PreNewItem(uint flags, IShellItem destinationFolder, string? newName) { }
    public void UpdateProgress(uint workTotal, uint workSoFar) { }
    public void ResetTimer() { }
    public void PauseTimer() { }
    public void ResumeTimer() { }

    /// <summary>Success is not only S_OK: a recycled or renamed item reports COPYENGINE_S_DONT_PROCESS_CHILDREN.
    /// Any non-negative result counts, except the code for an item the user chose to skip.</summary>
    private void Record(IShellItem? item, int result)
    {
        // Past MAX_PATH the shell answers in 8.3 or \\?\-prefixed form, never the path it was given;
        // without this a copy that worked was reported as zero items succeeded.
        var path = LongPath.FromShell(ShellApi.PathOf(item));

        if (result < 0) _failed.Add(new FailedItem(path, result, ShellApi.Describe(result)));
        else if (result != HResult.UserIgnored && Requested(path)) Succeeded++;
    }

    private bool Requested(string path) => _requested.Count == 0 || _requested.Contains(Normalise(path));

    private static string Normalise(string path) => Path.TrimEndingDirectorySeparator(path);
}
