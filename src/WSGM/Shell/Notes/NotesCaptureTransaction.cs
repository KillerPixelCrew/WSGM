using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Runs one Add Screenshot: returns to the application or desktop, counts down, captures the selected
///     window or display, puts the image into the page and returns to that page.
/// </summary>
/// <remarks>
///     <para>
///         The countdown is the feature, not a workaround: the player needs those seconds to unpause, open a
///         map or move the camera. Nothing is captured through an open Notes surface. An application capture
///         never falls back to the desktop or another application; desktop capture targets its own display.
///     </para>
///     <para>
///         At most one transaction runs. The page itself is never held: it stays saved throughout, so a
///         cancelled, failed or shut-down transaction loses nothing, and the image is inserted into the page's
///         current revision rather than over newer edits.
///     </para>
/// </remarks>
internal sealed class NotesCaptureTransaction
{
    private readonly Func<string, byte[], int, int, NoteEditResult> _insert;
    private readonly Action _changed;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _active;
    private string? _cancelReason;
    private NotesCaptureStatus _status = NotesCaptureStatus.None;

    /// <summary>Creates the coordinator.</summary>
    /// <param name="insert">Stores the image and inserts it into a page: page id, bytes, width, height.</param>
    /// <param name="changed">Reports a status change; called outside the lock.</param>
    internal NotesCaptureTransaction(Func<string, byte[], int, int, NoteEditResult> insert, Action changed)
    {
        _insert = insert;
        _changed = changed;
    }

    /// <summary>The current status.</summary>
    internal NotesCaptureStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Whether a transaction is running.</summary>
    internal bool Pending => Status.Phase != NotesCapturePhase.Idle;

    /// <summary>Runs one screenshot transaction to its end.</summary>
    /// <param name="origin">The surface it starts from and returns to.</param>
    /// <param name="pageId">The page the image goes into.</param>
    /// <param name="seconds">The countdown, 5 to 10 seconds.</param>
    /// <param name="target">The game.</param>
    /// <param name="ports">The effects.</param>
    /// <param name="lifetime">The session's lifetime; its cancellation is a shutdown, which reopens nothing.</param>
    /// <returns>The final status; a refusal to start when another transaction runs.</returns>
    internal async Task<NotesCaptureStatus> RunAsync(NotesCaptureOrigin origin, string pageId, int seconds,
        NotesCaptureTarget target, NotesCapturePorts ports, CancellationToken lifetime)
    {
        seconds = NoteSettings.ClampCountdown(seconds);
        CancellationTokenSource active;
        lock (_gate)
        {
            if (_active is not null)
            {
                return _status with { Outcome = "A screenshot is already pending.", OutcomeIsError = true };
            }

            active = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _active = active;
            _cancelReason = null;
            _status = new NotesCaptureStatus(NotesCapturePhase.Armed, origin, pageId, seconds, seconds, null, false);
        }

        _changed();
        Log.Info($"Notes screenshot armed: {origin}, {seconds} s, {target.Description} (pid {target.ProcessId}).");
        var delay = ports.Delay ?? Task.Delay;
        var token = active.Token;
        string? failure;
        string? insertedId = null;
        try
        {
            failure = await RunPhasesAsync(origin, pageId, seconds, target, ports, delay, token,
                id => insertedId = id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            failure = lifetime.IsCancellationRequested ? null : CancelReason() ?? "Screenshot cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error("Notes screenshot failed unexpectedly.", ex);
            failure = "The screenshot failed.";
        }
        finally
        {
            HideCountdown(ports);
        }

        var shuttingDown = lifetime.IsCancellationRequested;
        var cancelledByUser = !shuttingDown && token.IsCancellationRequested;
        var outcome = failure ?? (shuttingDown ? "Screenshot cancelled." : "Screenshot added.");
        Finish(NotesCapturePhase.Restoring, outcome, failure is not null, insertedId);
        Log.Info($"Notes screenshot ended: {outcome}");

        // Back to the exact page in every case but shutdown. A user cancellation came from reopening a
        // Notes surface, which is already in front.
        if (!shuttingDown && !cancelledByUser)
        {
            try
            {
                if (!await ports.Surface.ReopenAsync(target, pageId, CancellationToken.None).ConfigureAwait(false))
                {
                    Log.Warn("Notes screenshot: the Notes surface did not report reopening.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Notes screenshot: reopening Notes failed: {ex.Message}");
            }
        }

        lock (_gate)
        {
            _status = _status with { Phase = NotesCapturePhase.Idle, Remaining = 0 };
            _active = null;
        }

        active.Dispose();
        _changed();
        return Status;
    }

    /// <summary>Cancels a pending transaction.</summary>
    /// <param name="reason">What the page says afterwards.</param>
    /// <returns>Whether a transaction was pending before capture started.</returns>
    /// <remarks>Once the window is being captured the result is about to exist, so it is kept rather than cancelled.</remarks>
    internal bool Cancel(string reason)
    {
        lock (_gate)
        {
            if (_active is null || _status.Phase is NotesCapturePhase.Capturing or NotesCapturePhase.Restoring)
            {
                return false;
            }

            _cancelReason = reason;
            _active.Cancel();
            return true;
        }
    }

    /// <summary>Forgets the last outcome once a surface has shown it.</summary>
    internal void ClearOutcome()
    {
        lock (_gate)
        {
            if (_active is not null || _status.Outcome is null)
            {
                return;
            }

            _status = NotesCaptureStatus.None;
        }

        _changed();
    }

    private async Task<string?> RunPhasesAsync(NotesCaptureOrigin origin, string pageId, int seconds,
        NotesCaptureTarget target, NotesCapturePorts ports, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken token, Action<string> inserted)
    {
        if (!target.IsDesktop && (target.ProcessId == 0 || !ports.IsAlive(target.ProcessId)))
        {
            return "The game is no longer running.";
        }

        if (!await ports.Surface.ReturnToGameAsync(target, token).ConfigureAwait(false))
        {
            return target.IsDesktop ? "WSGM could not return you to the desktop." : "WSGM could not return you to the game.";
        }

        for (var remaining = seconds; remaining > 0; remaining--)
        {
            token.ThrowIfCancellationRequested();
            if (!target.IsDesktop && !ports.IsAlive(target.ProcessId))
            {
                return "The game closed before the screenshot.";
            }

            SetPhase(NotesCapturePhase.Countdown, remaining);
            ports.Countdown.Show(remaining, target.Description);
            await delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        SetPhase(NotesCapturePhase.Capturing, 0);
        HideCountdown(ports);
        // Let the countdown leave the screen before capturing a desktop display.
        await delay(TimeSpan.FromMilliseconds(150), CancellationToken.None).ConfigureAwait(false);
        if (!target.IsDesktop && !ports.IsAlive(target.ProcessId))
        {
            return "The game closed before the screenshot.";
        }

        var frame = await ports.Capture(target, CancellationToken.None).ConfigureAwait(false);
        if (frame.Image is null)
        {
            return frame.Error ?? (target.IsDesktop ? "The desktop could not be captured." : "The game could not be captured.");
        }

        var result = _insert(pageId, frame.Image, frame.Width, frame.Height);
        if (!result.Succeeded)
        {
            return result.Error ?? "The screenshot could not be added to the page.";
        }

        inserted(result.ElementId ?? "");
        Log.Info($"Notes screenshot inserted into page {pageId}: {frame.Width}x{frame.Height}, origin {origin}.");
        return null;
    }

    private void SetPhase(NotesCapturePhase phase, int remaining)
    {
        lock (_gate)
        {
            _status = _status with { Phase = phase, Remaining = remaining };
        }

        _changed();
    }

    private void Finish(NotesCapturePhase phase, string outcome, bool error, string? insertedId)
    {
        lock (_gate)
        {
            _status = _status with
            {
                Phase = phase, Remaining = 0, Outcome = outcome, OutcomeIsError = error,
                InsertedElementId = error ? null : insertedId
            };
        }

        _changed();
    }

    private string? CancelReason()
    {
        lock (_gate)
        {
            return _cancelReason;
        }
    }

    private static void HideCountdown(NotesCapturePorts ports)
    {
        try
        {
            ports.Countdown.Hide();
        }
        catch (Exception ex)
        {
            Log.Warn($"Notes screenshot: the countdown could not be hidden: {ex.Message}");
        }
    }
}
