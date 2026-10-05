using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    private int _pairedFrameLimit = -1;
    private long _pairedOperatingPoint = -1;

    /// <summary>Hands a foreground application change to the running-application monitor.</summary>
    /// <param name="executable">Foreground executable file name.</param>
    /// <param name="imagePath">Its full image path, or null when the process could not be opened.</param>
    /// <param name="processId">Its process identifier, or zero when it could not be read.</param>
    /// <remarks>
    ///     Straight through, with no policy of its own: the monitor's projection decides whether this
    ///     identity is used at all, so the precedence between Steam and the foreground stays in the one
    ///     pure function that can be tested.
    /// </remarks>
    private void OnForegroundApplicationChanged(string executable, string? imagePath, uint processId)
    {
        _runningApplications?.ReportForeground(executable, imagePath, processId);
    }

    /// <summary>Applies a refresh rate the user chose by hand.</summary>
    /// <param name="refreshHz">The chosen rate.</param>
    /// <returns>Whether the display is now at that rate.</returns>
    /// <remarks>
    ///     The ownership and validation rules live with
    ///     <see
    ///         cref="RefreshRatePairingService.TryApplyManual" />
    ///     ; this only supplies the cap in force.
    /// </remarks>
    private bool ApplyManualRefreshRate(int refreshHz)
    {
        return _refreshPairing?.TryApplyManual(
            refreshHz,
            _performance?.Current.Desired.FrameLimit ?? 0) ?? false;
    }

    private NativeQamPerfSupport ReadNativeQamPerfSupport()
    {
        var pairing = _refreshPairing;
        var options = pairing?.FrameLimitOptions() ?? [];
        // The same predicate the pairing service decides by, not a second copy of the comparison:
        // under either coupled strategy the pairing policy owns the refresh rate, so Steam's manual
        // refresh row must not be offered at all — a user setting it would watch the next frame-cap
        // change overwrite it.
        var manualRefresh = FrameLimitPairing.RefreshRateIsUserOwned(
            _config.Performance.FrameLimitStrategy);

        // The same capability the write goes to, on the device or a graphics package, so the toggle
        // cannot show a state the publisher disagrees with.
        var view = VariableRefreshCapabilities.Find(_deviceCoordinator, _gpu, true)?.View;
        var vrr = view is not null;
        var vrrEnabled = view?.Projection.State.ObservedValue?.BooleanValue ?? false;

        // Read through the pairing service's session cache: this runs on every state publication,
        // and enumerating plus CDS_TESTing every mode each time hammers the display driver.
        // Enumerated under every strategy: with the frame limit switched off the unified row
        // becomes a refresh-rate slider, offered whatever the pairing strategy is because there is
        // no cap left for it to fight. RefreshRatesSelectable below still gates Valve's SEPARATE
        // manual row, which must stay hidden while a cap owns the rate.
        var refreshRates = pairing?.AcceptedRates() ?? [];
        return new NativeQamPerfSupport(
            options,
            vrr,
            manualRefresh && refreshRates.Count > 0,
            refreshRates.Count > 0 ? refreshRates.Min() : null,
            refreshRates.Count > 0 ? refreshRates.Max() : null,
            vrrEnabled,
            refreshRates.Count > 0 ? PrimaryDisplayModes.ReadCurrentRefreshRate() : null,
            ReadPairedRefreshRates(pairing, options, manualRefresh),
            refreshRates);
    }

    /// <summary>The refresh rate each offered cap will be presented at.</summary>
    /// <remarks>
    ///     Built here rather than in the injected half so the pairing policy stays one decision in one
    ///     place. Empty under the uncoupled strategy, where a cap changes no display state and the row
    ///     therefore has no rate to name — which is also what makes the label collapse from
    ///     "60 FPS (60 Hz)" to plain "60 FPS" without a second flag saying so.
    /// </remarks>
    private static Dictionary<int, int>? ReadPairedRefreshRates(
        RefreshRatePairingService? pairing,
        IReadOnlyList<int> options,
        bool uncoupled)
    {
        if (uncoupled || pairing is null || options.Count == 0)
        {
            return null;
        }

        Dictionary<int, int> paired = new(options.Count);
        foreach (var cap in options)
        {
            if (cap > 0 && pairing.SelectRefreshHz(cap) is { } hz)
            {
                paired[cap] = hz;
            }
        }

        return paired.Count > 0 ? paired : null;
    }

    /// <summary>Applies the Device Integration master switch to AutoTDP at every entry point.</summary>
    internal static bool ShouldRunAutoTdp(DeviceIntegrationConfig config)
    {
        return config is { Enabled: true, AutoTdpEnabled: true };
    }

    private async Task ApplyRunningApplicationTargetAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        _autoTdp?.ApplyRunningApplication(snapshot);
        if (_deviceCoordinator is { } coordinator)
        {
            await coordinator.ApplyRunningApplicationAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void AttachOsdPowerStatus()
    {
        if (_deviceCoordinator is { } coordinator)
        {
            coordinator.Capabilities.Changed += OnOsdPowerCapabilitiesChanged;
            coordinator.ConfigurationChanged += OnOsdPowerConfigurationChanged;
        }

        if (_autoTdp is { } autoTdp)
        {
            autoTdp.StatusChanged += OnOsdAutoTdpStatusChanged;
        }

        UpdateOsdPowerStatus();
    }

    private void DetachOsdPowerStatus()
    {
        _performance?.ApplyOsdPowerStatus(RtssOsdPowerStatus.Empty);
        if (_deviceCoordinator is { } coordinator)
        {
            coordinator.Capabilities.Changed -= OnOsdPowerCapabilitiesChanged;
            coordinator.ConfigurationChanged -= OnOsdPowerConfigurationChanged;
        }

        if (_autoTdp is { } autoTdp)
        {
            autoTdp.StatusChanged -= OnOsdAutoTdpStatusChanged;
        }
    }

    private void OnOsdPowerCapabilitiesChanged(IReadOnlyList<DeviceCapabilityView> views)
    {
        UpdateOsdPowerStatus(views);
    }

    private void OnOsdPowerConfigurationChanged()
    {
        UpdateOsdPowerStatus();
    }

    private void OnOsdAutoTdpStatusChanged(AutoTdpStatus status)
    {
        UpdateOsdPowerStatus();
    }

    private void UpdateOsdPowerStatus(IReadOnlyList<DeviceCapabilityView>? views = null)
    {
        var performance = _performance;
        var coordinator = _deviceCoordinator;
        if (performance is null || coordinator is null)
        {
            return;
        }

        // Raw observed and desired watts: a device that has neither shows no figure, never the ceiling.
        var tdp = PowerLimitProjection.Project(views ?? coordinator.Capabilities.Snapshot());
        var autoTdp = _autoTdp?.Status;
        var enabled = _autoTdp?.Enabled ?? false;
        var running = enabled && autoTdp?.State is AutoTdpState.Controlling;
        var reportedTdpWatts = tdp.Available
            ? tdp.ObservedWatts ?? tdp.DesiredWatts
            : null;
        var tdpWatts = enabled && autoTdp?.Watts is { } automaticWatts
            ? automaticWatts
            : reportedTdpWatts;
        performance.ApplyOsdPowerStatus(new RtssOsdPowerStatus(
            tdpWatts,
            enabled,
            running,
            running ? autoTdp?.Watts : null,
            running ? AutoTdpActivity(enabled, autoTdp) : string.Empty));
    }

    internal static string AutoTdpActivity(bool enabled, AutoTdpStatus? status)
    {
        if (!enabled)
        {
            return string.Empty;
        }

        if (status is null || status.State is AutoTdpState.Off)
        {
            return "Starting";
        }

        return status.State switch
        {
            AutoTdpState.Unavailable => "Unavailable",
            AutoTdpState.Idle => "Waiting",
            AutoTdpState.Paused => "Paused",
            AutoTdpState.Controlling when status.Detail is "at-maximum" => "Can't Reach",
            AutoTdpState.Controlling when status.Detail is "settling" => "Settling",
            AutoTdpState.Controlling when status.Detail is "probe-pending" => "Testing",
            AutoTdpState.Controlling when status.Detail is "quarantine-hiatus" or "quarantine-stall"
                or "quarantine-recovery" => "Stalled",
            AutoTdpState.Controlling when status.Detail is "unresponsive" => "Holding",
            AutoTdpState.Controlling => status.Action switch
            {
                AutoTdpAction.Raise => "Raising",
                AutoTdpAction.Probe => "Lowering",
                AutoTdpAction.Restore => "Restoring",
                _ => "Holding"
            },
            _ => "Starting"
        };
    }

    /// <summary>
    ///     The deadline AutoTDP judges frame delivery against.
    /// </summary>
    /// <remarks>
    ///     Only a verified, active RTSS limit supplies a deadline. Zero means no control is permitted;
    ///     a desired value or a default 60 Hz target cannot stand in for an active limiter.
    /// </remarks>
    private double TargetFrametimeMs()
    {
        return AutoTdpService.TargetFrametime(_performance?.Current);
    }

    /// <remarks>
    ///     Runs off the state event rather than inside <see cref="PerformanceService" />, because that
    ///     service owns RTSS profiles and this changes a display mode — two different pieces of hardware
    ///     with different failure modes and different restore obligations.
    ///     <para>
    ///         Only an actual change is acted on. The state event fires for any performance change, and
    ///         re-applying the same mode on each would put a driver round trip behind every one of them.
    ///     </para>
    /// </remarks>
    private void OnPerformanceStateForPairing(PerformanceState state)
    {
        if (_autoTdp is { } autoTdp)
        {
            // Losing the limiter suspends control and restores the previous limit, but leaves the
            // setting alone. The limit is per application, so switching to a window without one and
            // back is the ordinary case, and clearing the setting there left AutoTDP off in the game.
            autoTdp.RefreshPrerequisites();
            if (autoTdp.Availability.Available && ShouldRunAutoTdp(_config.DeviceIntegration))
            {
                autoTdp.Apply(true);
            }
        }

        ApplyRefreshPairing(state.Desired.FrameLimit ?? 0, false);
    }

    /// <summary>Pairs the display cadence to a frame limit, unless that pairing is already held.</summary>
    /// <param name="limit">The desired frame limit, or zero for uncapped.</param>
    /// <param name="force">
    ///     Re-applies the pairing for the limit already latched. Set after a resume: the limit did not
    ///     change, but the panel behind it was re-established by the driver and may have come back at
    ///     its default rate, which the latch would otherwise hide until the user moved the cap twice.
    /// </param>
    private void ApplyRefreshPairing(int limit, bool force)
    {
        if (_refreshPairing is not { } pairing)
        {
            return;
        }

        var point = pairing.OperatingPointRevision;
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
                _ = pairing.Restore();
            }

            return;
        }

        _ = pairing.ApplyForCap(limit);
    }

    /// <summary>Whether WSGM may change RTSS. Overlay-test always may, against its simulated adapter.</summary>
    private bool PerformanceEnabled(AppConfig config)
    {
        return _overlayTestOnly || config.Performance.Enabled;
    }

    /// <summary>The profile keys of the device and every graphics package running now.</summary>
    /// <returns>The keys stored values of a running package carry.</returns>
    private IReadOnlyCollection<string> LiveCapabilityPublishers()
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        if (_deviceCoordinator?.DeviceIdentityKey is { } device)
        {
            keys.Add(device);
        }

        foreach (var key in _gpu?.ActiveProfileKeys ?? [])
        {
            keys.Add(key);
        }

        return keys;
    }

    /// <summary>Starts the one queue that carries profile changes to every consumer, in order.</summary>
    /// <remarks>
    ///     RTSS first because it is cheap and the overlay shows it; then the device's desired values,
    ///     fan profile and controller target; then the graphics packages' values and their
    ///     per-application sync; then the power limit and refresh preference, which read the published
    ///     capabilities of both.
    /// </remarks>
    private void StartProfileFanOut()
    {
        _profileFanOut = new ProfileFanOut(_profiles,
        [
            new ProfileConsumer("RTSS", (snapshot, token) => _performance is { } performance
                ? performance.ApplyProfilesAsync(snapshot, PerformanceEnabled(_config), token)
                : Task.CompletedTask),
            new ProfileConsumer("device", (snapshot, token) => _deviceCoordinator is { } coordinator
                ? coordinator.ApplyProfilesAsync(snapshot, token)
                : Task.CompletedTask),
            new ProfileConsumer("graphics", (snapshot, token) => _gpu is { } gpu
                ? gpu.ApplyProfilesAsync(snapshot, token)
                : Task.CompletedTask),
            new ProfileConsumer("power and refresh", (snapshot, token) =>
                _applicationProfiles.ReconcileApplicationProfileAsync(snapshot, token))
        ]);
        _profileFanOut.Queue(_profiles.Current, ProfileChangeKind.Application);
    }
}
