using Oan.Core.Operations;

namespace Oan.Core.Interop;

/// <summary>
/// Registered with <see cref="IFileOperation.Advise"/>. The per-item HRESULT that arrives in the
/// Post* callbacks is the only place the shell says which item failed and why — PerformOperations
/// itself returns S_OK even when items were denied.
/// </summary>
internal sealed class ProgressSink : IFileOperationProgressSink
{
    private readonly HashSet<string> _requested;
    private readonly List<FailedItem> _failed = [];

    /// <param name="requested">
    /// The items the caller asked for. The shell walks into folders and reports every descendant,
    /// so only these count towards <see cref="Succeeded"/> — otherwise copying one folder of four
    /// files reports six. An empty list counts every callback, which is what a new item needs: its
    /// path is not known until the shell has created it.
    /// </param>
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

    /// <summary>
    /// Success here is not S_OK: a recycled or renamed item reports
    /// COPYENGINE_S_DONT_PROCESS_CHILDREN. Any non-negative result means the shell handled the
    /// item, except the code it uses for one the user chose to skip.
    /// </summary>
    private void Record(IShellItem? item, int result)
    {
        var path = ShellApi.PathOf(item);

        if (result < 0) _failed.Add(new FailedItem(path, result, ShellApi.Describe(result)));
        else if (result != HResult.UserIgnored && Requested(path)) Succeeded++;
    }

    private bool Requested(string path) => _requested.Count == 0 || _requested.Contains(Normalise(path));

    private static string Normalise(string path) => Path.TrimEndingDirectorySeparator(path);
}
