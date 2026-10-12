using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using Oan.Core.Shell;

namespace Oan.App.Platform;

/// <summary>
/// Shell icon lookups off the UI thread: for folders and executables SHGetFileInfo touches the disk.
/// One STA thread drains the queue in order and hands each frozen image back on the dispatcher.
/// </summary>
internal static class IconLoader
{
    private static readonly BlockingCollection<Request> Queue = [];

    static IconLoader()
    {
        var thread = new Thread(Pump) { IsBackground = true, Name = "Shell icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Load(string path, bool isDirectory, int size, Action<ImageSource?> done) =>
        Queue.Add(new Request(path, isDirectory, size, done));

    private static void Pump()
    {
        foreach (var (path, isDirectory, size, done) in Queue.GetConsumingEnumerable())
        {
            var icon = ShellIcons.Get(path, isDirectory, size);
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher is null) done(icon);
            else dispatcher.BeginInvoke(() => done(icon));
        }
    }

    private sealed record Request(string Path, bool IsDirectory, int Size, Action<ImageSource?> Done);
}
