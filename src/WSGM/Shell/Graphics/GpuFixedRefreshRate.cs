using System;
using System.Collections.Generic;
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
///     Borrows the owners <see cref="BuiltinGpuService" /> runs and opens no driver session of its own. A
///     range once found for a display is kept while the Intel driver stays configured, so an owner that is
///     paused across a suspend or still starting never shrinks the rates offered. The
///     pairing calls synchronously, so each call waits on the owner's lane for a bounded time; a write
///     that did not answer in time is reported as failed and never sent again. The owner itself puts every
///     fixed rate back when it stops, which covers exit, upgrade and a switched-off graphics driver.
/// </remarks>
internal sealed class GpuFixedRefreshRate(Func<IReadOnlyList<GpuDriver>> drivers, Func<bool> configured)
    : IFixedRefreshRateDriver
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, DriverRefreshRange> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _knownGate = new();

    /// <summary>How many native owners can answer now; a change is worth a new discovery.</summary>
    internal int Available => drivers().Count;

    /// <inheritdoc />
    public DriverRefreshRange? ReadRange(DisplayTargetIdentity target)
    {
        if (Find(target) is { Range: var range })
        {
            DriverRefreshRange found = new((int)Math.Ceiling(range.MinimumHz - 0.001),
                (int)Math.Floor(range.MaximumHz + 0.001),
                range.FixedHz is { } held ? (int)Math.Round(held) : null);
            lock (_knownGate)
            {
                _known[target.DevicePath] = found;
            }

            return found;
        }

        // Nothing answered. While no owner runs at all and the Intel driver is still configured, that is a
        // pause, not a different display: keep what it reported, so the offered rates never shrink.
        lock (_knownGate)
        {
            if (drivers().Count == 0 && configured() && _known.TryGetValue(target.DevicePath, out var known))
            {
                return known with { FixedHz = null };
            }

            _known.Remove(target.DevicePath);
            return null;
        }
    }

    /// <inheritdoc />
    public bool Apply(DisplayTargetIdentity target, int refreshHz)
    {
        if (Find(target) is not { Driver: var driver })
        {
            Log.Warn($"Refresh rate {refreshHz} Hz: no graphics driver holds a rate on this display any more.");
            return false;
        }

        var result = Run(token => driver.SetRefreshRateAsync(Route(target), refreshHz, token));
        Log.Info($"Refresh rate {refreshHz} Hz through the Intel driver's Arc Sync Custom profile: "
                 + $"{result?.Outcome.ToString() ?? "no answer"}"
                 + (result is null ? "." : $", {result.Detail}"));
        return result?.Outcome is GpuRefreshRateOutcome.Applied;
    }

    /// <inheritdoc />
    public bool Release(DisplayTargetIdentity target)
    {
        // A driver that stopped has already put its fixed rates back; nothing is left to release.
        if (Find(target) is not { Driver: var driver })
        {
            return true;
        }

        var result = Run(token => driver.RestoreRefreshRateAsync(Route(target), token));
        if (result is not null)
        {
            Log.Info($"Refresh rate released through the Intel driver: {result.Outcome}, {result.Detail}");
        }

        return result?.Outcome is GpuRefreshRateOutcome.Applied or GpuRefreshRateOutcome.Unchanged;
    }

    private (GpuDriver Driver, GpuRefreshRateRange Range)? Find(DisplayTargetIdentity target)
    {
        var route = Route(target);
        foreach (var driver in drivers())
        {
            if (Run(token => driver.ReadRefreshRateRangeAsync(route, token)) is { } range)
            {
                return (driver, range);
            }
        }

        return null;
    }

    private static T? Run<T>(Func<CancellationToken, ValueTask<T>> call)
    {
        using var timeout = new CancellationTokenSource(Wait);
        try
        {
            return Task.Run(async () => await call(timeout.Token).ConfigureAwait(false), timeout.Token)
                .WaitAsync(Wait)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            Log.Warn($"The graphics driver did not answer a refresh-rate call within {Wait.TotalSeconds:0} s.");
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
