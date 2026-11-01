using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The session's native GPU owners as the refresh-rate pairing's fixed-rate driver: Intel's Arc Sync
///     Custom profile, wherever the driving adapter offers it for the display.
/// </summary>
/// <remarks>
///     Borrows the owners <see cref="BuiltinGpuService" /> runs and opens no driver session of its own, and
///     never blocks its caller. A range is answered from what the owners last reported and asked for again
///     on the thread pool, raising <see cref="RangeChanged" /> when the answer differs. A range once found
///     is kept while the Intel driver stays configured, so an owner that is paused across a suspend never
///     shrinks the rates offered. Writes run in order, each waiting a bounded time on the owner's lane; one
///     that did not answer in time is reported as failed and never sent again. The owner itself puts every
///     fixed rate back when it stops, which covers exit, upgrade and a switched-off graphics driver.
///     <para>
///         The extended VRR range's Arc Sync policy writes, baseline and Intel frame rate solutions share the same
///         ordered queue (<see cref="IExtendedVrrDriver" />), so they never interleave with a refresh write.
///     </para>
/// </remarks>
internal sealed class GpuFixedRefreshRate(Func<IReadOnlyList<GpuDriver>> drivers, Func<bool> configured)
    : IFixedRefreshRateDriver, IExtendedVrrDriver
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Known> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reading = new(StringComparer.OrdinalIgnoreCase);
    private Task _writes = Task.CompletedTask;

    /// <summary>How many native owners can answer now; a rise is worth asking again.</summary>
    internal int Available => drivers().Count;

    /// <inheritdoc />
    public event Action? RangeChanged;

    /// <inheritdoc />
    public DriverRefreshRange? ReadRange(DisplayTargetIdentity target)
    {
        lock (_gate)
        {
            if (_known.TryGetValue(target.DevicePath, out var known))
            {
                return known.Range;
            }
        }

        Refresh(target);
        return null;
    }

    /// <inheritdoc />
    public Task<bool> ApplyAsync(DisplayTargetIdentity target, int refreshHz)
    {
        return Enqueue(async () =>
        {
            if (DriverFor(target) is not { } driver)
            {
                Log.Warn($"Refresh rate {refreshHz} Hz: no graphics driver holds a rate on this display any more.");
                return false;
            }

            var result = await CallAsync(token => driver.SetRefreshRateAsync(Route(target), refreshHz, token))
                .ConfigureAwait(false);
            Log.Info($"Refresh rate {refreshHz} Hz through the Intel driver's Arc Sync Custom profile: "
                     + (result is null ? "no answer." : $"{result.Outcome}, {result.Detail}"));
            var applied = result?.Outcome is GpuRefreshRateOutcome.Applied;
            if (applied)
            {
                Hold(target, refreshHz);
            }

            return applied;
        });
    }

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(DisplayTargetIdentity target)
    {
        return Enqueue(async () =>
        {
            // A driver that stopped has already put its fixed rates back; nothing is left to release.
            if (DriverFor(target) is not { } driver)
            {
                return true;
            }

            var result = await CallAsync(token => driver.RestoreRefreshRateAsync(Route(target), token))
                .ConfigureAwait(false);
            if (result is not null)
            {
                Log.Info($"Refresh rate released through the Intel driver: {result.Outcome}, {result.Detail}");
            }

            var released = result?.Outcome is GpuRefreshRateOutcome.Applied or GpuRefreshRateOutcome.Unchanged;
            if (released)
            {
                Hold(target, null);
            }

            return released;
        });
    }

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

    /// <summary>Asks every owner again for the displays already asked about, in the background.</summary>
    internal void Refresh()
    {
        DisplayTargetIdentity[] targets;
        lock (_gate)
        {
            targets = [.. _known.Values.Select(known => known.Target)];
        }

        foreach (var target in targets)
        {
            Refresh(target);
        }
    }

    private void Refresh(DisplayTargetIdentity target)
    {
        lock (_gate)
        {
            if (!_reading.Add(target.DevicePath))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            bool changed;
            try
            {
                var found = await FindAsync(target).ConfigureAwait(false);
                lock (_gate)
                {
                    _known.TryGetValue(target.DevicePath, out var previous);
                    var next = found is { } answer
                        ? new Known(target, answer.Driver, Whole(answer.Range))
                        // Nothing answered. While no owner runs at all and the Intel driver is still
                        // configured, that is a pause, not a different display: keep what it reported.
                        : drivers().Count == 0 && configured() && previous?.Range is not null
                            ? previous
                            : new Known(target, null, null);
                    _known[target.DevicePath] = next;
                    changed = !Equals(previous?.Range, next.Range);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _reading.Remove(target.DevicePath);
                }
            }

            if (changed)
            {
                RangeChanged?.Invoke();
            }
        });
    }

    private async Task<(GpuDriver Driver, GpuRefreshRateRange Range)?> FindAsync(DisplayTargetIdentity target)
    {
        var route = Route(target);
        foreach (var driver in drivers())
        {
            if (await CallAsync(token => driver.ReadRefreshRateRangeAsync(route, token)).ConfigureAwait(false) is
                { } range)
            {
                return (driver, range);
            }
        }

        return null;
    }

    private GpuDriver? DriverFor(DisplayTargetIdentity target)
    {
        lock (_gate)
        {
            return _known.TryGetValue(target.DevicePath, out var known) && known.Driver is { } driver
                                                                         && drivers().Contains(driver)
                ? driver
                : null;
        }
    }

    /// <summary>Keeps the answered range in step with the rate this owner now holds.</summary>
    private void Hold(DisplayTargetIdentity target, int? refreshHz)
    {
        lock (_gate)
        {
            if (_known.TryGetValue(target.DevicePath, out var known) && known.Range is { } range)
            {
                _known[target.DevicePath] = known with { Range = range with { FixedHz = refreshHz } };
            }
        }
    }

    /// <summary>
    ///     Runs one call on the owner that answered for the display, finding it first when none did yet, in
    ///     order behind every other call.
    /// </summary>
    /// <returns>The answer, or default when no owner answers for the display or it did not answer in time.</returns>
    private Task<T?> OnDriverAsync<T>(DisplayTargetIdentity target,
        Func<GpuDriver, GpuDisplayTarget, CancellationToken, ValueTask<T>> call)
    {
        return Enqueue(async () =>
        {
            // The pairing's own record is left to its background read, which announces a changed range.
            var driver = DriverFor(target) ?? (await FindAsync(target).ConfigureAwait(false))?.Driver;
            return driver is null
                ? default
                : await CallAsync(token => call(driver, Route(target), token)).ConfigureAwait(false);
        });
    }

    /// <summary>Runs one write after the ones before it, whatever they did.</summary>
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
            Log.Warn($"The graphics driver did not answer a refresh-rate call within {Wait.TotalSeconds:0} s.");
            return default;
        }
    }

    private static DriverRefreshRange Whole(GpuRefreshRateRange range)
    {
        return new DriverRefreshRange((int)Math.Ceiling(range.MinimumHz - 0.001),
            (int)Math.Floor(range.MaximumHz + 0.001),
            range.FixedHz is { } held ? (int)Math.Round(held) : null);
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

    /// <summary>What the owners last said about one display: who answered, and the range, or null for none.</summary>
    private sealed record Known(DisplayTargetIdentity Target, GpuDriver? Driver, DriverRefreshRange? Range);
}
