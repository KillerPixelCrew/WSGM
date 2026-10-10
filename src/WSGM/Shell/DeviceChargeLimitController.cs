using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Applies a charge ceiling through devices whose firmware exposes only bypass charging.</summary>
internal sealed class DeviceChargeLimitController(
    Func<RtssOsdMetrics> metrics,
    Func<bool, CancellationToken, Task<bool>> writeBypass) : IAsyncDisposable
{
    private readonly SemaphoreSlim _owner = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private bool _failed;
    private int? _limit;
    private Task _loop = Task.CompletedTask;
    private bool? _written;

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    internal async Task ConfigureAsync(int? limit, bool active, bool explicitSelection = false)
    {
        if (limit is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await _owner.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_limit == limit && active == _cancellation is not null && !explicitSelection)
            {
                return;
            }

            await StopUnderOwnerAsync().ConfigureAwait(false);
            if (explicitSelection || _limit != limit)
            {
                _failed = false;
            }

            _limit = limit;
            if (active && limit is not null)
            {
                var cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                _loop = Task.Run(() => RunAsync(cancellation.Token));
            }
        }
        finally
        {
            _owner.Release();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (!token.IsCancellationRequested && !_failed && _limit is { } limit)
            {
                var sample = metrics();
                if (DesiredBypass(sample.BatteryPercent, limit) is { } bypass && _written != bypass)
                {
                    // Claim before dispatch. A failed or uncertain write requires a new user selection.
                    _failed = true;
                    if (!await writeBypass(bypass, token).ConfigureAwait(false))
                    {
                        return;
                    }

                    _written = bypass;
                    _failed = false;
                }

                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _failed = true;
            Log.Warn($"Charge limit control stopped: {ex.Message}");
        }
    }

    internal static bool? DesiredBypass(double? percentage, int limit)
    {
        return percentage is { } battery
               && double.IsFinite(battery) && battery is >= 0 and <= 100
            ? battery >= limit
            : null;
    }

    internal async Task StopAsync()
    {
        await _owner.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopUnderOwnerAsync().ConfigureAwait(false);
        }
        finally
        {
            _owner.Release();
        }
    }

    private async Task StopUnderOwnerAsync()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
            cancellation.Dispose();
        }

        _written = null;
    }
}
