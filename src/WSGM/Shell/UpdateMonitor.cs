using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Runs the daily update check while the session lives. It only records what it found; Settings
///     shows it and the user decides whether to apply it.
/// </summary>
internal sealed class UpdateMonitor : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan Wakeup = TimeSpan.FromHours(1);

    private readonly CancellationTokenSource _stop = new();
    private readonly Func<bool> _enabled;
    private readonly Task _loop;

    /// <summary>Starts the loop.</summary>
    /// <param name="enabled">Read on every wakeup, so a config reload takes effect without a rebuild.</param>
    public UpdateMonitor(Func<bool> enabled)
    {
        _enabled = enabled;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Stops the loop; an in-flight check is cancelled.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancelled.
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var http = UpdateChecker.CreateHttpClient();
        try
        {
            await Task.Delay(FirstCheckDelay, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var last = UpdateChecker.ReadState().LastCheckUtc;
                if (_enabled() && (last is null || DateTimeOffset.UtcNow - last.Value >= CheckInterval))
                {
                    await UpdateChecker.CheckAsync(http, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(Wakeup, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session ending.
        }
    }
}
