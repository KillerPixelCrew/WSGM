using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Update all: applies pending updates one at a time through the ordinary update transaction. A failed update is
///     recorded and the next one still runs; cancellation stops the sequence before the next update.
/// </summary>
internal static class EmulatorUpdateSequence
{
    /// <summary>Runs the updates in order and reports the run after every step.</summary>
    /// <param name="updates">The updates to apply, in order.</param>
    /// <param name="update">Applies one installation's update; a throw is that update's failure.</param>
    /// <param name="publish">Receives the run's state before each update and once at the end.</param>
    /// <param name="token">Stops the sequence; the running update is cancelled by its own transaction.</param>
    /// <returns>The finished run.</returns>
    internal static async Task<EmulatorUpdateAllState> RunAsync(IReadOnlyList<EmulatorPendingUpdate> updates,
        Func<EmulatorPendingUpdate, CancellationToken, Task> update, Action<EmulatorUpdateAllState> publish,
        CancellationToken token)
    {
        List<string> updated = [];
        List<string> failed = [];
        var stoppedAt = -1;
        for (var index = 0; index < updates.Count; index++)
        {
            var next = updates[index];
            if (token.IsCancellationRequested)
            {
                stoppedAt = index;
                break;
            }

            publish(new EmulatorUpdateAllState(true, index, updates.Count, next.Name, [.. updated], [.. failed], ""));
            try
            {
                await update(next, token).ConfigureAwait(false);
                updated.Add(next.Name + " " + next.Version);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                stoppedAt = index;
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add(next.Name + ": " + ex.Message);
            }
        }

        string[] remaining = stoppedAt < 0 ? [] : [.. updates.Skip(stoppedAt).Select(item => item.Name)];
        var result = new EmulatorUpdateAllState(false, updated.Count + failed.Count, updates.Count, "", updated,
            failed, Outcome(updated, failed, remaining)) { Cancelled = stoppedAt >= 0 };
        publish(result);
        return result;
    }

    /// <summary>What the run did, for the banner and the status line; a partial failure is never worded as success.</summary>
    /// <param name="updated">The emulators that updated, with their versions.</param>
    /// <param name="failed">Each failure as "Name: reason".</param>
    /// <param name="remaining">The emulators the run stopped before.</param>
    /// <returns>The outcome.</returns>
    internal static string Outcome(IReadOnlyList<string> updated, IReadOnlyList<string> failed,
        IReadOnlyList<string> remaining)
    {
        List<string> parts = [];
        if (updated.Count > 0)
        {
            parts.Add("Updated " + EmulatorPresentation.JoinAnd(updated) + ".");
        }

        foreach (var failure in failed)
        {
            var split = failure.IndexOf(": ", StringComparison.Ordinal);
            parts.Add(split < 0
                ? failure + " was not updated."
                : failure[..split] + " was not updated: " + failure[(split + 2)..]);
        }

        if (remaining.Count > 0)
        {
            parts.Add("Update all was cancelled before " + EmulatorPresentation.JoinAnd(remaining)
                                                         + ". Their active versions are preserved.");
        }

        return parts.Count == 0 ? "Nothing needed updating." : string.Join(" ", parts);
    }
}
