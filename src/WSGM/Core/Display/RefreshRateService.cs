using System;
using System.Collections.Generic;
using System.Threading;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>The refresh rates Windows lists for the current display mode, and the one write that chooses among them.</summary>
/// <remarks>
///     A rate is a Windows mode change the user asks for by hand. Nothing else moves it: the frame limit is
///     a cap and changes no display state, and no graphics driver holds a rate in between the listed ones.
///     <para>
///         Discovery is cached for the current display, resolution and colour depth. A topology or mode
///         change invalidates it, and a read is checked against that identity before it is cached.
///     </para>
/// </remarks>
internal sealed class RefreshRateService
{
    private readonly Func<DisplayTargetIdentity, int, bool> _applyRate;
    private readonly Lock _discoveryGate = new();
    private readonly Lock _gate = new();
    private readonly Func<DisplayOperatingPoint?> _readOperatingPoint;
    private readonly Func<IReadOnlyList<int>> _readRates;
    private readonly Func<DisplayTargetIdentity, int?> _readTargetRate;

    private DisplayOperatingPoint? _operatingPoint;
    private bool _operatingPointKnown;
    private long _operatingPointRevision;
    private IReadOnlyList<int>? _rates;

    /// <summary>Creates the service over the real display.</summary>
    internal RefreshRateService()
        : this(
            PrimaryDisplayModes.EnumerateRefreshRates,
            PrimaryDisplayModes.ReadPrimaryOperatingPoint,
            PrimaryDisplayModes.TryRestoreRefreshRate,
            target => DisplayModes.ReadCurrent(target)?.RefreshHz)
    {
    }

    /// <summary>Creates the service over supplied display operations, for tests.</summary>
    /// <param name="readRates">The rates Windows lists for the current mode.</param>
    /// <param name="readOperatingPoint">Reads display identity and dimensions.</param>
    /// <param name="applyRate">Applies a Windows mode rate to the captured target.</param>
    /// <param name="readTargetRate">Reads the Windows mode rate from the captured target.</param>
    internal RefreshRateService(
        Func<IReadOnlyList<int>> readRates,
        Func<DisplayOperatingPoint?> readOperatingPoint,
        Func<DisplayTargetIdentity, int, bool> applyRate,
        Func<DisplayTargetIdentity, int?> readTargetRate)
    {
        _readRates = readRates ?? throw new ArgumentNullException(nameof(readRates));
        _readOperatingPoint = readOperatingPoint ?? throw new ArgumentNullException(nameof(readOperatingPoint));
        _applyRate = applyRate ?? throw new ArgumentNullException(nameof(applyRate));
        _readTargetRate = readTargetRate ?? throw new ArgumentNullException(nameof(readTargetRate));
    }

    /// <summary>Raised, on whichever thread finished the work, when what the refresh rows show may have changed.</summary>
    /// <remarks>Raised even when a write changed nothing, so a slider left where it was dragged returns to the rate in force.</remarks>
    internal event Action? Changed;

    /// <summary>Invalidates discovery after a display notification or resume, without querying the display.</summary>
    internal void Invalidate()
    {
        lock (_gate)
        {
            _rates = null;
            _operatingPoint = null;
            _operatingPointKnown = false;
            _operatingPointRevision++;
        }

        Changed?.Invoke();
    }

    /// <summary>The rates Windows lists for the current mode, discovered once and shared by every consumer.</summary>
    /// <returns>Ascending; empty when the display cannot be read.</returns>
    /// <remarks>
    ///     Never a mode test: the list is the same before and after a resume, because a panel still waking
    ///     refused every mode but its current one and that answer used to shrink the list.
    /// </remarks>
    internal IReadOnlyList<int> ListedRates()
    {
        return Discover()?.Rates ?? [];
    }

    /// <summary>The refresh rate of the current Windows mode.</summary>
    /// <returns>The rate, or null when the display cannot be read.</returns>
    internal int? CurrentRate()
    {
        return Discover() is { } discovered ? _readTargetRate(discovered.Point.Target) : null;
    }

    /// <summary>The slider position of a rate: the listed rate at or below it, for a mode something else left the panel on.</summary>
    /// <param name="rates">The listed rates, ascending.</param>
    /// <param name="refreshHz">The rate in force, or null when unread.</param>
    /// <returns>The index into <paramref name="rates" />, or null when there is no rate to place.</returns>
    internal static int? PositionOf(IReadOnlyList<int> rates, int? refreshHz)
    {
        if (refreshHz is not { } current || rates.Count == 0)
        {
            return null;
        }

        var position = 0;
        for (var index = 0; index < rates.Count; index++)
        {
            if (rates[index] <= current)
            {
                position = index;
            }
        }

        return position;
    }

    /// <summary>Applies a refresh rate the user chose.</summary>
    /// <param name="refreshHz">The chosen rate; the nearest listed rate applies, the higher of two equally near.</param>
    /// <returns>Whether the display is now at the rate.</returns>
    /// <remarks>
    ///     Checked against the list rather than passed to Windows: the value can arrive from injected
    ///     JavaScript. Written once against the display the list describes, and refused when that changed.
    /// </remarks>
    internal bool TryApply(int refreshHz)
    {
        try
        {
            RefreshOperatingPoint(true);
            if (Discover() is not { } current)
            {
                return false;
            }

            if (NearestRate(current.Rates, refreshHz) is not { } listed)
            {
                Log.Warn($"Refresh rate {refreshHz} Hz refused: Windows lists no refresh rate for this mode.");
                return false;
            }

            lock (_gate)
            {
                if (current.Revision != _operatingPointRevision)
                {
                    return false;
                }
            }

            return _applyRate(current.Point.Target, listed);
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The two ends of the frame cap range for this display.</summary>
    /// <returns>The inclusive bounds, or null when no rate is high enough to carry a cap.</returns>
    /// <remarks>
    ///     Every surface that offers a frame limit asks this, so the overlay's slider and the Quick
    ///     Access row cannot disagree about what a legal cap is.
    /// </remarks>
    internal (int Minimum, int Maximum)? FrameLimitRange()
    {
        return FrameLimitCaps.Range(ListedRates());
    }

    /// <summary>The frame caps worth offering on this display.</summary>
    /// <returns>Zero first for off, then every cap in <see cref="FrameLimitRange" />.</returns>
    internal IReadOnlyList<int> FrameLimitOptions()
    {
        return FrameLimitCaps.Options(ListedRates());
    }

    private static int? NearestRate(IReadOnlyList<int> rates, int refreshHz)
    {
        int? nearest = null;
        foreach (var rate in rates)
        {
            if (nearest is not { } best
                || Math.Abs(rate - refreshHz) < Math.Abs(best - refreshHz)
                || (Math.Abs(rate - refreshHz) == Math.Abs(best - refreshHz) && rate > best))
            {
                nearest = rate;
            }
        }

        return nearest;
    }

    private (DisplayOperatingPoint Point, IReadOnlyList<int> Rates, long Revision)? Discover()
    {
        using var discovery = _discoveryGate.EnterScope();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RefreshOperatingPoint();
            long revision;
            DisplayOperatingPoint point;
            lock (_gate)
            {
                if (_operatingPoint is null)
                {
                    return null;
                }

                revision = _operatingPointRevision;
                if (_rates is not null)
                {
                    return (_operatingPoint, _rates, revision);
                }

                point = _operatingPoint;
            }

            var rates = _readRates();
            RefreshOperatingPoint(true);
            lock (_gate)
            {
                if (revision == _operatingPointRevision)
                {
                    _rates = rates;
                    return (point, rates, revision);
                }
            }
        }

        return null;
    }

    private void RefreshOperatingPoint(bool force = false)
    {
        using var discovery = _discoveryGate.EnterScope();
        long revision;
        lock (_gate)
        {
            if (_operatingPointKnown && !force)
            {
                return;
            }

            revision = _operatingPointRevision;
        }

        var observed = _readOperatingPoint();
        lock (_gate)
        {
            // A notification arriving during the read owns the next discovery. Never let an old
            // observation mark that newer revision as current.
            if (revision != _operatingPointRevision)
            {
                return;
            }

            if (observed != _operatingPoint)
            {
                _rates = null;
                _operatingPoint = observed;
                _operatingPointRevision++;
            }

            _operatingPointKnown = true;
        }
    }
}
