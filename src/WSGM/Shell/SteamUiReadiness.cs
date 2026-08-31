using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Single policy gate for autonomous CEF work during Steam startup.</summary>
internal static class SteamUiReadiness
{
    /// <summary>Gets whether Steam has progressed beyond process creation to a real
    /// Big Picture window. A cold-start SharedJSContext can accept evaluations before
    /// this point; early mutation was the distinguishing state in a device-observed
    /// startup failure. BOTH conditions are required — a live steam.exe alone is not
    /// a constructed Big Picture session.</summary>
    internal static bool IsReady => Steam.IsRunning && Steam.IsBigPictureVisible;

    /// <summary>Runs one bounded automatic CEF operation after Big Picture and its target are ready.</summary>
    /// <param name="operation">Stable diagnostic name.</param>
    /// <param name="attemptAsync">Returns true when the operation completed, false to retry.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Whether the operation completed within the bounded retry window.</returns>
    internal static async Task<bool> RunWhenReadyAsync(
        string operation,
        Func<CancellationToken, Task<bool>> attemptAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(attemptAsync);
        bool waitingForBigPicture = false;
        for (int attempt = 0; attempt < 30 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(
                    attempt == 0 ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(5),
                    cancellationToken).ConfigureAwait(false);
                if (!IsReady)
                {
                    if (!waitingForBigPicture)
                    {
                        waitingForBigPicture = true;
                        Log.Info($"{operation}: waiting for the Big Picture window.");
                    }
                    continue;
                }
                if (waitingForBigPicture)
                {
                    waitingForBigPicture = false;
                    Log.Info($"{operation}: Big Picture is ready; probing CEF.");
                }
                if (await attemptAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"{operation} attempt failed: {ex.Message}");
            }
        }

        Log.Info($"{operation}: Steam UI not reachable in time; deferring until the next trigger.");
        return false;
    }
}
