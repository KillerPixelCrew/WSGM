using System.Collections.Generic;
using System.Threading;

namespace WSGM.DeviceLab.Worker;

/// <summary>
///     The cancellation of the one call the worker runs at a time. The worker reads <c>cancel</c> requests
///     while a call runs, so a wizard that stops waiting can end the call's wait early.
/// </summary>
/// <remarks>
///     Cancelling only ends a wait inside the service. A write the service already sent stays sent and is
///     never retried; the call still returns its result.
/// </remarks>
internal sealed class LabWorkerCalls
{
    // A cancel can arrive before its call starts; remember a few of those.
    private const int MaxEarly = 64;

    private readonly HashSet<long> _early = [];
    private readonly Lock _gate = new();
    private bool _closed;
    private CancellationTokenSource? _current;
    private long _currentId;

    /// <summary>Starts a call; its token is already cancelled when a cancel came first.</summary>
    /// <param name="id">The call's request ID.</param>
    /// <returns>The call's cancellation; pass it to <see cref="End" />.</returns>
    public CancellationTokenSource Begin(long id)
    {
        lock (_gate)
        {
            CancellationTokenSource call = new();
            if (_closed || _early.Remove(id))
            {
                call.Cancel();
            }

            _early.RemoveWhere(item => item < id);
            _current = call;
            _currentId = id;
            return call;
        }
    }

    /// <summary>Ends a call.</summary>
    /// <param name="call">From <see cref="Begin" />.</param>
    public void End(CancellationTokenSource call)
    {
        lock (_gate)
        {
            if (_current == call)
            {
                _current = null;
            }
        }

        call.Dispose();
    }

    /// <summary>Cancels the running call with this ID, or the call when it starts.</summary>
    /// <param name="id">The call's request ID.</param>
    public void Cancel(long id)
    {
        lock (_gate)
        {
            if (_current is not null && _currentId == id)
            {
                _current.Cancel();
            }
            else if (id > _currentId && _early.Count < MaxEarly)
            {
                _early.Add(id);
            }
        }
    }

    /// <summary>The wizard is gone: cancels the running call and every later one.</summary>
    public void CancelAll()
    {
        lock (_gate)
        {
            _closed = true;
            _current?.Cancel();
        }
    }
}
