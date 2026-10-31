using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     Applies the refresh rate that goes with the frame cap in force, under the user's strategy.
/// </summary>
/// <remarks>
///     The pairing decision itself is <see cref="FrameLimitPairing" /> and stays pure; this owns the
///     parts that touch the machine — discovering what the display accepts, caching that, applying a
///     rate, and putting the original back.
///     <para>
///         Discovery is cached for the current display, resolution and colour depth. A topology or
///         mode change invalidates it. Driver reads are checked against that identity before caching.
///     </para>
/// </remarks>
internal sealed class RefreshRatePairingService
{
    private readonly Lock _discoveryGate = new();
    private readonly Lock _gate = new();

    private readonly Dictionary<string, (DisplayTargetIdentity Target, int Rate)> _originals =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<IReadOnlyList<int>> _readAcceptedRates;
    private readonly Func<IReadOnlyList<int>> _readAdvertisedRates;
    private readonly Func<DisplayOperatingPoint?> _readOperatingPoint;
    private readonly Func<DisplayTargetIdentity, int?> _readTargetRate;
    private readonly Func<DisplayTargetIdentity, int, bool> _restoreTarget;

    private IReadOnlyList<int>? _accepted;
    private IReadOnlyList<int>? _advertised;
    private DisplayOperatingPoint? _operatingPoint;
    private bool _operatingPointKnown;
    private long _operatingPointRevision;
    private int _pairedFrameLimit = -1;
    private long _pairedOperatingPoint = -1;
    private FrameLimitStrategy _strategy = FrameLimitStrategy.FrameLimitOnly;

    /// <summary>Creates the service over the real display.</summary>
    internal RefreshRatePairingService()
        : this(
            PrimaryDisplayModes.EnumerateAcceptedRefreshRates,
            PrimaryDisplayModes.ReadAdvertisedRefreshRates,
            PrimaryDisplayModes.ReadPrimaryOperatingPoint,
            PrimaryDisplayModes.TryRestoreRefreshRate,
            target => DisplayModes.Read(target)?.Current.RefreshHz)
    {
    }

    /// <summary>Creates the service over supplied display operations, for tests.</summary>
    /// <param name="readAcceptedRates">Every rate the driver accepts.</param>
    /// <param name="readAdvertisedRates">Rates the panel itself advertises.</param>
    /// <param name="readOperatingPoint">Reads display identity and dimensions.</param>
    /// <param name="restoreTarget">Applies a rate to the captured target.</param>
    /// <param name="readTargetRate">Reads the rate from the captured target.</param>
    internal RefreshRatePairingService(
        Func<IReadOnlyList<int>> readAcceptedRates,
        Func<IReadOnlyList<int>> readAdvertisedRates,
        Func<DisplayOperatingPoint?> readOperatingPoint,
        Func<DisplayTargetIdentity, int, bool> restoreTarget,
        Func<DisplayTargetIdentity, int?> readTargetRate
    )
    {
        _readAcceptedRates = readAcceptedRates;
        _readAdvertisedRates = readAdvertisedRates;
        _readOperatingPoint = readOperatingPoint ?? throw new ArgumentNullException(nameof(readOperatingPoint));
        _restoreTarget = restoreTarget ?? throw new ArgumentNullException(nameof(restoreTarget));
        _readTargetRate = readTargetRate ?? throw new ArgumentNullException(nameof(readTargetRate));
    }

    /// <summary>Gets the cached display revision, discovering it only after invalidation.</summary>
    internal long OperatingPointRevision
    {
        get
        {
            RefreshOperatingPoint();
            lock (_gate)
            {
                return _operatingPointRevision;
            }
        }
    }

    /// <summary>Invalidates discovery after a display notification or resume, without querying the driver.</summary>
    internal void Invalidate()
    {
        lock (_gate)
        {
            _accepted = null;
            _advertised = null;
            _operatingPoint = null;
            _operatingPointKnown = false;
            _operatingPointRevision++;
        }
    }

    /// <summary>
    ///     Adopts a strategy, restoring the display first when the new one no longer owns it.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <returns>True when the strategy changed, independent of whether any requested restoration succeeded.</returns>
    internal bool SetStrategy(FrameLimitStrategy strategy)
    {
        bool restore;
        lock (_gate)
        {
            if (_strategy == strategy)
            {
                return false;
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

        return true;
    }

    /// <summary>The rates the driver accepts, discovered once and shared by every consumer.</summary>
    /// <returns>Accepted rates, ascending. Empty when the display cannot be read.</returns>
    internal IReadOnlyList<int> AcceptedRates()
    {
        using var discovery = _discoveryGate.EnterScope();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RefreshOperatingPoint();
            long revision;
            lock (_gate)
            {
                if (_operatingPoint is null)
                {
                    return [];
                }

                if (_accepted is not null)
                {
                    return _accepted;
                }

                revision = _operatingPointRevision;
            }

            var accepted = _readAcceptedRates();
            RefreshOperatingPoint(force: true);
            lock (_gate)
            {
                if (revision == _operatingPointRevision)
                {
                    return _accepted ??= accepted;
                }
            }
        }

        return [];
    }

    /// <summary>Applies a refresh rate the user chose by hand.</summary>
    /// <param name="refreshHz">The chosen rate.</param>
    /// <param name="capFps">The frame cap in force; zero or negative means uncapped.</param>
    /// <returns>Whether the display is now at that rate.</returns>
    /// <remarks>
    ///     Checked against the rates discovery accepted, not passed straight to the driver: the value
    ///     arrives from injected JavaScript, and a rate the panel cannot show is a black screen. A
    ///     manual write is user-owned, so no original is captured and nothing restores it later.
    /// </remarks>
    internal bool TryApplyManual(int refreshHz, int capFps)
    {
        FrameLimitStrategy strategy;
        lock (_gate)
        {
            strategy = _strategy;
        }

        // A pairing strategy owns the refresh rate only while there is a CAP for it to own one
        // against. With the frame limit off there is no cadence to pair to and the unified row's
        // slider becomes the rate itself, so refusing there rejected the very writes that row
        // exists to make — "Manual refresh rate 72 Hz refused" against a strategy that was, at that
        // moment, pairing nothing.
        if (capFps > 0 && !FrameLimitPairing.RefreshRateIsUserOwned(strategy))
        {
            Log.Warn(
                $"Manual refresh rate {refreshHz} Hz refused: the "
                + $"{strategy} strategy owns the refresh rate while a "
                + $"{capFps} FPS cap is set.");
            return false;
        }

        RefreshOperatingPoint(force: true);
        var revision = OperatingPointRevision;
        var accepted = AcceptedRates();
        if (revision != OperatingPointRevision)
        {
            return false;
        }

        if (accepted.Contains(refreshHz))
        {
            return ApplyRate(refreshHz, revision, captureOriginal: false);
        }

        Log.Warn(
            $"Manual refresh rate {refreshHz} Hz refused: accepted rates are "
            + $"[{string.Join(",", accepted)}].");
        return false;
    }

    /// <summary>The frame caps worth offering under the current strategy, and the rate each is presented at.</summary>
    /// <returns>
    ///     Caps, ascending, with zero first for uncapped; and the paired refresh rate for every cap that
    ///     moves the display, empty when none does. Both come from one discovery snapshot.
    /// </returns>
    /// <remarks>
    ///     The paired rates are the read-only half of <see cref="ApplyForCap" />, for labelling a cap the
    ///     user is still dragging through. Same policy, same snapshot, no display call.
    /// </remarks>
    internal (IReadOnlyList<int> Options, IReadOnlyDictionary<int, int> PairedRates) FrameLimitChoices()
    {
        var (strategy, advertised, accepted) =
            Snapshot();
        var options = FrameLimitPairing.FrameLimitOptions(strategy, advertised, accepted);
        Dictionary<int, int> paired = [];
        foreach (var cap in options)
        {
            if (cap > 0 && FrameLimitPairing.SelectRefreshHz(strategy, cap, advertised, accepted) is { } hz)
            {
                paired[cap] = hz;
            }
        }

        return (options, paired);
    }

    /// <summary>The two ends of the cap range this panel can hold.</summary>
    /// <returns>The inclusive bounds, or null when no rate is high enough to carry a cap.</returns>
    /// <remarks>
    ///     Every surface that offers a frame limit asks this, so the overlay's slider and the Quick
    ///     Access row cannot disagree about what a legal cap is. They did: the overlay ran from RTSS's
    ///     own floor of zero and let a 12 FPS cap be set, which the Quick Access row then refused to
    ///     render at all because it bookends the slider here (Claw, 2026-09-03).
    /// </remarks>
    internal (int Minimum, int Maximum)? FrameLimitRange()
    {
        var (strategy, advertised, accepted) =
            Snapshot();
        return FrameLimitPairing.FrameLimitRange(strategy, advertised, accepted);
    }

    /// <summary>
    ///     Applies the refresh rate paired with a frame cap.
    /// </summary>
    /// <param name="capFps">The frame cap in force; zero or negative means uncapped.</param>
    /// <returns>The rate applied, or null when the refresh rate was left alone.</returns>
    internal int? ApplyForCap(int capFps)
    {
        RefreshOperatingPoint(force: true);
        var revision = OperatingPointRevision;
        var (strategy, advertised, accepted) =
            Snapshot();
        if (strategy is FrameLimitStrategy.FrameLimitOnly)
        {
            return null;
        }

        var target = FrameLimitPairing.SelectRefreshHz(strategy, capFps, advertised, accepted);
        if (target is not { } rate)
        {
            Log.Info(
                $"Frame limit {capFps}: no exact-cadence mode among [{string.Join(",", accepted)}]; "
                + "refresh left alone.");
            return null;
        }

        return ApplyRate(rate, revision, captureOriginal: true) ? rate : null;
    }

    /// <summary>
    ///     Puts back the refresh rate found before this service moved it.
    /// </summary>
    /// <returns><see langword="true" /> when nothing was left changed.</returns>
    /// <remarks>
    ///     Originals are retained per target until restoration succeeds. Transient mode changes
    ///     are not automatically undone when this process exits.
    /// </remarks>
    internal bool Restore()
    {
        KeyValuePair<string, (DisplayTargetIdentity Target, int Rate)>[] originals;
        lock (_gate)
        {
            originals = _originals.ToArray();
        }

        var complete = true;
        foreach (var original in originals)
        {
            var restored = _restoreTarget(original.Value.Target, original.Value.Rate);
            lock (_gate)
            {
                if (restored && _originals.TryGetValue(original.Key, out var current) && current == original.Value)
                {
                    _originals.Remove(original.Key);
                }
            }

            complete &= restored;
        }

        return complete;
    }

    /// <summary>Pairs the display cadence to a frame limit, unless that pairing is already held.</summary>
    /// <param name="limit">The desired frame limit, or zero for uncapped.</param>
    /// <param name="force">
    ///     Re-applies the pairing for the limit already latched. Set after a resume: the limit did not
    ///     change, but the panel behind it was re-established by the driver and may have come back at
    ///     its default rate, which the latch would otherwise hide until the user moved the cap twice.
    /// </param>
    /// <remarks>The latch is owned by the session's UI-thread callers.</remarks>
    internal void Pair(int limit, bool force)
    {
        var point = OperatingPointRevision;
        if (limit == _pairedFrameLimit && point == _pairedOperatingPoint && !force)
        {
            return;
        }

        _pairedFrameLimit = limit;
        _pairedOperatingPoint = point;

        // Uncapped hands the display back: there is no cadence left to pair against, and holding a
        // reduced refresh rate after the cap is gone would cap frames by the back door. A forced
        // pass is the exception: with no cap there is no WSGM-driven rate to undo, and restoring
        // would overwrite a rate the user set by hand before the sleep.
        if (limit <= 0)
        {
            if (!force)
            {
                _ = Restore();
            }

            return;
        }

        _ = ApplyForCap(limit);
    }

    /// <summary>Records the target's current rate the first time this service moves it.</summary>
    /// <param name="point">The operating point the write is about to address.</param>
    private void CaptureOriginal(DisplayOperatingPoint point)
    {
        lock (_gate)
        {
            if (_originals.ContainsKey(point.Target.DevicePath))
            {
                return;
            }
        }

        // Capture from this target, never a primary display that may have changed meanwhile.
        if (_readTargetRate(point.Target) is { } rate)
        {
            lock (_gate)
            {
                _originals.TryAdd(point.Target.DevicePath, (point.Target, rate));
            }
        }
    }

    /// <summary>Writes a rate to the display the caller's revision describes.</summary>
    /// <param name="rate">The rate to apply.</param>
    /// <param name="expectedRevision">The revision the caller chose the rate against.</param>
    /// <param name="captureOriginal">Whether to record the target's rate first, so it can be restored.</param>
    /// <returns>Whether the write landed; false when the display changed since the caller looked.</returns>
    /// <remarks>
    ///     The one forced re-read before the write: the caller read the display once at its start, and
    ///     a change since then, notified or not, fails the revision check here.
    /// </remarks>
    private bool ApplyRate(int rate, long expectedRevision, bool captureOriginal)
    {
        RefreshOperatingPoint(force: true);
        DisplayOperatingPoint? point;
        lock (_gate)
        {
            if (expectedRevision != _operatingPointRevision)
            {
                return false;
            }

            point = _operatingPoint;
        }

        if (point is null)
        {
            return false;
        }

        if (captureOriginal)
        {
            CaptureOriginal(point);
        }

        return _restoreTarget(point.Target, rate);
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
                _accepted = null;
                _advertised = null;
                _operatingPoint = observed;
                _operatingPointRevision++;
            }

            _operatingPointKnown = true;
        }
    }

    private (FrameLimitStrategy, IReadOnlyList<int>, IReadOnlyList<int>) Snapshot()
    {
        using var discovery = _discoveryGate.EnterScope();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RefreshOperatingPoint();
            long revision;
            FrameLimitStrategy strategy;
            IReadOnlyList<int>? advertised;
            lock (_gate)
            {
                revision = _operatingPointRevision;
                strategy = _strategy;
                advertised = _advertised;
                if (_operatingPoint is null)
                {
                    return (strategy, [], []);
                }
            }

            var accepted = AcceptedRates();
            if (advertised is null)
            {
                advertised = _readAdvertisedRates();
                RefreshOperatingPoint(force: true);
            }
            lock (_gate)
            {
                if (revision == _operatingPointRevision && strategy == _strategy)
                {
                    _advertised ??= advertised;
                    return (strategy, _advertised, accepted);
                }
            }
        }

        lock (_gate)
        {
            return (_strategy, [], []);
        }
    }
}
