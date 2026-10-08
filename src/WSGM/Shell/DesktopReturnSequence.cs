using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>The ordered effects shared by normal desktop return and failed entry recovery.</summary>
internal interface IDesktopReturnBackend
{
    /// <summary>Requests leaving Big Picture before desktop restoration.</summary>
    /// <returns>Completion of the bounded exit attempt.</returns>
    Task ExitBigPictureAsync();

    /// <summary>Restores the recorded or configured desktop display layout.</summary>
    /// <returns>Whether the requested layout was restored; false preserves pending recovery.</returns>
    Task<bool> RestoreLayoutAsync();

    /// <summary>Restores desktop audio after the display attempt settles.</summary>
    /// <returns>Whether the requested audio settings were restored; false preserves pending recovery.</returns>
    Task<bool> RestoreAudioAsync();

    /// <summary>Releases game-mode UI and shell ownership before Explorer starts.</summary>
    /// <returns>Completion of the retirement attempt.</returns>
    Task RetireGameModeAsync();

    /// <summary>Restores the desktop shell using the saved Explorer anchor.</summary>
    /// <returns>Whether Explorer restoration succeeded.</returns>
    Task<bool> RestoreExplorerAsync();

    /// <summary>Runs the configured external leave actions after Explorer is restored.</summary>
    /// <returns>Completion of the leave-action sequence.</returns>
    Task RunLeaveActionsAsync();

    /// <summary>Clears the durable recovery record after display, audio and Explorer restoration.</summary>
    /// <returns>Completion after the cleared record is persisted.</returns>
    Task ClearPendingReturnAsync();
}

/// <summary>
///     Restores the desktop before optional external actions. One failed phase cannot skip
///     later recovery, and a failed layout remains recorded for a later explicit return.
/// </summary>
internal static class DesktopReturnSequence
{
    /// <summary>Attempts each desktop recovery phase in order, reporting failures without skipping later phases.</summary>
    /// <param name="backend">Borrowed effect owner for this recovery attempt.</param>
    /// <param name="runLeaveActions">Whether to run external leave actions after Explorer returns.</param>
    /// <param name="error">Nonthrowing failure reporter; throwing here interrupts recovery.</param>
    /// <param name="trace">Optional nonthrowing phase-duration reporter.</param>
    /// <returns>Whether Explorer was restored. True can still leave display or audio recovery pending.</returns>
    internal static async Task<bool> RunAsync(
        IDesktopReturnBackend backend, bool runLeaveActions, Action<string, Exception> error,
        Action<string>? trace = null)
    {
        await Attempt("Leaving Big Picture", backend.ExitBigPictureAsync).ConfigureAwait(false);
        var layoutRestored = false;
        await Attempt("Restoring the desktop layout", async () =>
            layoutRestored = await backend.RestoreLayoutAsync().ConfigureAwait(false)).ConfigureAwait(false);
        var audioRestored = false;
        await Attempt("Restoring desktop audio", async () =>
            audioRestored = await backend.RestoreAudioAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await Attempt("Retiring Game Mode", backend.RetireGameModeAsync).ConfigureAwait(false);
        var desktopRestored = false;
        await Attempt("Restoring Explorer", async () =>
            desktopRestored = await backend.RestoreExplorerAsync().ConfigureAwait(false)).ConfigureAwait(false);
        if (!desktopRestored)
        {
            return false;
        }

        // Settle the durable recovery record before an optional plugin can stall or throw.
        if (layoutRestored && audioRestored)
        {
            await Attempt("Clearing the desktop recovery record", backend.ClearPendingReturnAsync)
                .ConfigureAwait(false);
        }

        if (runLeaveActions)
        {
            await Attempt("Running leave actions", backend.RunLeaveActionsAsync).ConfigureAwait(false);
        }

        return true;

        async Task Attempt(string phase, Func<Task> action)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error(phase, ex);
            }
            finally
            {
                trace?.Invoke($"Desktop return: {phase} settled in {elapsed.ElapsedMilliseconds} ms.");
            }
        }
    }
}
