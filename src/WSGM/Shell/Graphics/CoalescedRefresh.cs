using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>Runs a refresh one at a time, and once more when it was asked for again while running.</summary>
/// <remarks>
///     One display change arrives as a burst of notifications, and a graphics refresh rereads every driver
///     feature. However many arrive while one runs, the single run after it sees the final state.
/// </remarks>
/// <param name="refresh">The refresh.</param>
internal sealed class CoalescedRefresh(Func<Task> refresh)
{
    private int _requests;

    /// <summary>Asks for a refresh.</summary>
    /// <returns>The run this request started, or a completed task when one already running will cover it.</returns>
    internal Task RequestAsync()
    {
        return Interlocked.Increment(ref _requests) == 1 ? RunAsync() : Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        try
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref _requests);
                await refresh().ConfigureAwait(false);
            } while (Interlocked.Add(ref _requests, -seen) > 0);
        }
        catch
        {
            // Not stuck after a failed run: the next request starts again.
            Volatile.Write(ref _requests, 0);
            throw;
        }
    }
}
