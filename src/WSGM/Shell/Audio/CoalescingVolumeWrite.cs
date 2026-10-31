using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Runs one direction's volume writes on a worker, collapsing a burst of slider moves
///     into the latest value.
/// </summary>
/// <param name="label">What is written, for the failure messages.</param>
internal sealed class CoalescingVolumeWrite(string label)
{
    private readonly Lock _gate = new();
    private int? _pending;
    private bool _running;

    /// <summary>Records the latest value and starts a worker unless one is already running.</summary>
    /// <param name="percentage">The value to write, 0 to 100.</param>
    /// <param name="stopped">Read on the worker before each write; true drops the pending value.</param>
    /// <param name="write">Writes one value on the worker.</param>
    /// <param name="fail">Reports a write that threw, on the worker.</param>
    internal void Queue(int percentage, Func<bool> stopped, Action<int> write, Action<string> fail)
    {
        lock (_gate)
        {
            _pending = percentage;
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    int requested;
                    lock (_gate)
                    {
                        if (_pending is not { } pending || stopped())
                        {
                            _running = false;
                            return;
                        }

                        requested = pending;
                        _pending = null;
                    }

                    try
                    {
                        write(requested);
                    }
                    catch (Exception ex)
                    {
                        // Report and carry on: unwinding the loop would strand _running at
                        // true and silently drop every later slider move.
                        fail($"{label} write failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // The flag is the only thing that lets a later write start a worker at all,
                // so it must be cleared on the abnormal exit too.
                lock (_gate)
                {
                    _running = false;
                }

                Log.Warn($"{label} write worker stopped: {ex.Message}");
            }
        });
    }

    /// <summary>Drops a value that has not been written yet.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _pending = null;
        }
    }
}
