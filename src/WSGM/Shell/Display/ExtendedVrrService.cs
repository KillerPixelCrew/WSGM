using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using LibHandheld.Contracts;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Owns the extended VRR range of a panel LibHandheld names: the EDID override with the lower floor, Arc Sync
///     Excellent across it, and Intel's low frame rate solution off, as the Atlas8Tweaks VRR Fix does, unless the
///     user keeps it on.
/// </summary>
/// <remarks>
///     Mirrors <c>AcerAtlasVrr.ps1</c> step for step. Turning it on saves the original Intel state once, writes the
///     override and asks for a restart until Windows reports it. While it is in force, every WSGM start waits for
///     the Intel driver and applies Excellent and the chosen low frame rate compensation (off by default, as the
///     script); WSGM starting at sign-in replaces the script's sign-in task. Turning it off removes the override and puts the saved Intel state back. A foreign override or an
///     unknown EDID is refused before anything changes. A standalone install of the script is shown as active and
///     left to its own task; turning the range off here removes it too.
///     <para>
///         Arc Sync keeps one owner. Excellent is written through the driver's policy write, never through a user's
///         Arc Sync row, and is supplied as the profile a released fixed refresh rate returns to, so a WSGM refresh
///         write never leaves the panel without its floor. A write that got no answer is reported and never sent
///         again; turning the range off and on, or the next start, is the retry. Every transition is logged once.
///         A null panel (another device, or Device Integration off) hides the row and changes nothing.
///     </para>
/// </remarks>
internal sealed class ExtendedVrrService : IAsyncDisposable
{
    private const string OffDetail = "Lowers the variable refresh floor from 48 to 28 Hz. Takes effect after a Windows restart.";
    private const string ActiveDetail = "Active: Arc Sync Excellent from 28 to 120 Hz.";
    private const string ApplyDetail = "Restart Windows to apply.";
    private const string RemoveDetail = "Turned off. Restart Windows to finish.";
    private const string ForeignDetail =
        "Another tool's EDID override is in place for this panel. Remove it first (CRU: run reset-all.exe and restart). Nothing was changed.";

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1500);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IExtendedVrrDriver _driver;
    private readonly TimeSpan _driverWait;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IExtendedVrrMachine _machine;
    private readonly Func<ExtendedVrrPanel?> _panel;
    private readonly Func<ExtendedVrrIntelState?> _readSaved;
    private readonly Action<bool> _writeLowFrameRate;
    private readonly Lock _sync = new();
    private readonly Action<ExtendedVrrIntelState?> _writeSaved;
    private Task _check = Task.CompletedTask;
    private string? _lastLogged;
    private volatile bool _lowFrameRate;
    private volatile bool _rangeLoaded;
    private ExtendedVrrStatus _status = ExtendedVrrStatus.Hidden;

    /// <summary>Creates the owner; nothing is read or written until <see cref="Recheck" /> or a change.</summary>
    /// <param name="panel">The panel of the running device, or null with Device Integration off or another device.</param>
    /// <param name="machine">The panel's EDID, its override and the standalone tool.</param>
    /// <param name="driver">The Intel driver calls, ordered and off the caller's thread.</param>
    /// <param name="readSaved">Reads the saved original Intel state.</param>
    /// <param name="writeSaved">Saves or clears the original Intel state.</param>
    /// <param name="driverWait">How long a start waits for the Intel driver to report Arc Sync; 90 s, as the script.</param>
    /// <param name="delay">The wait between driver polls and before a readback; tests replace it.</param>
    /// <param name="lowFrameRate">
    ///     Reads once whether Intel's low frame rate compensation stays on while the range is active; off when omitted.
    /// </param>
    /// <param name="writeLowFrameRate">Saves that choice; nothing is saved when omitted.</param>
    internal ExtendedVrrService(Func<ExtendedVrrPanel?> panel, IExtendedVrrMachine machine, IExtendedVrrDriver driver,
        Func<ExtendedVrrIntelState?> readSaved, Action<ExtendedVrrIntelState?> writeSaved, TimeSpan? driverWait = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<bool>? lowFrameRate = null,
        Action<bool>? writeLowFrameRate = null)
    {
        _panel = panel;
        _machine = machine;
        _driver = driver;
        _readSaved = readSaved;
        _writeSaved = writeSaved;
        _lowFrameRate = lowFrameRate?.Invoke() ?? false;
        _writeLowFrameRate = writeLowFrameRate ?? (_ => { });
        _driverWait = driverWait ?? TimeSpan.FromSeconds(90);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>What both surfaces show.</summary>
    internal ExtendedVrrStatus Status => Volatile.Read(ref _status);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task check;
        lock (_sync)
        {
            check = _check;
        }

        try
        {
            await check.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    /// <summary>Raised off the caller's thread when <see cref="Status" /> changes.</summary>
    internal event Action? Changed;

    /// <summary>Reads the panel again in the background and, while the range is in force, applies its Intel state.</summary>
    /// <remarks>Run at start and whenever the running device changes. Checks queue behind any change in progress.</remarks>
    internal void Recheck()
    {
        lock (_sync)
        {
            var previous = _check;
            _check = Task.Run(async () =>
            {
                // Whatever the previous check did, this one runs after it.
                await Task.WhenAny(previous).ConfigureAwait(false);
                await RunAsync(async token =>
                {
                    await CheckAsync(token).ConfigureAwait(false);
                    return Status with { Busy = false };
                }).ConfigureAwait(false);
            });
        }
    }

    /// <summary>Waits for the background check queued last.</summary>
    internal Task WaitForCheckAsync()
    {
        lock (_sync)
        {
            return _check;
        }
    }

    /// <summary>Turns the extended range on or off; on while already active applies its Intel state again.</summary>
    /// <param name="enabled">The request.</param>
    /// <param name="cancellationToken">Cancels the wait for a change in progress.</param>
    /// <returns>The status afterwards, which explains a refusal or a failure.</returns>
    internal async Task<ExtendedVrrStatus> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await RunAsync(token => enabled ? EnableAsync(token) : DisableAsync(token), linked.Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Saves whether Intel's low frame rate compensation stays on while the range is active and, while it is
    ///     active, applies it at once.
    /// </summary>
    /// <param name="enabled">The request.</param>
    /// <param name="cancellationToken">Cancels the wait for a change in progress.</param>
    /// <returns>The status afterwards, which explains a failure.</returns>
    internal async Task<ExtendedVrrStatus> SetLowFrameRateCompensationAsync(bool enabled,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await RunAsync(async token =>
        {
            _lowFrameRate = enabled;
            _writeLowFrameRate(enabled);
            Log.Info($"Extended VRR range: low frame rate compensation {(enabled ? "on" : "off")} while active.");
            if (Status.Phase is ExtendedVrrPhase.Standalone && _rangeLoaded
                && TakeOverStandalone() is { } problem)
            {
                return Status with { Busy = false, Detail = problem };
            }

            if (Status.Phase is ExtendedVrrPhase.Active or ExtendedVrrPhase.Standalone)
            {
                // The start's own check: it reads the panel again and writes only what differs.
                await CheckAsync(token).ConfigureAwait(false);
            }

            return Status with { Busy = false };
        }, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Turns the range off for the uninstaller when WSGM turned it on, unless the standalone tool owns it.</summary>
    /// <returns>Whether nothing is left to restore, and whether a restart loads the factory EDID.</returns>
    internal async Task<(bool Complete, bool RestartRequired)> RestoreForUninstallAsync()
    {
        if (_readSaved() is null)
        {
            return (true, false);
        }

        if (_machine.ReadStandalone() is not null)
        {
            Log.Info("Uninstall restore: the standalone Atlas8Tweaks VRR Fix owns the extended VRR range; it is left in place.");
            _writeSaved(null);
            return (true, false);
        }

        var status = await RunAsync(DisableAsync, _lifetime.Token).ConfigureAwait(false);
        return (_readSaved() is null, status.Phase is ExtendedVrrPhase.RestartToRemove);
    }

    private async Task<ExtendedVrrStatus> RunAsync(Func<CancellationToken, Task<ExtendedVrrStatus>> work,
        CancellationToken? token = null)
    {
        var cancellation = token ?? _lifetime.Token;
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (Status.Visible)
            {
                Publish(Status with { Busy = true });
            }

            ExtendedVrrStatus status;
            try
            {
                status = await work(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                status = Status with { Busy = false };
            }
            catch (Exception ex)
            {
                Log.Warn($"Extended VRR range: {ex.Message}");
                status = Status with { Busy = false, Detail = "Could not finish: " + ex.Message };
            }

            Publish(status);
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        if (_panel() is not { } panel)
        {
            Publish(ExtendedVrrStatus.Hidden);
            return;
        }

        var (inspection, refusal) = Inspect(panel);
        if (inspection is null)
        {
            Publish(refusal);
            return;
        }

        var arc = await WaitForArcSyncAsync(inspection.Target, token).ConfigureAwait(false);
        if (arc is null)
        {
            LogOnce("Extended VRR range: the Intel driver reports no Arc Sync for the panel; the control is hidden.");
            Publish(ExtendedVrrStatus.Hidden);
            return;
        }

        var lfc = await _driver.ReadSolutionsAsync(inspection.Target).ConfigureAwait(false);
        LogOnce($"Extended VRR range: active EDID {Describe(inspection.Edid.Reported)}, override {inspection.OverrideText}, "
                + $"Intel {Describe(arc)}, LFC {Describe(lfc)}.");
        var phase = PhaseOf(inspection);
        if (phase is ExtendedVrrPhase.Refused)
        {
            Publish(new ExtendedVrrStatus(phase, ForeignDetail));
            return;
        }

        var detail = DetailOf(phase, inspection);
        if (inspection.ActiveOurs && Math.Abs(arc.MonitorMinimumHz - inspection.Edid.Reported!.FloorHz) <= 0.1)
        {
            // Whoever applies the profile, a fixed refresh rate released by WSGM returns to the extended range.
            await _driver.SetBaselineAsync(inspection.Target, Excellent(arc)).ConfigureAwait(false);
        }

        if (phase is ExtendedVrrPhase.Active)
        {
            detail = await RepairAsync(inspection, arc, token).ConfigureAwait(false);
        }

        Publish(new ExtendedVrrStatus(phase, detail));
    }

    private async Task<ExtendedVrrStatus> EnableAsync(CancellationToken token)
    {
        if (_panel() is not { } panel)
        {
            return ExtendedVrrStatus.Hidden;
        }

        var (inspection, refusal) = Inspect(panel);
        if (inspection is null)
        {
            return refusal;
        }

        var phase = PhaseOf(inspection);
        if (phase is ExtendedVrrPhase.Refused)
        {
            return new ExtendedVrrStatus(phase, ForeignDetail);
        }

        if (phase is ExtendedVrrPhase.Standalone)
        {
            return new ExtendedVrrStatus(phase, DetailOf(phase, inspection));
        }

        var arc = await _driver.ReadArcSyncAsync(inspection.Target).ConfigureAwait(false);
        var lfc = await _driver.ReadSolutionsAsync(inspection.Target).ConfigureAwait(false);
        if (arc is null || lfc is null)
        {
            return new ExtendedVrrStatus(phase,
                "Could not read the Intel driver's Arc Sync and low frame rate state. Nothing was changed.");
        }

        var floor = panel.FloorHz;
        Log.Info($"Extended VRR range: turning on {floor}-{panel.MaximumHz} Hz. Active EDID "
                 + $"{Describe(inspection.Edid.Reported)}, Intel {Describe(arc)}, LFC {Describe(lfc)}.");

        // Save the original Intel state once. With an extended EDID already written or in force the current
        // state is not the original, so the measured factory defaults are saved instead.
        if (_readSaved() is null)
        {
            var original = inspection.Override is OverrideKind.None && inspection.Edid.Reported is null
                ? Measured(arc.FixedHz is null ? arc.Profile : arc.BeforeFixed ?? arc.Profile, lfc)
                : Factory(panel);
            _writeSaved(original);
            Log.Info($"Extended VRR range: saved the original Intel state ({original.Source}).");
        }

        var variant = panel.CreateEdid(inspection.Edid.Physical!, floor);
        var hash = ExtendedVrrPanel.Sha256(variant);
        if (inspection.OverrideSha256 != hash)
        {
            _machine.WriteOverride(inspection.Target, variant);
            if (_machine.ReadOverride(inspection.Target).Edid is not { } written || ExtendedVrrPanel.Sha256(written) != hash)
            {
                Log.Warn("Extended VRR range: the EDID override read-back does not match.");
                return new ExtendedVrrStatus(ExtendedVrrPhase.RestartToApply,
                    "Could not confirm the EDID override; its read-back does not match.");
            }

            Log.Info($"Extended VRR range: EDID override {floor}-{panel.MaximumHz} Hz written ({hash}).");
        }
        else
        {
            Log.Info($"Extended VRR range: EDID override {floor}-{panel.MaximumHz} Hz already present.");
        }

        if (inspection.Edid.ReportedSha256 != hash)
        {
            Log.Info($"Extended VRR range: {floor}-{panel.MaximumHz} Hz becomes active after a restart.");
            return new ExtendedVrrStatus(ExtendedVrrPhase.RestartToApply, ApplyDetail);
        }

        await _driver.SetBaselineAsync(inspection.Target, Excellent(arc)).ConfigureAwait(false);
        return new ExtendedVrrStatus(ExtendedVrrPhase.Active,
            await RepairAsync(inspection, arc, token).ConfigureAwait(false));
    }

    private async Task<ExtendedVrrStatus> DisableAsync(CancellationToken token)
    {
        if (_panel() is not { } panel)
        {
            return ExtendedVrrStatus.Hidden;
        }

        var (inspection, refusal) = Inspect(panel);
        if (inspection is null)
        {
            return refusal;
        }

        var target = inspection.Target;
        List<string> problems = [];
        if (inspection.Standalone is { } standalone)
        {
            // Its saved state is the original; keep it before its folder goes.
            if (standalone.Original is { } standaloneOriginal && _readSaved() is null)
            {
                _writeSaved(standaloneOriginal);
            }

            if (_machine.RemoveStandalone() is { } problem)
            {
                Log.Warn($"Extended VRR range: the standalone Atlas8Tweaks VRR Fix was not fully removed: {problem}");
                problems.Add(problem);
            }
            else
            {
                Log.Info("Extended VRR range: removed the standalone Atlas8Tweaks VRR Fix and its sign-in task.");
            }
        }

        switch (inspection.Override)
        {
            case OverrideKind.Ours:
                _machine.RemoveOverride(target);
                Log.Info("Extended VRR range: EDID override removed.");
                break;
            case OverrideKind.Foreign:
                Log.Warn("Extended VRR range: an EDID override from another tool exists; it was left untouched.");
                break;
        }

        var restart = inspection.Edid.Reported is not null;
        var original = _readSaved();
        if (original is null)
        {
            original = Factory(panel);
            Log.Warn("Extended VRR range: no saved original state found; restoring the factory defaults.");
        }

        if (await RestoreIntelAsync(target, original, token).ConfigureAwait(false) is { } intelProblem)
        {
            problems.Add(intelProblem);
        }
        else
        {
            _writeSaved(null);
        }

        var phase = restart ? ExtendedVrrPhase.RestartToRemove : ExtendedVrrPhase.Off;
        var detail = problems.Count > 0
            ? "Could not restore everything: " + string.Join(" ", problems) + " Turn it off again to retry."
            : inspection.Standalone is not null
                ? "Turned off and removed the standalone Atlas8Tweaks VRR Fix." + (restart ? " Restart Windows to finish." : "")
                : restart
                    ? RemoveDetail
                    : OffDetail;
        return new ExtendedVrrStatus(phase, detail);
    }

/// <summary>
    ///     Hands a loaded standalone range to WSGM: the tool's sign-in task and folder go, its EDID override stays, so
    ///     the range stays active without a restart and WSGM applies the Intel state from then on.
    /// </summary>
    /// <returns>Null when WSGM owns the range now, otherwise why not.</returns>
    private string? TakeOverStandalone()
    {
        if (_machine.ReadStandalone() is not { } standalone)
        {
            return null;
        }

        // Its saved state is the original the range returns to when it is switched off.
        if (standalone.Original is { } original && _readSaved() is null)
        {
            _writeSaved(original);
        }

        if (_machine.RemoveStandalone() is { } problem)
        {
            Log.Warn($"Extended VRR range: the standalone Atlas8Tweaks VRR Fix was not fully removed: {problem}");
            return "Could not take over from the standalone Atlas8Tweaks VRR Fix: " + problem;
        }

        Log.Info("Extended VRR range: took over from the standalone Atlas8Tweaks VRR Fix; its EDID override stays.");
        return null;
    }

    /// <summary>Puts the saved Arc Sync profile and frame rate solutions back; null when everything is back.</summary>
    private async Task<string?> RestoreIntelAsync(DisplayTargetIdentity target, ExtendedVrrIntelState original,
        CancellationToken token)
    {
        var arc = await WaitForArcSyncAsync(target, token).ConfigureAwait(false);
        if (arc is null)
        {
            Log.Warn("Extended VRR range: the Intel driver reports no Arc Sync; the saved Intel state is kept for later.");
            return "The Intel driver did not answer.";
        }

        var profile = new GpuArcSyncBaseline((GpuArcSyncProfile)original.ProfileId, original.MinimumHz,
            original.MaximumHz, original.MaxIncreaseUs, original.MaxDecreaseUs);
        if (arc.FixedHz is not null)
        {
            // A fixed refresh rate holds the display; its release writes the original profile.
            await _driver.SetBaselineAsync(target, profile).ConfigureAwait(false);
            Log.Info($"Extended VRR range: Intel profile {Describe(profile)} returns when the fixed refresh rate is released.");
        }
        else
        {
            await _driver.SetBaselineAsync(target, null).ConfigureAwait(false);
            var write = await _driver.SetProfileAsync(target, profile).ConfigureAwait(false);
            if (write is not { Applied: true })
            {
                Log.Warn($"Extended VRR range: Intel profile {Describe(profile)} not restored: {write?.Detail ?? "no answer"}.");
                return write is null
                    ? "The Intel driver did not answer the Arc Sync write."
                    : "Intel refused the original Arc Sync profile.";
            }

            await _delay(Settle, token).ConfigureAwait(false);
            var after = await _driver.ReadArcSyncAsync(target).ConfigureAwait(false);
            if (after is null || (int)after.Profile.Profile != original.ProfileId)
            {
                Log.Warn($"Extended VRR range: Intel kept {Describe(after)} instead of profile {original.ProfileId}.");
                return "Intel kept another Arc Sync profile.";
            }

            Log.Info($"Extended VRR range: Intel profile restored: {Describe(after.Profile)}.");
        }

        var lfc = await _driver.ReadSolutionsAsync(target).ConfigureAwait(false);
        if (lfc is null)
        {
            return "Intel's frame rate solutions could not be read.";
        }

        if (lfc.LowFrameRate != original.LowFpsSolutionEnabled
            && await SetSolutionAsync(target, GpuVrrSolution.LowFrameRate, original.LowFpsSolutionEnabled)
                .ConfigureAwait(false) is { } low)
        {
            return low;
        }

        if (lfc.HighFrameRate != original.HighFpsSolutionEnabled
            && await SetSolutionAsync(target, GpuVrrSolution.HighFrameRate, original.HighFpsSolutionEnabled)
                .ConfigureAwait(false) is { } high)
        {
            return high;
        }

        Log.Info($"Extended VRR range: LFC restored: low={original.LowFpsSolutionEnabled} high={original.HighFpsSolutionEnabled}.");
        return null;
    }

    /// <summary>The script's Repair: Excellent across the floor, then LFC off, once each, never retried.</summary>
    private async Task<string> RepairAsync(Inspection inspection, GpuArcSyncState arc, CancellationToken token)
    {
        var target = inspection.Target;
        var floor = inspection.Edid.Reported!.FloorHz;
        if (Math.Abs(arc.MonitorMinimumHz - floor) > 0.1)
        {
            Log.Warn($"Extended VRR range: SKIP Intel reports a {Hz(arc.MonitorMinimumHz)}-{Hz(arc.MonitorMaximumHz)} Hz "
                     + $"panel range, expected floor {floor}. Nothing changed.");
            return $"Intel reports {Hz(arc.MonitorMinimumHz)}-{Hz(arc.MonitorMaximumHz)} Hz, not the {floor} Hz floor. "
                   + "Nothing was changed.";
        }

        List<string> changes = [];
        if (arc.FixedHz is not null)
        {
            Log.Info($"Extended VRR range: a fixed refresh rate of {Hz(arc.FixedHz.Value)} Hz holds the panel; "
                     + "Excellent returns with its release.");
        }
        else if (!IsExcellentAcross(arc.Profile, floor))
        {
            var before = Describe(arc.Profile);
            var write = await _driver.SetProfileAsync(target, Excellent(arc)).ConfigureAwait(false);
            if (write is not { Applied: true })
            {
                var reason = write?.Detail ?? "the driver did not answer";
                Log.Warn($"Extended VRR range: Arc Sync Excellent not applied ({reason}); it is not sent again.");
                return "Could not apply Arc Sync Excellent: " + reason;
            }

            await _delay(Settle, token).ConfigureAwait(false);
            var after = await _driver.ReadArcSyncAsync(target).ConfigureAwait(false);
            if (after?.Profile.Profile is not GpuArcSyncProfile.Excellent)
            {
                Log.Warn($"Extended VRR range: Intel kept {Describe(after)} instead of Excellent.");
                return $"Could not apply Arc Sync Excellent: Intel kept {Describe(after)}.";
            }

            if (Math.Abs(after.Profile.MinimumHz - floor) > 0.1)
            {
                Log.Warn($"Extended VRR range: Excellent was set but its range is {Describe(after.Profile)}.");
                return $"Could not confirm the floor: Excellent runs {Hz(after.Profile.MinimumHz)}-{Hz(after.Profile.MaximumHz)} Hz.";
            }

            changes.Add($"profile {before} -> {Describe(after.Profile)}");
        }

        var lfc = await _driver.ReadSolutionsAsync(target).ConfigureAwait(false);
        if (lfc is null)
        {
            return "Could not read Intel's low frame rate solution.";
        }

        // Off unless the user keeps it, as the script turns it off.
        var wanted = _lowFrameRate;
        var state = wanted ? "on" : "off";
        if (lfc.LowFrameRate != wanted)
        {
            if (await SetSolutionAsync(target, GpuVrrSolution.LowFrameRate, wanted).ConfigureAwait(false) is { } problem)
            {
                return $"Could not turn {state} Intel's low frame rate compensation: " + problem;
            }

            changes.Add($"LFC {(wanted ? "off" : "on")} -> {state}");
        }

        Log.Info(changes.Count == 0
            ? $"Extended VRR range: OK EXCELLENT {floor}-120 Hz, LFC {state}; nothing to repair."
            : "Extended VRR range: REPAIRED " + string.Join("; ", changes));
        return ActiveDetail;
    }

    /// <summary>Writes one frame rate solution and reads it back; null when it took.</summary>
    private async Task<string?> SetSolutionAsync(DisplayTargetIdentity target, GpuVrrSolution solution, bool enabled)
    {
        var write = await _driver.SetSolutionAsync(target, solution, enabled).ConfigureAwait(false);
        if (write is not { Applied: true })
        {
            Log.Warn($"Extended VRR range: {solution} solution {(enabled ? "on" : "off")} not applied: "
                     + $"{write?.Detail ?? "no answer"}; it is not sent again.");
            return write?.Detail ?? "The Intel driver did not answer.";
        }

        var after = await _driver.ReadSolutionsAsync(target).ConfigureAwait(false);
        var actual = solution is GpuVrrSolution.LowFrameRate ? after?.LowFrameRate : after?.HighFrameRate;
        if (actual != enabled)
        {
            Log.Warn($"Extended VRR range: Intel {solution} solution did not change to {enabled}.");
            return $"Intel's {(solution is GpuVrrSolution.LowFrameRate ? "low" : "high")} frame rate solution did not change.";
        }

        return null;
    }

    /// <summary>Polls the driver every three seconds until it reports Arc Sync for the panel, as the script waits after sign-in.</summary>
    private async Task<GpuArcSyncState?> WaitForArcSyncAsync(DisplayTargetIdentity target, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + (long)_driverWait.TotalMilliseconds;
        while (true)
        {
            if (await _driver.ReadArcSyncAsync(target).ConfigureAwait(false) is { } state)
            {
                return state;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await _delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
    }

    /// <summary>Finds the panel and classifies its EDID and override, or says why nothing may change.</summary>
    private (Inspection? Inspection, ExtendedVrrStatus Status) Inspect(ExtendedVrrPanel panel)
    {
        _rangeLoaded = false;
        var displays = _machine.FindPanels(panel);
        if (displays.Count == 0)
        {
            LogOnce($"Extended VRR range: no active {panel.Manufacturer}{panel.ProductCode} {panel.Name} panel; the control is hidden.");
            return (null, ExtendedVrrStatus.Hidden);
        }

        if (displays.Count > 1)
        {
            return (null, Refused($"The {panel.Name} panel was found {displays.Count} times. Disconnect external displays."));
        }

        var display = displays[0];
        var edid = panel.Read(display.Reported);
        if (!edid.Supported)
        {
            return (null, Refused(edid.Refusal!));
        }

        var current = _machine.ReadOverride(display.Target);
        var variant = current.Edid is { } bytes ? panel.Recognize(edid.Physical, bytes) : null;
        var kind = !current.Present ? OverrideKind.None : variant is null ? OverrideKind.Foreign : OverrideKind.Ours;
        var inspection = new Inspection(display.Target, edid, kind,
            current.Edid is { } overriding ? ExtendedVrrPanel.Sha256(overriding) : null, variant,
            _machine.ReadStandalone());
        _rangeLoaded = inspection.ActiveOurs;
        return (inspection, ExtendedVrrStatus.Hidden);

        ExtendedVrrStatus Refused(string reason)
        {
            LogOnce("Extended VRR range: refused: " + reason);
            return new ExtendedVrrStatus(ExtendedVrrPhase.Refused, reason);
        }
    }

    private static ExtendedVrrPhase PhaseOf(Inspection inspection)
    {
        if (inspection.Override is OverrideKind.Foreign)
        {
            return ExtendedVrrPhase.Refused;
        }

        if (inspection.Standalone is not null)
        {
            return ExtendedVrrPhase.Standalone;
        }

        return inspection.Override switch
        {
            OverrideKind.Ours => inspection.ActiveOurs ? ExtendedVrrPhase.Active : ExtendedVrrPhase.RestartToApply,
            _ => inspection.Edid.Reported is null ? ExtendedVrrPhase.Off : ExtendedVrrPhase.RestartToRemove
        };
    }

    private static string DetailOf(ExtendedVrrPhase phase, Inspection inspection)
    {
        return phase switch
        {
            ExtendedVrrPhase.Off => OffDetail,
            ExtendedVrrPhase.RestartToApply => ApplyDetail,
            ExtendedVrrPhase.Active => ActiveDetail,
            ExtendedVrrPhase.RestartToRemove => RemoveDetail,
            ExtendedVrrPhase.Standalone => inspection.ActiveOurs
                ? "Active, managed by the standalone Atlas8Tweaks VRR Fix. Changing low framerate compensation hands it "
                  + "to WSGM; turning it off here removes that tool."
                : "Managed by the standalone Atlas8Tweaks VRR Fix. Restart Windows to apply.",
            _ => string.Empty
        };
    }

    private static GpuArcSyncBaseline Excellent(GpuArcSyncState arc)
    {
        return new GpuArcSyncBaseline(GpuArcSyncProfile.Excellent, arc.MonitorMinimumHz, arc.MonitorMaximumHz);
    }

    private static bool IsExcellentAcross(GpuArcSyncBaseline profile, int floor)
    {
        return profile.Profile is GpuArcSyncProfile.Excellent && Math.Abs(profile.MinimumHz - floor) <= 0.1
                                                              && Math.Abs(profile.MaximumHz - 120) <= 0.1;
    }

    private static ExtendedVrrIntelState Measured(GpuArcSyncBaseline profile, GpuVrrSolutions lfc)
    {
        return new ExtendedVrrIntelState
        {
            Source = "MEASURED", ProfileId = (int)profile.Profile, MinimumHz = profile.MinimumHz,
            MaximumHz = profile.MaximumHz, MaxIncreaseUs = profile.MaxFrameTimeIncreaseUs,
            MaxDecreaseUs = profile.MaxFrameTimeDecreaseUs, LowFpsSolutionEnabled = lfc.LowFrameRate,
            HighFpsSolutionEnabled = lfc.HighFrameRate
        };
    }

    private static ExtendedVrrIntelState Factory(ExtendedVrrPanel panel)
    {
        var factory = panel.Factory;
        return new ExtendedVrrIntelState
        {
            Source = "FACTORY_DEFAULT", ProfileId = factory.ArcSyncProfile, MinimumHz = factory.MinimumHz,
            MaximumHz = factory.MaximumHz, MaxIncreaseUs = factory.MaxFrameTimeIncreaseUs,
            MaxDecreaseUs = factory.MaxFrameTimeDecreaseUs, LowFpsSolutionEnabled = factory.LowFpsSolution,
            HighFpsSolutionEnabled = factory.HighFpsSolution
        };
    }

    private static string Describe(ExtendedVrrVariant? variant)
    {
        return variant is null
            ? "factory 48-120 Hz"
            : $"{variant.FloorHz}-120 Hz{(variant.TestBuild ? " (test build)" : "")}";
    }

    private static string Describe(GpuArcSyncState? state)
    {
        return state is null
            ? "no answer"
            : $"{Describe(state.Profile)}, panel {Hz(state.MonitorMinimumHz)}-{Hz(state.MonitorMaximumHz)} Hz"
              + (state.FixedHz is { } held ? $", fixed at {Hz(held)} Hz" : "");
    }

    private static string Describe(GpuArcSyncBaseline profile)
    {
        return $"{profile.Profile.ToString().ToUpperInvariant()} {Hz(profile.MinimumHz)}-{Hz(profile.MaximumHz)} Hz "
               + $"({profile.MaxFrameTimeIncreaseUs}/{profile.MaxFrameTimeDecreaseUs} us)";
    }

    private static string Describe(GpuVrrSolutions? lfc)
    {
        return lfc is null ? "unreadable" : $"low={lfc.LowFrameRate} high={lfc.HighFrameRate}";
    }

    private static string Hz(double value)
    {
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private void LogOnce(string message)
    {
        if (Interlocked.Exchange(ref _lastLogged, message) != message)
        {
            Log.Info(message);
        }
    }

    private void Publish(ExtendedVrrStatus status)
    {
        // Offered while the range is loaded: WSGM's own, or the standalone tool's, which the switch takes over.
        status = status with
        {
            LowFrameRateCompensation = _lowFrameRate,
            LowFrameRateVisible = _rangeLoaded && (status.Phase is ExtendedVrrPhase.Active or ExtendedVrrPhase.Standalone)
        };
        if (Interlocked.Exchange(ref _status, status) != status)
        {
            Changed?.Invoke();
        }
    }

    private enum OverrideKind
    {
        None,
        Ours,
        Foreign
    }

    /// <summary>The panel as found: its display, its EDID, its override and the standalone tool.</summary>
    private sealed record Inspection(
        DisplayTargetIdentity Target,
        ExtendedVrrEdid Edid,
        OverrideKind Override,
        string? OverrideSha256,
        ExtendedVrrVariant? OverrideVariant,
        ExtendedVrrStandalone? Standalone)
    {
        /// <summary>Whether Windows reports the override that is written: one of ours, loaded.</summary>
        public bool ActiveOurs => Override is OverrideKind.Ours && Edid.Reported is not null
                                                                && OverrideSha256 == Edid.ReportedSha256;

        /// <summary>The override for the log.</summary>
        public string OverrideText => Override switch
        {
            OverrideKind.None => "none",
            OverrideKind.Ours => Describe(OverrideVariant),
            _ => "FOREIGN (another tool)"
        };
    }
}
