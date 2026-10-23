using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The Notes surface an Add Screenshot started from, and the one it returns to.</summary>
internal enum NotesCaptureOrigin
{
    /// <summary>WSGM's overlay.</summary>
    Overlay,

    /// <summary>Steam's in-game overlay.</summary>
    Steam
}

/// <summary>Where a screenshot transaction is.</summary>
/// <remarks>
///     Armed → Countdown → Capturing → Restoring → Idle. Cancellation, game exit, a lost foreground, a failed
///     capture and shutdown leave from any phase; every exit hides the countdown, and every exit except
///     shutdown returns the player to the same page.
/// </remarks>
internal enum NotesCapturePhase
{
    /// <summary>No screenshot is pending.</summary>
    Idle,

    /// <summary>Validated and returning the player to the game.</summary>
    Armed,

    /// <summary>The game is in front and the countdown is running.</summary>
    Countdown,

    /// <summary>The countdown expired; the game's window is being captured.</summary>
    Capturing,

    /// <summary>The image is in the page and the Notes surface is reopening.</summary>
    Restoring
}

/// <summary>The game a screenshot is of.</summary>
/// <param name="ProcessId">The game process; only a window it owns is ever captured.</param>
/// <param name="Window">The game window seen when the screenshot was requested, or zero when unknown.</param>
/// <param name="SteamAppId">The Steam app id whose overlay hosts Notes, or zero for WSGM's overlay.</param>
/// <param name="Description">The game, for messages and the log.</param>
internal sealed record NotesCaptureTarget(uint ProcessId, nint Window, uint SteamAppId, string Description);

/// <summary>The image a capture produced, or why there is none.</summary>
/// <param name="Image">Encoded PNG or JPEG bytes, or null on failure.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Error">The reason the capture failed, in words the page shows.</param>
internal sealed record NotesCaptureFrame(byte[]? Image, int Width, int Height, string? Error)
{
    /// <summary>A failed capture.</summary>
    /// <param name="error">Why.</param>
    /// <returns>The frame.</returns>
    internal static NotesCaptureFrame Failed(string error)
    {
        return new NotesCaptureFrame(null, 0, 0, error);
    }
}

/// <summary>The current screenshot state, as both surfaces show it.</summary>
/// <param name="Phase">Where the transaction is.</param>
/// <param name="Origin">The surface it started from.</param>
/// <param name="PageId">The page the image goes into.</param>
/// <param name="Seconds">The chosen countdown.</param>
/// <param name="Remaining">Seconds left while counting down.</param>
/// <param name="Outcome">How the last transaction ended, until the next one starts.</param>
/// <param name="OutcomeIsError">Whether that outcome is a failure.</param>
/// <param name="InsertedElementId">The image element the last successful capture added.</param>
internal sealed record NotesCaptureStatus(
    NotesCapturePhase Phase,
    NotesCaptureOrigin Origin,
    string? PageId,
    int Seconds,
    int Remaining,
    string? Outcome,
    bool OutcomeIsError,
    string? InsertedElementId = null)
{
    /// <summary>Nothing pending and nothing to report.</summary>
    internal static NotesCaptureStatus None { get; } =
        new(NotesCapturePhase.Idle, NotesCaptureOrigin.Overlay, null, 0, 0, null, false);
}

/// <summary>Hides a Notes surface for a screenshot and brings it back afterwards.</summary>
internal interface INotesCaptureSurface
{
    /// <summary>Closes the surface and gives the game the foreground and the input.</summary>
    /// <param name="target">The game.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>Whether the game was handed back.</returns>
    Task<bool> ReturnToGameAsync(NotesCaptureTarget target, CancellationToken cancellationToken);

    /// <summary>Opens the surface again on a page.</summary>
    /// <param name="target">The game the surface belongs to.</param>
    /// <param name="pageId">The page to show.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>Whether the surface reported the page open.</returns>
    Task<bool> ReopenAsync(NotesCaptureTarget target, string pageId, CancellationToken cancellationToken);
}

/// <summary>The countdown shown over the game while the player prepares.</summary>
internal interface INotesCountdownDisplay
{
    /// <summary>Shows or updates the countdown.</summary>
    /// <param name="remaining">Seconds left.</param>
    /// <param name="game">The game being captured.</param>
    void Show(int remaining, string game);

    /// <summary>Hides the countdown; safe to call when hidden.</summary>
    void Hide();
}

/// <summary>The effects one screenshot transaction needs, injected so it can run without a live game.</summary>
/// <param name="Surface">Returns to the game and reopens Notes, for the origin surface.</param>
/// <param name="Countdown">The countdown display.</param>
/// <param name="Capture">Captures the game's window; refuses anything not owned by the game.</param>
/// <param name="IsAlive">Whether the game process still runs.</param>
/// <param name="Delay">Waits; replaced by tests.</param>
internal sealed record NotesCapturePorts(
    INotesCaptureSurface Surface,
    INotesCountdownDisplay Countdown,
    Func<NotesCaptureTarget, CancellationToken, Task<NotesCaptureFrame>> Capture,
    Func<uint, bool> IsAlive,
    Func<TimeSpan, CancellationToken, Task>? Delay = null);

/// <summary>
///     Runs one Add Screenshot: hands the game back, counts down while the player prepares it, captures the
///     game's own window, puts the image into the page and returns to that page.
/// </summary>
/// <remarks>
///     <para>
///         The countdown is the feature, not a workaround: the player needs those seconds to unpause, open a
///         map or move the camera. Nothing is captured through an open Notes surface, and nothing but a window
///         the game process owns is ever captured, so a failure can never put a desktop or another
///         application into the note.
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
        seconds = Math.Clamp(seconds, NoteSettings.MinimumCountdown, NoteSettings.MaximumCountdown);
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
        if (target.ProcessId == 0 || !ports.IsAlive(target.ProcessId))
        {
            return "The game is no longer running.";
        }

        if (!await ports.Surface.ReturnToGameAsync(target, token).ConfigureAwait(false))
        {
            return "WSGM could not return you to the game.";
        }

        for (var remaining = seconds; remaining > 0; remaining--)
        {
            token.ThrowIfCancellationRequested();
            if (!ports.IsAlive(target.ProcessId))
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
        // One frame for the countdown to leave the screen; a window capture excludes it anyway.
        await delay(TimeSpan.FromMilliseconds(150), CancellationToken.None).ConfigureAwait(false);
        if (!ports.IsAlive(target.ProcessId))
        {
            return "The game closed before the screenshot.";
        }

        var frame = await ports.Capture(target, CancellationToken.None).ConfigureAwait(false);
        if (frame.Image is null)
        {
            return frame.Error ?? "The game could not be captured.";
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
