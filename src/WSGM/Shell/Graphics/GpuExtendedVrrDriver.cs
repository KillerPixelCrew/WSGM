using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The session's native GPU owners as the extended VRR range's Intel driver.</summary>
/// <remarks>
///     Borrows the owners <see cref="BuiltinGpuService" /> runs and opens no driver session of its own, and
///     never blocks its caller. The Arc Sync policy writes, baseline and Intel frame rate solutions run in
///     order, each waiting a bounded time on the owner's lane; one that did not answer in time is reported as
///     failed and never sent again.
/// </remarks>
internal sealed class GpuExtendedVrrDriver(Func<IReadOnlyList<GpuDriver>> drivers) : IExtendedVrrDriver
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private Task _writes = Task.CompletedTask;

    /// <inheritdoc />
    public Task<GpuArcSyncState?> ReadArcSyncAsync(DisplayTargetIdentity target)
    {
        return OnDriverAsync<GpuArcSyncState?>(target,
            (driver, display, token) => driver.ReadArcSyncAsync(display, token));
    }

    /// <inheritdoc />
    public async Task<bool> SetBaselineAsync(DisplayTargetIdentity target, GpuArcSyncBaseline? baseline)
    {
        return await OnDriverAsync<bool?>(target,
                   async (driver, display, token) =>
                       await driver.SetRefreshRateBaselineAsync(display, baseline, token).ConfigureAwait(false))
               .ConfigureAwait(false) == true;
    }

    /// <inheritdoc />
    public Task<GpuDisplayWriteResult?> SetProfileAsync(DisplayTargetIdentity target, GpuArcSyncBaseline profile)
    {
        return OnDriverAsync<GpuDisplayWriteResult?>(target,
            async (driver, display, token) =>
                await driver.SetArcSyncProfileAsync(display, profile, token).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<GpuVrrSolutions?> ReadSolutionsAsync(DisplayTargetIdentity target)
    {
        return OnDriverAsync<GpuVrrSolutions?>(target,
            (driver, display, token) => driver.ReadVrrSolutionsAsync(display, token));
    }

    /// <inheritdoc />
    public Task<GpuDisplayWriteResult?> SetSolutionAsync(DisplayTargetIdentity target, GpuVrrSolution solution,
        bool enabled)
    {
        return OnDriverAsync<GpuDisplayWriteResult?>(target,
            async (driver, display, token) =>
                await driver.SetVrrSolutionAsync(display, solution, enabled, token).ConfigureAwait(false));
    }

    /// <summary>Runs one call on the owner that answers for the display, in order behind every other call.</summary>
    /// <returns>The answer, or default when no owner answers for the display or it did not answer in time.</returns>
    private Task<T?> OnDriverAsync<T>(DisplayTargetIdentity target,
        Func<GpuDriver, GpuDisplayTarget, CancellationToken, ValueTask<T>> call)
    {
        return Enqueue(async () =>
        {
            var driver = await FindAsync(target).ConfigureAwait(false);
            return driver is null
                ? default
                : await CallAsync(token => call(driver, Route(target), token)).ConfigureAwait(false);
        });
    }

    private async Task<GpuDriver?> FindAsync(DisplayTargetIdentity target)
    {
        var route = Route(target);
        foreach (var driver in drivers())
        {
            if (await CallAsync(token => driver.ReadRefreshRateRangeAsync(route, token)).ConfigureAwait(false)
                is not null)
            {
                return driver;
            }
        }

        return null;
    }

    /// <summary>Runs one call after the ones before it, whatever they did.</summary>
    private Task<T> Enqueue<T>(Func<Task<T>> work)
    {
        lock (_gate)
        {
            var next = _writes.ContinueWith(_ => work(), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
            _writes = next;
            return next;
        }
    }

    private static async Task<T?> CallAsync<T>(Func<CancellationToken, ValueTask<T>> call)
    {
        using var timeout = new CancellationTokenSource(Wait);
        try
        {
            return await call(timeout.Token).AsTask().WaitAsync(Wait).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            Log.Warn($"The graphics driver did not answer an Arc Sync call within {Wait.TotalSeconds:0} s.");
            return default;
        }
    }

    private static GpuDisplayTarget Route(DisplayTargetIdentity target)
    {
        return new GpuDisplayTarget
        {
            DevicePath = target.DevicePath, EdidIdentity = target.EdidIdentity,
            AdapterLowPart = target.AdapterLowPart, AdapterHighPart = target.AdapterHighPart,
            TargetId = target.TargetId
        };
    }
}
