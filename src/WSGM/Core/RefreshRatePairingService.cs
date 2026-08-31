using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>
/// Applies the refresh rate that goes with the frame cap in force, under the user's strategy.
/// </summary>
/// <remarks>
/// The pairing decision itself is <see cref="FrameLimitPairing"/> and stays pure; this owns the
/// parts that touch the machine — discovering what the display accepts, caching that, applying a
/// rate, and putting the original back.
/// <para>
/// Discovery is cached because it is not free: every candidate rate costs a `CDS_TEST` round trip
/// through the driver, and a cap change is a user-facing action that should not stall behind a dozen
/// of them. <see cref="Invalidate"/> drops the cache when the display itself may have changed.
/// </para>
/// </remarks>
internal sealed class RefreshRatePairingService
{
    private readonly Func<IReadOnlyList<int>> _readAcceptedRates;
    private readonly Func<IReadOnlyList<int>> _readAdvertisedRates;
    private readonly Func<int, bool> _applyRate;
    private readonly Func<int?> _readCurrentRate;
    private readonly object _gate = new();

    private IReadOnlyList<int>? _accepted;
    private IReadOnlyList<int>? _advertised;
    private int? _originalRate;
    private FrameLimitStrategy _strategy = FrameLimitStrategy.FrameLimitOnly;

    /// <summary>Creates the service over the real display.</summary>
    internal RefreshRatePairingService()
        : this(
            DisplayProfiles.EnumerateAcceptedRefreshRates,
            DisplayProfiles.ReadAdvertisedRefreshRates,
            DisplayProfiles.TryApplyTransientRefreshRate,
            DisplayProfiles.ReadCurrentRefreshRate)
    {
    }

    /// <summary>Creates the service over supplied display operations, for tests.</summary>
    /// <param name="readAcceptedRates">Every rate the driver accepts.</param>
    /// <param name="readAdvertisedRates">Rates the panel itself advertises.</param>
    /// <param name="applyRate">Applies a rate, returning whether it took.</param>
    /// <param name="readCurrentRate">Reads the rate in force.</param>
    internal RefreshRatePairingService(
        Func<IReadOnlyList<int>> readAcceptedRates,
        Func<IReadOnlyList<int>> readAdvertisedRates,
        Func<int, bool> applyRate,
        Func<int?> readCurrentRate
    )
    {
        _readAcceptedRates = readAcceptedRates;
        _readAdvertisedRates = readAdvertisedRates;
        _applyRate = applyRate;
        _readCurrentRate = readCurrentRate;
    }

    /// <summary>The strategy currently in force.</summary>
    internal FrameLimitStrategy Strategy
    {
        get
        {
            lock (_gate)
            {
                return _strategy;
            }
        }
    }

    /// <summary>
    /// Adopts a strategy, restoring the display first when the new one no longer owns it.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    internal void SetStrategy(FrameLimitStrategy strategy)
    {
        bool restore;
        lock (_gate)
        {
            if (_strategy == strategy)
            {
                return;
            }

            // Switching to cap-only hands the refresh rate back to the user, so anything this
            // service moved has to go back before it stops being responsible for it.
            restore = strategy is FrameLimitStrategy.FrameLimitOnly;
            _strategy = strategy;
        }

        Log.Info($"Frame limit strategy: {strategy}.");
        if (restore)
        {
            Restore();
        }
    }

    /// <summary>Drops cached discovery, after a display or mode change.</summary>
    internal void Invalidate()
    {
        lock (_gate)
        {
            _accepted = null;
            _advertised = null;
        }
    }

    /// <summary>The frame caps worth offering under the current strategy.</summary>
    /// <returns>Caps, ascending, with zero first for uncapped.</returns>
    internal IReadOnlyList<int> FrameLimitOptions()
    {
        (FrameLimitStrategy strategy, IReadOnlyList<int> advertised, IReadOnlyList<int> accepted) =
            Snapshot();
        return FrameLimitPairing.FrameLimitOptions(strategy, advertised, accepted);
    }

    /// <summary>The refresh rate a cap would be presented at, without applying anything.</summary>
    /// <param name="capFps">The frame cap being considered.</param>
    /// <returns>The paired rate, or null when the refresh rate would be left alone.</returns>
    /// <remarks>
    /// The read-only half of <see cref="ApplyForCap"/>, for labelling a cap the user is still
    /// dragging through. Same policy, same snapshot, no display call.
    /// </remarks>
    internal int? SelectRefreshHz(int capFps)
    {
        (FrameLimitStrategy strategy, IReadOnlyList<int> advertised, IReadOnlyList<int> accepted) =
            Snapshot();
        return FrameLimitPairing.SelectRefreshHz(strategy, capFps, advertised, accepted);
    }

    /// <summary>
    /// Applies the refresh rate paired with a frame cap.
    /// </summary>
    /// <param name="capFps">The frame cap in force; zero or negative means uncapped.</param>
    /// <returns>The rate applied, or null when the refresh rate was left alone.</returns>
    internal int? ApplyForCap(int capFps)
    {
        (FrameLimitStrategy strategy, IReadOnlyList<int> advertised, IReadOnlyList<int> accepted) =
            Snapshot();
        if (strategy is FrameLimitStrategy.FrameLimitOnly)
        {
            return null;
        }

        int? target = FrameLimitPairing.SelectRefreshHz(strategy, capFps, advertised, accepted);
        if (target is not { } rate)
        {
            Log.Info(
                $"Frame limit {capFps}: no exact-cadence mode among [{string.Join(",", accepted)}]; "
                + "refresh left alone.");
            return null;
        }

        CaptureOriginal();
        return _applyRate(rate) ? rate : null;
    }

    /// <summary>
    /// Puts back the refresh rate found before this service moved it.
    /// </summary>
    /// <returns><see langword="true"/> when nothing was left changed.</returns>
    /// <remarks>
    /// Applying is transient rather than persisted, so an abrupt exit already self-heals. This
    /// exists for the ordinary case, where leaving the desktop at 48 Hz after a game closes would
    /// be a change the user never asked for and would have to hunt for.
    /// </remarks>
    internal bool Restore()
    {
        int? original;
        lock (_gate)
        {
            original = _originalRate;
        }

        if (original is not { } rate)
        {
            return true;
        }

        Log.Info($"Frame limit strategy released the display; restoring {rate} Hz.");
        bool restored = _applyRate(rate);
        if (restored)
        {
            lock (_gate)
            {
                if (_originalRate == rate)
                {
                    _originalRate = null;
                }
            }
        }
        else
        {
            Log.Warn($"Frame limit strategy could not restore {rate} Hz; the snapshot was retained.");
        }
        return restored;
    }

    private void CaptureOriginal()
    {
        lock (_gate)
        {
            if (_originalRate is not null)
            {
                return;
            }
        }

        // Read outside the lock: it crosses into the display driver, and the only cost of a race
        // here is capturing the same rate twice.
        int? current = _readCurrentRate();
        lock (_gate)
        {
            _originalRate ??= current;
        }
    }

    private (FrameLimitStrategy, IReadOnlyList<int>, IReadOnlyList<int>) Snapshot()
    {
        FrameLimitStrategy strategy;
        IReadOnlyList<int>? accepted;
        IReadOnlyList<int>? advertised;
        lock (_gate)
        {
            strategy = _strategy;
            accepted = _accepted;
            advertised = _advertised;
        }

        // Discovery runs outside the lock because each candidate rate costs a driver round trip,
        // and holding the gate across that would block every caller behind it.
        accepted ??= _readAcceptedRates();
        advertised ??= _readAdvertisedRates();
        lock (_gate)
        {
            _accepted ??= accepted;
            _advertised ??= advertised;
        }

        return (strategy, advertised, accepted);
    }
}
