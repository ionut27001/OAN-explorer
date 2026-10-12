using System.Windows.Media;
using Oan.App.Platform;
using Oan.Core.Shell;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The queue that serves the rows. What matters here is not the pictures — ThumbnailTests covers
/// those — but that every request is answered: a row told only about successes cannot tell a file
/// with no preview from one still in the queue, and would spin forever waiting for a picture that
/// is never coming.
/// </summary>
[Collection(DiskBound.Name)]
public class ThumbnailPumpTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public void Every_request_is_answered_including_the_ones_with_no_picture()
    {
        using var corpus = new ThumbnailCorpus();
        Thumbnails.Clear();

        var asked = new (string Path, bool IsDirectory)[]
        {
            (corpus.Jpeg, false),
            (corpus.Png, false),
            (corpus.NoExtension, false),
            (corpus.Folder, true),
        };

        var answers = Ask(asked, 256);

        Assert.Equal(asked.Length, answers.Count);
        Assert.Equal(ThumbnailKind.Extracted, answers[corpus.Jpeg].Kind);
        Assert.NotNull(answers[corpus.Jpeg].Image);
        Assert.Equal(ThumbnailKind.None, answers[corpus.NoExtension].Kind);
        Assert.Null(answers[corpus.NoExtension].Image);
    }

    /// <summary>
    /// The count the status bar needs. Two pictures and a file that never had one is not a
    /// partial answer and says nothing; the tally only speaks when something went wrong.
    /// </summary>
    [Fact]
    public void A_folder_is_tallied_rather_than_quietly_drawn_as_icons()
    {
        using var corpus = new ThumbnailCorpus();
        Thumbnails.Clear();

        var answers = Ask(
            [(corpus.Jpeg, false), (corpus.Png, false), (corpus.NoExtension, false)], 256);

        var tally = answers.Values.Aggregate(new ThumbnailTally(), (sum, found) => sum.Plus(found.Kind));

        Assert.Equal(2, tally.Pictures);
        Assert.Equal(1, tally.NoPicture);
        Assert.Equal(0, tally.Unavailable);
        Assert.Null(tally.Note);
    }

    /// <summary>The details view asks for nothing, so the queue answers without touching the shell.</summary>
    [Fact]
    public void The_smallest_view_is_answered_without_a_provider()
    {
        using var corpus = new ThumbnailCorpus();

        var answers = Ask([(corpus.Jpeg, false)], 16);

        Assert.Equal(ThumbnailKind.NotCached, answers[corpus.Jpeg].Kind);
    }

    /// <summary>A badge comes back through the same queue, and a file with none is told so.</summary>
    [Fact]
    public void A_badge_is_answered_too_and_absence_is_an_answer()
    {
        using var corpus = new ThumbnailCorpus();
        var answers = new Dictionary<string, ImageSource?>();
        using var done = new CountdownEvent(2);

        foreach (var path in new[] { corpus.UrlShortcut, corpus.NoExtension })
            ThumbnailPump.LoadBadge(path, 32, badge =>
            {
                lock (answers) answers[path] = badge;
                done.Signal();
            });

        Assert.True(done.Wait(Patience), $"only {answers.Count} of 2 requests came back");
        Assert.NotNull(answers[corpus.UrlShortcut]);
        Assert.Null(answers[corpus.NoExtension]);
    }

    [Fact]
    public void Nothing_has_been_given_up_on_by_these_tests()
    {
        // A provider that hangs cannot be installed from a test, so this is the negative half of
        // that story: the ordinary corpus must not push anything into the table by accident.
        Assert.DoesNotContain(".jpg", ThumbnailPump.GaveUpOn);
    }

    private static Dictionary<string, Thumbnail> Ask((string Path, bool IsDirectory)[] items, int size)
    {
        var answers = new Dictionary<string, Thumbnail>();
        using var done = new CountdownEvent(items.Length);

        foreach (var (path, isDirectory) in items)
            ThumbnailPump.Load(path, isDirectory, size, found =>
            {
                lock (answers) answers[path] = found;
                done.Signal();
            });

        Assert.True(done.Wait(Patience), $"only {answers.Count} of {items.Length} requests came back");
        return answers;
    }
}
