namespace Oan.Core.Shell;

/// <summary>One STA thread for work that runs code we did not write (COM shell extensions); every call gives up at a deadline.
/// A single thread for the whole job, because a COM object lives in the apartment that created it.</summary>
public sealed class StaThread : IDisposable
{
    private readonly SemaphoreSlim _posted = new(0);
    private readonly SemaphoreSlim _finished = new(0);
    private Action? _job;
    private volatile bool _lost;
    private volatile bool _pending;

    public StaThread(string name)
    {
        var thread = new Thread(Pump) { Name = name, IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>True once this thread has been given up on.</summary>
    public bool Lost => _lost;

    /// <summary>Runs <paramref name="job"/> and waits up to <paramref name="deadline"/>; false means it did not finish in time.
    /// <paramref name="abandon"/> true gives the thread up for good; false leaves the job to finish and be collected by the next call.</summary>
    public bool Run(Action job, TimeSpan deadline, bool abandon = true)
    {
        // A thread stuck inside an extension cannot be recovered (no Thread.Abort), so it is abandoned, and what it built is
        // leaked with it: releasing a COM object from another thread is a use-after-free once the extension returns.
        if (_lost) return false;
        if (_pending && !Collect(deadline)) return false;

        _job = job;
        _posted.Release();

        if (_finished.Wait(deadline)) return true;

        if (abandon) _lost = true;
        else _pending = true;

        return false;
    }

    /// <summary>Starts <paramref name="job"/> without waiting for it; the next call collects it first.
    /// False, and nothing started, when the thread is lost or an earlier job has not finished.</summary>
    public bool Post(Action job)
    {
        // A finished earlier job's completion is taken here, or the next call would take it for this one's.
        if (_lost || (_pending && !_finished.Wait(0))) return false;

        _pending = true;
        _job = job;
        _posted.Release();
        return true;
    }

    /// <summary>Waits up to <paramref name="deadline"/> for a job that was posted or overran; false means it did not finish
    /// and the thread is given up on.</summary>
    public bool Wait(TimeSpan deadline) => !_lost && (!_pending || Collect(deadline));

    /// <summary>Ends the thread. A lost one is told as well, in case the extension ever returns.</summary>
    public void Dispose()
    {
        _job = null;
        _posted.Release();
    }

    /// <summary>Takes the completion of a job left running: posted, or overran without being abandoned. Nothing may be posted
    /// before that, or the next call would take the old job's completion for its own.</summary>
    private bool Collect(TimeSpan deadline)
    {
        if (!_finished.Wait(deadline))
        {
            _lost = true;
            return false;
        }

        _pending = false;
        return true;
    }

    private void Pump()
    {
        // Cut and Copy put an IDataObject on the clipboard: that is OLE, which the CLR's STA setup leaves out.
        Native.OleInitialize(0);

        try
        {
            while (true)
            {
                _posted.Wait();

                var job = _job;
                _job = null;
                if (job is null) return;

                // An exception that leaves a thread ends the process, and this thread runs somebody else's DLL.
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
