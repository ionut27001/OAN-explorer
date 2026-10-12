using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Oan.Core.Shell;

namespace Oan.App.Platform;

/// <summary>
/// Thumbnails and overlay badges off the UI thread: one worker draining one queue in order. Extraction runs
/// a third-party IThumbnailProvider, so every shell call is on a <see cref="StaThread"/> under a deadline.
/// </summary>
internal static class ThumbnailPump
{
    /// <summary>
    /// How long one shell call gets: the bound on how long one item may hold up every row behind it.
    /// The slowest real extraction measured in docs/thumbnails.md is 47 ms.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(3000);

    /// <summary>Kinds of file whose provider hung once in this run; they are not sent to it again. Forgotten between runs.</summary>
    private static readonly ConcurrentDictionary<string, bool> Stuck = new(StringComparer.OrdinalIgnoreCase);

    private static readonly BlockingCollection<Request> Queue = [];

    /// <summary>Only ever touched by the queue thread below, which is why it needs no lock.</summary>
    private static StaThread _worker = new("Shell thumbnails");

    static ThumbnailPump()
    {
        var thread = new Thread(Pump) { IsBackground = true, Name = "Shell thumbnail queue" };
        thread.Start();
    }

    /// <summary>Asks for a picture. The answer always arrives, including when it is "there is none".</summary>
    public static void Load(string path, bool isDirectory, int size, Action<Thumbnail> done) =>
        Queue.Add(new Request(path, isDirectory, size, done, null));

    /// <summary>
    /// The overlay badge for an item, or null. Same queue and deadline: for a shortcut to a dead share
    /// the shell call blocks for the network timeout.
    /// </summary>
    public static void LoadBadge(string path, int size, Action<ImageSource?> done) =>
        Queue.Add(new Request(path, false, size, null, done));

    /// <summary>The kinds given up on, for a test and for anyone wondering why a folder is plain.</summary>
    public static IReadOnlyCollection<string> GaveUpOn => Stuck.Keys.ToArray();

    private static void Pump()
    {
        foreach (var request in Queue.GetConsumingEnumerable())
        {
            if (request.Picture is { } wantsPicture)
            {
                var found = Answer(request);
                Hand(() => wantsPicture(found));
            }
            else if (request.Badge is { } wantsBadge)
            {
                var badge = Badge(request);
                Hand(() => wantsBadge(badge));
            }
        }
    }

    /// <summary>SIIGBF_INCACHEONLY first: it reads Windows' thumbnail cache and runs no provider. Only a miss is extracted.</summary>
    private static Thumbnail Answer(Request request)
    {
        var kind = Kind(request.Path, request.IsDirectory);

        var cached = Thumbnail.NotCached;
        if (!OnWorker(() => cached = Thumbnails.Get(request.Path, request.Size, extract: false)))
            return GiveUp(kind);

        if (cached.Kind != ThumbnailKind.NotCached) return cached;
        if (Stuck.ContainsKey(kind)) return new(null, ThumbnailKind.Unavailable);

        var found = Thumbnail.NotCached;
        return OnWorker(() => found = Thumbnails.Get(request.Path, request.Size, extract: true))
            ? found
            : GiveUp(kind);
    }

    private static ImageSource? Badge(Request request)
    {
        ImageSource? badge = null;
        if (!OnWorker(() => badge = ShellOverlays.For(request.Path, request.Size)))
            GiveUp(Kind(request.Path, isDirectory: false));

        return badge;
    }

    /// <summary>
    /// abandon: false, so a slow job finishes in its own time and costs only its own picture. A worker that
    /// never came back is replaced: a thread cannot be taken back from a DLL that will not return.
    /// </summary>
    private static bool OnWorker(Action job)
    {
        if (_worker.Lost) _worker = new StaThread("Shell thumbnails");
        return _worker.Run(job, Deadline, abandon: false);
    }

    private static Thumbnail GiveUp(string kind)
    {
        Stuck[kind] = true;
        return new(null, ThumbnailKind.Unavailable);
    }

    private static void Hand(Action done)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null) done();
        else dispatcher.BeginInvoke(done);
    }

    /// <summary>
    /// What a hang is remembered against: the extension, which is what a provider registers for. Folders get
    /// their own bucket, or a folder called archive.iso would cost real .iso files their previews.
    /// </summary>
    private static string Kind(string path, bool isDirectory) =>
        isDirectory ? "folders"
        : Path.GetExtension(path) is { Length: > 1 } extension ? extension.ToLowerInvariant()
        : "files with no extension";

    private sealed record Request(string Path, bool IsDirectory, int Size,
        Action<Thumbnail>? Picture, Action<ImageSource?>? Badge);
}
