using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>The ordered effects shared by normal desktop return and failed entry recovery.</summary>
internal interface IDesktopReturnBackend
{
    Task ExitBigPictureAsync();
    Task<bool> RestoreLayoutAsync();
    Task RetireGameModeAsync();
    Task<bool> RestoreExplorerAsync();
    Task RunLeaveActionsAsync();
    Task ClearPendingReturnAsync();
}

/// <summary>Restores the desktop before optional external actions. One failed phase cannot skip
/// later recovery, and a failed layout remains recorded for a later explicit return.</summary>
internal static class DesktopReturnSequence
{
    internal static async Task<bool> RunAsync(
        IDesktopReturnBackend backend, bool runLeaveActions, Action<string, Exception> error,
        Action<string>? trace = null)
    {
        async Task<bool> Attempt(string phase, Func<Task> action)
        {
            var elapsed = Stopwatch.StartNew();
            try { await action().ConfigureAwait(false); return true; }
            catch (Exception ex) { error(phase, ex); return false; }
            finally { trace?.Invoke($"Desktop return: {phase} settled in {elapsed.ElapsedMilliseconds} ms."); }
        }

        await Attempt("Leaving Big Picture", backend.ExitBigPictureAsync).ConfigureAwait(false);
        var layoutRestored = false;
        await Attempt("Restoring the desktop layout", async () =>
            layoutRestored = await backend.RestoreLayoutAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await Attempt("Retiring Game Mode", backend.RetireGameModeAsync).ConfigureAwait(false);
        var desktopRestored = false;
        await Attempt("Restoring Explorer", async () =>
            desktopRestored = await backend.RestoreExplorerAsync().ConfigureAwait(false)).ConfigureAwait(false);
        if (desktopRestored)
        {
            // Settle the durable recovery record before an optional plugin can stall or throw.
            if (layoutRestored)
            {
                await Attempt("Clearing the desktop recovery record", backend.ClearPendingReturnAsync)
                    .ConfigureAwait(false);
            }
            if (runLeaveActions)
            {
                await Attempt("Running leave actions", backend.RunLeaveActionsAsync).ConfigureAwait(false);
            }
        }
        return desktopRestored;
    }
}
