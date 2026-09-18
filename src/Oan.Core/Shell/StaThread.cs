namespace Oan.Core.Shell;

/// <summary>
/// One thread, for work that runs code we did not write. Every call gives up at a deadline; what
/// that costs is the caller's to choose, because the two kinds of work here are not alike.
///
/// It is an STA because COM shell extensions require one, and it is a single thread for the whole
/// right-click because a COM object lives in the apartment that created it: building the menu on a
/// thread that then exited would leave the open menu holding a dead pointer.
/// </summary>
/// <remarks>Public because the icon pump needs the same bound: it too calls handlers we did not
/// write, one at a time, on one thread.</remarks>
public sealed class StaThread : IDisposable
{
    private readonly SemaphoreSlim _posted = new(0);
    private readonly SemaphoreSlim _finished = new(0);
    private Action? _job;
    private volatile bool _lost;
    private volatile bool _overrun;

    public StaThread(string name)
    {
        var thread = new Thread(Pump) { Name = name, IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>True once this thread has been given up on. It is never coming back.</summary>
    public bool Lost => _lost;

    /// <summary>
    /// Runs <paramref name="job"/> on the thread and waits for it, up to <paramref name="deadline"/>.
    /// False means it did not finish in time.
    ///
    /// <paramref name="abandon"/> says what that costs. True is for work that cannot be left half
    /// done — building a menu, invoking a verb — and gives the thread up for good. False is for
    /// work whose whole value is its own result, like an extension drawing a picture: the job is
    /// left to finish in its own time and collected by the next call, so one slow drawing does not
    /// take the commands with it.
    /// </summary>
    public bool Run(Action job, TimeSpan deadline, bool abandon = true)
    {
        // A thread stuck inside an extension cannot be recovered: Thread.Abort is gone from .NET
        // and would have left the apartment corrupt anyway. So it is abandoned on purpose —
        // background, so it cannot hold the process open, named, so it is recognisable in a
        // debugger — and whatever it was building is leaked with it, because releasing a COM
        // object from another thread would be a use-after-free the moment the extension returns.
        // Starting a second job behind the stuck one would only hang the next caller too.
        if (_lost) return false;
        if (_overrun && !Collect(deadline)) return false;

        _job = job;
        _posted.Release();

        if (_finished.Wait(deadline)) return true;

        if (abandon) _lost = true;
        else _overrun = true;

        return false;
    }

    /// <summary>
    /// Ends the thread. A lost one is told as well: if the extension ever does return, the thread
    /// finds this waiting and tidies itself up instead of sitting there for the rest of the run.
    /// </summary>
    public void Dispose()
    {
        _job = null;
        _posted.Release();
    }

    /// <summary>
    /// Takes the completion of a job that outstayed a deadline it was not abandoned for. Until
    /// that is done nothing may be posted: the next call would take the old job's completion for
    /// its own and report work as finished that had not started. A job that is still running when
    /// this gives up was not slow after all, and the thread goes the way of any other stuck one.
    /// </summary>
    private bool Collect(TimeSpan deadline)
    {
        if (!_finished.Wait(deadline))
        {
            _lost = true;
            return false;
        }

        _overrun = false;
        return true;
    }

    private void Pump()
    {
        // Verbs invoked from this thread reach for the clipboard — Cut and Copy put an
        // IDataObject on it — and that is the OLE half, which the CLR's own STA setup leaves out.
        Native.OleInitialize(0);

        try
        {
            while (true)
            {
                _posted.Wait();

                var job = _job;
                _job = null;
                if (job is null) return;

                // An exception that leaves a thread ends the process, and this thread runs
                // somebody else's DLL.
                try
                {
                    job();
                }
                catch (Exception)
                {
                }
                finally
                {
                    _finished.Release();
                }
            }
        }
        finally
        {
            Native.OleUninitialize();
        }
    }
}
