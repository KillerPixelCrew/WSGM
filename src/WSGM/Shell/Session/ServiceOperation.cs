using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>
///     The one-at-a-time background operation behind a Steam page's service: its busy flag, the notice
///     or error the page shows, and every background task the service started.
/// </summary>
/// <remarks>
///     <see cref="Busy" />, <see cref="Notice" /> and <see cref="Error" /> are guarded by the owner's state
///     lock, which this class takes as well, so they stay consistent with the rest of the state the owner
///     publishes under it.
/// </remarks>
internal sealed class ServiceOperation
{
    private readonly Lock _sync;

    // Every background task the owner started and has not seen finish. Guarded by _sync.
    private readonly List<Task> _work = [];

    /// <summary>Creates the operation over its owner's state lock.</summary>
    /// <param name="sync">The owner's state lock.</param>
    internal ServiceOperation(Lock sync)
    {
        _sync = sync;
    }

    /// <summary>Whether an operation is running. Read under the owner's lock.</summary>
    internal bool Busy { get; private set; }

    /// <summary>What the last operation or command reported. Read and written under the owner's lock.</summary>
    internal string? Notice { get; set; }

    /// <summary>Why the last operation or command failed. Read and written under the owner's lock.</summary>
    internal string? Error { get; set; }

    /// <summary>Completes when every tracked task has finished.</summary>
    internal Task Completion
    {
        get
        {
            lock (_sync)
            {
                _work.RemoveAll(static task => task.IsCompleted);
                return Task.WhenAll(_work);
            }
        }
    }

    /// <summary>Keeps a background task for <see cref="Completion" />, dropping those already finished.</summary>
    /// <param name="task">The task.</param>
    internal void Track(Task task)
    {
        lock (_sync)
        {
            _work.RemoveAll(static finished => finished.IsCompleted);
            _work.Add(task);
        }
    }

    /// <summary>Shows a notice and clears the error. Called under the owner's lock.</summary>
    /// <param name="notice">The notice.</param>
    internal void SetNotice(string notice)
    {
        Notice = notice;
        Error = null;
    }

    /// <summary>Clears the notice and the error. Called under the owner's lock.</summary>
    internal void Clear()
    {
        Notice = null;
        Error = null;
    }

    /// <summary>Runs work in the background unless another operation is running, answering the command at once.</summary>
    /// <param name="busyMessage">The refusal while another operation runs.</param>
    /// <param name="work">The work, answering the notice.</param>
    /// <param name="describe">Turns a failure other than cancellation into the error shown.</param>
    /// <param name="started">Publishes the busy state; runs outside the lock.</param>
    /// <param name="settled">
    ///     Runs outside the lock once the notice or error is set, with both, so the owner can log and publish.
    /// </param>
    /// <returns>The command's answer: applied, or refused while another operation runs.</returns>
    internal SteamUiCommandResult Start(
        string busyMessage,
        Func<Task<string>> work,
        Func<Exception, string> describe,
        Action started,
        Action<string?, string?> settled)
    {
        lock (_sync)
        {
            if (Busy)
            {
                return new SteamUiCommandResult(false, busyMessage);
            }

            Busy = true;
            Clear();
        }

        started();
        Track(Task.Run(async () =>
        {
            string? notice = null;
            string? error = null;
            try
            {
                notice = await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                error = "Cancelled.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = describe(ex);
            }
            finally
            {
                lock (_sync)
                {
                    Busy = false;
                    Notice = notice;
                    Error = error;
                }
            }

            settled(notice, error);
        }));
        return SteamUiCommandResult.Applied;
    }
}
