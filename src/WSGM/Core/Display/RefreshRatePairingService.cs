using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     Applies the refresh rate that goes with the frame cap in force, under the user's strategy.
/// </summary>
/// <remarks>
///     The pairing decision itself is <see cref="FrameLimitPairing" /> and stays pure; this owns the
///     parts that touch the machine — discovering which rates apply, caching that, applying a rate,
///     and putting the original back.
///     <para>
///         One decision, per display, says how a rate is applied. Where a graphics driver can hold the
///         display at any rate in a range (<see cref="IFixedRefreshRateDriver" />, Intel's Arc Sync), every
///         whole rate in that range applies, through the driver, and the Windows mode is left alone apart
///         from being raised when it sits below the requested rate. Everywhere else only the rates Windows
///         lists for the current mode apply, through a Windows mode change. Manual writes, Valve's row and
///         the frame-limit pairing all go the same way, and the pairing picks only from what applies.
///     </para>
///     <para>
///         Discovery is cached for the current display, resolution and colour depth. A topology or
///         mode change invalidates it. Driver reads are checked against that identity before caching.
///     </para>
/// </remarks>
internal sealed class RefreshRatePairingService
{
    private readonly Lock _discoveryGate = new();

    private readonly IFixedRefreshRateDriver? _driver;
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

    /// <summary>The driver range the cached discovery found, or null when the listed Windows rates apply.</summary>
    private DriverRefreshRange? _driverRange;

    /// <summary>The display and rate the driver holds for this service, and whether the pairing set it.</summary>
    private (DisplayTargetIdentity Target, int Rate, bool Paired)? _fixed;

    private DisplayOperatingPoint? _operatingPoint;
    private bool _operatingPointKnown;
    private long _operatingPointRevision;
    private int _pairedFrameLimit = -1;
    private long _pairedOperatingPoint = -1;
    private FrameLimitStrategy _strategy = FrameLimitStrategy.FrameLimitOnly;

    /// <summary>Creates the service over the real display.</summary>
    /// <param name="driver">The graphics driver that can hold a rate, or null when none can.</param>
    internal RefreshRatePairingService(IFixedRefreshRateDriver? driver)
        : this(
            PrimaryDisplayModes.EnumerateRefreshRates,
            PrimaryDisplayModes.ReadAdvertisedRefreshRates,
            PrimaryDisplayModes.ReadPrimaryOperatingPoint,
            PrimaryDisplayModes.TryRestoreRefreshRate,
            target => DisplayModes.ReadCurrent(target)?.RefreshHz,
            driver)
    {
    }

    /// <summary>Creates the service over supplied display operations, for tests.</summary>
    /// <param name="readAcceptedRates">The rates Windows lists for the current mode.</param>
    /// <param name="readAdvertisedRates">Rates the panel itself advertises.</param>
    /// <param name="readOperatingPoint">Reads display identity and dimensions.</param>
    /// <param name="restoreTarget">Applies a Windows mode rate to the captured target.</param>
    /// <param name="readTargetRate">Reads the Windows mode rate from the captured target.</param>
    /// <param name="driver">The graphics driver that can hold a rate, or null when none can.</param>
    internal RefreshRatePairingService(
        Func<IReadOnlyList<int>> readAcceptedRates,
        Func<IReadOnlyList<int>> readAdvertisedRates,
        Func<DisplayOperatingPoint?> readOperatingPoint,
        Func<DisplayTargetIdentity, int, bool> restoreTarget,
        Func<DisplayTargetIdentity, int?> readTargetRate,
        IFixedRefreshRateDriver? driver = null
    )
    {
        _driver = driver;
        if (driver is not null)
        {
            // The driver answers from what it last read and reads again in the background; a different
            // answer is a different set of rates, discovered again and published.
            driver.RangeChanged += () =>
            {
                Invalidate();
                Changed?.Invoke();
            };
        }

        _readAcceptedRates = readAcceptedRates;
        _readAdvertisedRates = readAdvertisedRates;
        _readOperatingPoint = readOperatingPoint ?? throw new ArgumentNullException(nameof(readOperatingPoint));
        _restoreTarget = restoreTarget ?? throw new ArgumentNullException(nameof(restoreTarget));
        _readTargetRate = readTargetRate ?? throw new ArgumentNullException(nameof(readTargetRate));
    }

    /// <summary>
    ///     Raised, on whichever thread finished the work, when what the refresh rows show may have changed: a
    ///     manual write was answered, a driver write or release completed, or the driver's range moved.
    /// </summary>
    /// <remarks>
    ///     Raised even when the display already ran at the rate, so Valve's row, which shows the value the
    ///     user dragged to until it hears otherwise, snaps back to the rate in force.
    /// </remarks>
    internal event Action? Changed;

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
            _driverRange = null;
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

    /// <summary>The rates that apply on the current display, discovered once and shared by every consumer.</summary>
    /// <returns>
    ///     Every whole rate in the driver's range where a driver can hold one, else the rates Windows lists for
    ///     the current mode; ascending. Empty when the display cannot be read.
    /// </returns>
    /// <remarks>
    ///     Never a mode test: the list is the same before and after a resume, because a panel still waking
    ///     refused every Windows mode but its current one and that answer used to shrink the list.
    /// </remarks>
    internal IReadOnlyList<int> AcceptedRates()
    {
        return Discover()?.Rates ?? [];
    }

    /// <summary>The refresh rate the display presents now: a rate the driver holds, else the Windows mode's.</summary>
    /// <returns>The rate, or null when the display cannot be read.</returns>
    internal int? CurrentRefreshRate()
    {
        if (Discover() is not { } discovered)
        {
            return null;
        }

        lock (_gate)
        {
            if (discovered.Driver is not null && _fixed is { } held
                                              && string.Equals(held.Target.DevicePath,
                                                  discovered.Point.Target.DevicePath,
                                                  StringComparison.OrdinalIgnoreCase))
            {
                return held.Rate;
            }
        }

        return _readTargetRate(discovered.Point.Target);
    }

    private (DisplayOperatingPoint Point, IReadOnlyList<int> Rates, DriverRefreshRange? Driver)? Discover()
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

                if (_accepted is not null)
                {
                    return (_operatingPoint, _accepted, _driverRange);
                }

                revision = _operatingPointRevision;
                point = _operatingPoint;
            }

            var listed = _readAcceptedRates();
            // Never above the fastest Windows mode: the panel cannot run faster than the mode it is driven at,
            // and Arc Sync reports a range one hertz wider than the panel's modes (48-121 Hz on the Atlas).
            var ceiling = listed.Count > 0 ? listed.Max() : int.MaxValue;
            var range = _driver?.ReadRange(point.Target) is { } read
                        && Math.Min(read.MaximumHz, ceiling) > read.MinimumHz
                ? read with { MaximumHz = Math.Min(read.MaximumHz, ceiling) }
                : null;
            var rates = range is null
                ? listed
                : [.. Enumerable.Range(range.MinimumHz, range.MaximumHz - range.MinimumHz + 1)];
            if (range is not null)
            {
                Log.Info($"Display modes: the graphics driver holds any rate in {range.MinimumHz}-{range.MaximumHz} Hz"
                         + (range.FixedHz is { } held ? $", {held} Hz now." : "."));
            }

            RefreshOperatingPoint(true);
            lock (_gate)
            {
                if (revision == _operatingPointRevision)
                {
                    _accepted ??= rates;
                    _driverRange = range;
                    // The driver's own answer wins over what this service last wrote: an Arc Sync row the
                    // user moved since hands the profile back, and nothing is fixed any more.
                    _fixed = range?.FixedHz is { } fixedHz
                        ? (point.Target, fixedHz, _fixed?.Paired ?? false)
                        : range is null
                            ? _fixed
                            : null;
                    return (point, _accepted, _driverRange);
                }
            }
        }

        return null;
    }

    /// <summary>The listed rate nearest to a requested one; the higher of two equally near.</summary>
    /// <param name="rates">The listed rates.</param>
    /// <param name="refreshHz">The requested rate.</param>
    /// <returns>The nearest rate, or null when none is listed.</returns>
    internal static int? NearestRate(IReadOnlyList<int> rates, int refreshHz)
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

    /// <summary>Applies a refresh rate the user chose by hand.</summary>
    /// <param name="refreshHz">The chosen rate.</param>
    /// <param name="capFps">The frame cap in force; zero or negative means uncapped.</param>
    /// <returns>Whether the display is now at that rate.</returns>
    /// <remarks>
    ///     Checked against what applies, not passed straight to the driver: the value arrives from injected
    ///     JavaScript. Where only listed Windows rates apply, Valve's row still offers every whole rate between
    ///     its bounds, so a rate between two listed ones goes to the nearest listed rate, and the next
    ///     publication shows that rate on the row. Where a driver holds the rate, any rate in its range
    ///     applies, and the Windows mode's own rate hands the display back to the driver's profile. A
    ///     manual write is user-owned, so no original is captured and the pairing never restores it.
    /// </remarks>
    internal bool TryApplyManual(int refreshHz, int capFps)
    {
        try
        {
            return ApplyManual(refreshHz, capFps);
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    private bool ApplyManual(int refreshHz, int capFps)
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

        RefreshOperatingPoint(true);
        var revision = OperatingPointRevision;
        var discovered = Discover();
        if (revision != OperatingPointRevision || discovered is not { } current)
        {
            return false;
        }

        if (current.Driver is { } range)
        {
            if (refreshHz < range.MinimumHz || refreshHz > range.MaximumHz)
            {
                Log.Warn($"Manual refresh rate {refreshHz} Hz refused: the driver holds "
                         + $"{range.MinimumHz}-{range.MaximumHz} Hz.");
                return false;
            }

            // ApplyRate hands the Windows mode's own rate back to the driver's profile.
            return ApplyRate(refreshHz, revision, false);
        }

        if (NearestRate(current.Rates, refreshHz) is not { } listed)
        {
            Log.Warn($"Manual refresh rate {refreshHz} Hz refused: Windows lists no refresh rate for this mode.");
            return false;
        }

        if (listed != refreshHz)
        {
            Log.Info($"Manual refresh rate {refreshHz} Hz applies as {listed} Hz, the nearest listed rate "
                     + $"[{string.Join(",", current.Rates)}].");
        }

        return ApplyRate(listed, revision, false);
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
        RefreshOperatingPoint(true);
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

        return ApplyRate(rate, revision, true) ? rate : null;
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

        var complete = ReleaseFixed(true);
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

    /// <summary>Hands a rate the driver holds back to the driver's own profile.</summary>
    /// <param name="pairedOnly">Only a rate the pairing set; a manual one is the user's.</param>
    /// <returns>True: there was nothing to release, or the release is under way and publishes when it ends.</returns>
    private bool ReleaseFixed(bool pairedOnly)
    {
        (DisplayTargetIdentity Target, int Rate, bool Paired) held;
        lock (_gate)
        {
            if (_fixed is not { } current || (pairedOnly && !current.Paired))
            {
                return true;
            }

            held = current;
        }

        if (_driver is not { } driver)
        {
            return true;
        }

        WhenDone(driver.ReleaseAsync(held.Target), released =>
        {
            Log.Info($"Refresh rate {held.Rate} Hz released to the graphics driver's profile: "
                     + (released ? "done." : "the driver refused; it stays until the next change."));
            if (released)
            {
                lock (_gate)
                {
                    if (_fixed == held)
                    {
                        _fixed = null;
                    }
                }
            }
        });
        return true;
    }

    /// <summary>Finishes a driver write where it completes, then says the refresh rows may have changed.</summary>
    /// <param name="write">The write; one that faulted or was cancelled counts as refused.</param>
    /// <param name="finish">Records the outcome.</param>
    /// <remarks>A write that already completed finishes on the caller's thread, before this returns.</remarks>
    private void WhenDone(Task<bool> write, Action<bool> finish)
    {
        void Complete(Task<bool> done)
        {
            finish(done.Status == TaskStatus.RanToCompletion && done.Result);
            Changed?.Invoke();
        }

        if (write.IsCompleted)
        {
            Complete(write);
            return;
        }

        _ = write.ContinueWith(Complete, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Writes a rate to the display the caller's revision describes.</summary>
    /// <param name="rate">The rate to apply.</param>
    /// <param name="expectedRevision">The revision the caller chose the rate against.</param>
    /// <param name="captureOriginal">Whether the pairing owns the write, so it records what to restore.</param>
    /// <returns>
    ///     Whether the write landed, or for a driver-held rate whether it was sent; false when the display
    ///     changed since the caller looked.
    /// </returns>
    /// <remarks>
    ///     The one forced re-read before the write: the caller read the display once at its start, and
    ///     a change since then, notified or not, fails the revision check here. A driver-held rate is
    ///     written through the driver, off the caller's thread, and recorded once the driver answered; a
    ///     Windows mode below that rate is raised to the highest listed one first, since the panel cannot
    ///     run faster than its mode.
    /// </remarks>
    private bool ApplyRate(int rate, long expectedRevision, bool captureOriginal)
    {
        RefreshOperatingPoint(true);
        DisplayOperatingPoint? point;
        DriverRefreshRange? driver;
        lock (_gate)
        {
            if (expectedRevision != _operatingPointRevision)
            {
                return false;
            }

            point = _operatingPoint;
            driver = _driverRange;
        }

        if (point is null)
        {
            return false;
        }

        if (driver is null || _driver is not { } fixedRate)
        {
            if (captureOriginal)
            {
                CaptureOriginal(point);
            }

            return _restoreTarget(point.Target, rate);
        }

        // The Windows mode's own rate needs no fixed rate: the driver's profile already runs there and keeps
        // its variable range, so an idle desktop can still drop below it. Pinning it held the Atlas panel at
        // a constant 120 Hz under the default 120 FPS cap.
        var modeRate = _readTargetRate(point.Target);
        if (modeRate == rate)
        {
            return ReleaseFixed(false);
        }

        if (modeRate is { } mode && mode < rate
                                                      && _readAcceptedRates().DefaultIfEmpty(0).Max() is var top
                                                      && top > mode)
        {
            if (captureOriginal)
            {
                CaptureOriginal(point);
            }

            _ = _restoreTarget(point.Target, top);
        }

        var target = point.Target;
        WhenDone(fixedRate.ApplyAsync(target, rate), applied =>
        {
            if (!applied)
            {
                Log.Warn($"Refresh rate {rate} Hz was not applied by the graphics driver; nothing is retried.");
                return;
            }

            lock (_gate)
            {
                // A manual write takes over a rate the pairing set, and the pairing keeps one the user set
                // only until its own next write.
                _fixed = (target, rate, captureOriginal);
            }
        });
        return true;
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
                RefreshOperatingPoint(true);
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
