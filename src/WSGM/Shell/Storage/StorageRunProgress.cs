using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The one storage run at a time behind the format page: an SD-card format or an Add Library.
///     Serializes those runs, publishes whether one is in flight and its current stage, and reports
///     each run's terminal message. UI-thread owner for its bound properties.
/// </summary>
/// <param name="writeLog">Writes the terminal outcome to the log.</param>
public sealed class StorageRunProgress(Action<LogLevel, string, Exception?> writeLog) : ObservableObject
{
    /// <summary>Serializes storage runs: strictly one at a time.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    ///     Gets whether a run is in flight. The flow's buttons and
    ///     the target list disable while true.
    /// </summary>
    public bool Busy
    {
        get;
        private set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(Busy)))
            {
                return;
            }

            Raise(nameof(NotBusy));
        }
    }

    /// <summary>Gets the inverse of <see cref="Busy" />, for IsEnabled bindings.</summary>
    public bool NotBusy => !Busy;

    /// <summary>Gets the current stage or terminal outcome of the run.</summary>
    public string StatusText
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(StatusText)))
            {
                return;
            }

            Raise(nameof(HasStatus));
        }
    } = "";

    /// <summary>Gets whether a status line should be shown.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>
    ///     Raised on the UI thread when a run finishes, with the
    ///     terminal message — the controller surfaces it even when the overlay has
    ///     been closed mid-format.
    /// </summary>
    public event Action<string, bool>? Finished;

    /// <summary>Waits for the previous run to end, then marks this one in flight.</summary>
    /// <param name="cancellationToken">Cancels the wait; nothing is entered when it fires.</param>
    /// <returns>Completion once this run holds the gate; pair it with <see cref="Exit" />.</returns>
    internal async Task EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        Busy = true;
    }

    /// <summary>Marks the run finished and lets the next one start.</summary>
    internal void Exit()
    {
        Busy = false;
        _gate.Release();
    }

    /// <summary>Publishes and logs the run's terminal message and raises <see cref="Finished" />.</summary>
    /// <param name="message">The user-facing outcome.</param>
    /// <param name="success">Whether the run succeeded.</param>
    internal void Finish(string message, bool success)
    {
        StatusText = message;
        if (success)
        {
            writeLog(LogLevel.Info, $"Format: done — {message}", null);
        }
        else
        {
            writeLog(LogLevel.Warn, $"Format: failed — {message}", null);
        }

        Finished?.Invoke(message, success);
    }
}
