using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>
///     The session's performance set: RTSS with its overlay projection and OSD power status, the
///     profile fan-out, the refresh-rate pairing and display resolutions, and the running-application
///     target fed by Steam and the foreground window.
/// </summary>
/// <remarks>
///     Overlay-test gets the RTSS half against a simulated adapter and a pretend running game; it moves
///     no display and creates no running-application target.
/// </remarks>
/// <param name="state">The session's live state.</param>
/// <param name="profiles">The profile owner whose changes the fan-out carries.</param>
/// <param name="cpuBoost">The processor boost the overlay's performance rows show.</param>
/// <param name="applicationProfiles">The power and refresh reconcile the fan-out ends with.</param>
/// <param name="integrations">The device, AutoTDP and graphics owners the performance rows read.</param>
internal sealed class PerformanceServices(
    SessionState state,
    ProfileService profiles,
    CpuBoostService cpuBoost,
    ApplicationPerformanceReconciler applicationProfiles,
    IntegrationServices integrations)
{
    private ForegroundWindowWatcher? _foregroundWindows;

    /// <summary>When the last resume repair ran, so one wake's several resume notices repair once.</summary>
    private long _lastResumeRepairTick = long.MinValue / 2;

    /// <summary>The rendering set that proves which foreground process is the game.</summary>
    private RtssFrametimeReader? _pairingFrametimes;

    private RunningApplicationCoordinator? _runningApplicationTargets;

    /// <summary>The RTSS performance service, or null before it started or when it could not.</summary>
    internal PerformanceService? Service { get; private set; }

    /// <summary>The overlay's projection of <see cref="Service" />.</summary>
    internal PerformanceOverlayBridge? OverlayBridge { get; private set; }

    /// <summary>The one queue that carries profile changes to every consumer, in order.</summary>
    internal ProfileFanOut? FanOut { get; private set; }

    /// <summary>The refresh-rate pairing that follows the frame limit; null in overlay-test.</summary>
    internal RefreshRatePairingService? RefreshPairing { get; private set; }

    /// <summary>The display resolutions Steam's Quick Access offers; null in overlay-test.</summary>
    internal DisplayResolutionService? Resolutions { get; private set; }

    /// <summary>The running-application monitor; null in overlay-test.</summary>
    internal RunningApplicationMonitor? RunningApplications { get; private set; }

    /// <summary>
    ///     Creates RTSS, its overlay projection and the profile fan-out, and applies the OSD settings.
    ///     Overlay-test then pretends one Steam game is running.
    /// </summary>
    internal void StartRtss()
    {
        var service = Service = new PerformanceService(
            state.OverlayTestOnly ? new SimulatedRtssAdapter() : new RtssNativeAdapter(),
            new RtssLauncher(),
            (field, value, token) => profiles.SetAsync(field, value, token),
            profiles.Current,
            PerformanceEnabled(state.Config));
        // Read through the property rather than captured, because the pairing service is created
        // after this and replaced on shutdown; the overlay slider then bookends exactly where the
        // Quick Access row does instead of running over RTSS's own 0-1000.
        OverlayBridge = new PerformanceOverlayBridge(
            service,
            profiles,
            () => RefreshPairing?.FrameLimitRange(),
            cpuBoost)
        {
            LivePublishers = integrations.LiveCapabilityPublishers
        };
        StartProfileFanOut();
        service.ApplyOsdCustomization(RtssOsdCustomSettings.FromConfig(state.Config.Performance));
        AttachOsdPowerStatus();
        if (state.OverlayTestOnly)
        {
            // The per-application workflow must be inspectable in the safe UI mode: pretend one
            // Steam game is running and focused, so Device -> Profiles shows the application layer
            // instead of a permanently unavailable row. The real shell gets this target from
            // RunningApplicationCoordinator, which overlay-test deliberately never creates.
            profiles.SetRunningApplication(new PerformanceApplicationTarget(
                "steam:480",
                480,
                "PreviewGame.exe"));
        }
    }

    /// <summary>Creates the refresh-rate pairing and display resolutions and lets AutoTDP run. Not in overlay-test.</summary>
    internal void StartDisplayPairing()
    {
        // Overlay-test runs without a real display to move, and pairing is the one performance
        // concern that changes hardware state rather than an RTSS profile.
        RefreshPairing = new RefreshRatePairingService();
        Resolutions = new DisplayResolutionService();
        RefreshPairing.SetStrategy(state.Config.Performance.FrameLimitStrategy);
        Service!.StateChanged += OnPerformanceStateForPairing;
        integrations.AutoTdp?.Apply(IntegrationServices.ShouldRunAutoTdp(state.Config.DeviceIntegration));
    }

    /// <summary>Starts the running-application monitor over Steam and the foreground window. Not in overlay-test.</summary>
    /// <param name="steam">The session's Steam client.</param>
    internal void StartRunningApplications(SteamClient steam)
    {
        // Its own reader rather than AutoTDP's: RtssFrametimeReader is not thread-safe, and
        // these two poll on different threads at different cadences. The mapping is read-only,
        // so a second view of it costs a handle and nothing else.
        _pairingFrametimes = new RtssFrametimeReader();
        var runningApplications = RunningApplications = new RunningApplicationMonitor(
            steam.RunningApps,
            state.Config.Cef.Enabled,
            _pairingFrametimes.ReadLive);
        runningApplications.Start();

        // The second identity source. It feeds the same monitor rather than driving policy on
        // its own, so per-application settings also work on the desktop and for titles Steam
        // never launched — which is the only way the overlay's per-game rows mean anything
        // outside a Steam game.
        _foregroundWindows = new ForegroundWindowWatcher();
        _foregroundWindows.ApplicationChanged += OnForegroundApplicationChanged;
        _runningApplicationTargets = new RunningApplicationCoordinator(
            runningApplications,
            // The profile store decides what runs and which layer is in force; its change drives
            // RTSS, the device, power and refresh through the fan-out.
            target => profiles.SetRunningApplication(target),
            integrations.Device is null
                ? null
                : ApplyRunningApplicationTargetAsync);
        _foregroundWindows.Start();
    }

    /// <summary>The running application now, or the initial snapshot when none is tracked.</summary>
    internal RunningApplicationTargetSnapshot CurrentApplication =>
        RunningApplications?.Current ?? RunningApplicationTargetSnapshot.Initial();

    /// <summary>Samples the sensors AutoTDP consults, or nothing before RTSS started.</summary>
    /// <returns>The current OSD metrics.</returns>
    internal RtssOsdMetrics SampleSensors()
    {
        return Service?.SampleSensors() ?? RtssOsdMetrics.Empty;
    }

    /// <summary>
    ///     The deadline AutoTDP judges frame delivery against.
    /// </summary>
    /// <remarks>
    ///     AutoTDP's own per-game target, or an active RTSS frame limit below it; no frame limit is needed.
    /// </remarks>
    /// <returns>The frame deadline in milliseconds.</returns>
    internal double TargetFrametimeMs()
    {
        return AutoTdpService.TargetFrametime(Service?.Current, profiles.Current.Layers);
    }

    /// <summary>Whether WSGM may change RTSS. Overlay-test always may, against its simulated adapter.</summary>
    /// <param name="config">The configuration to read.</param>
    /// <returns>Whether RTSS profiles are applied.</returns>
    internal bool PerformanceEnabled(AppConfig config)
    {
        return state.OverlayTestOnly || config.Performance.Enabled;
    }

    /// <summary>Applies a reloaded configuration's frame-limit strategy, OSD and RTSS profiles.</summary>
    /// <param name="config">The reloaded configuration.</param>
    internal void ApplyConfig(AppConfig config)
    {
        var performance = Service;
        if (performance is null)
        {
            return;
        }

        if (RefreshPairing?.SetStrategy(config.Performance.FrameLimitStrategy) == true)
        {
            RefreshPairing.Pair(performance.Current.Desired.FrameLimit ?? 0, true);
        }

        performance.ApplyOsdCustomization(RtssOsdCustomSettings.FromConfig(config.Performance));
        Log.Observe(
            performance.ApplyProfilesAsync(profiles.Current, PerformanceEnabled(config)),
            "RTSS performance config apply",
            true);
    }

    /// <summary>Tells the running-application monitor whether it may ask Steam.</summary>
    /// <param name="enabled">The master Steam integration switch.</param>
    internal void SetSteamEnabled(bool enabled)
    {
        RunningApplications?.SetSteamEnabled(enabled);
    }

    /// <summary>Re-applies the running application's target after the device applied a reloaded configuration.</summary>
    internal void RefreshApplicationTarget()
    {
        _runningApplicationTargets?.RefreshCurrent();
    }

    /// <summary>Re-establishes the state a sleep invalidates without announcing it.</summary>
    /// <remarks>
    ///     The device cycle has its own resume and the Steam transport reconnects after every resume
    ///     (<see cref="SteamUiServices.ReleaseAfterResume" />). What is left are the two answers cached
    ///     from before the sleep. The refresh-rate pairing is latched
    ///     on the limit it last applied, so a panel that came back at its default rate keeps the
    ///     paired label while running at another cadence until the cap is moved twice. RTSS is only
    ///     probed while a UI client holds an observation lease, so with Quick Access closed and
    ///     Steam's performance rows absent nothing looks at it after a wake at all: a restarted RTSS
    ///     or a profile edited meanwhile goes unnoticed until the next game launch. One forced pass
    ///     of each costs a driver round trip and a probe, and only on a real wake.
    /// </remarks>
    internal void RepairAfterResume()
    {
        var now = Environment.TickCount64;
        if (state.ShutdownRequested || state.OverlayTestOnly || now - _lastResumeRepairTick < 2000)
        {
            return;
        }

        _lastResumeRepairTick = now;

        RefreshPairing?.Invalidate();
        RefreshPairing?.Pair(Service?.Current.Desired.FrameLimit ?? 0, true);
        if (Service is { } performance && PerformanceEnabled(state.Config))
        {
            Log.Observe(performance.RefreshAsync(), "RTSS resume refresh");
        }
    }

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
        RunningApplications?.ReportForeground(executable, imagePath, processId);
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
    internal bool ApplyManualRefreshRate(int refreshHz)
    {
        return RefreshPairing?.TryApplyManual(
            refreshHz,
            Service?.Current.Desired.FrameLimit ?? 0) ?? false;
    }

    /// <summary>Reads what Steam's Quick Access performance rows may offer now.</summary>
    /// <returns>The frame caps, refresh rates and variable refresh state.</returns>
    internal NativeQamPerfSupport ReadNativeQamPerfSupport()
    {
        var pairing = RefreshPairing;
        var (options, paired) = pairing?.FrameLimitChoices() ?? ([], new Dictionary<int, int>());
        // The same predicate the pairing service decides by, not a second copy of the comparison:
        // under either coupled strategy the pairing policy owns the refresh rate, so Steam's manual
        // refresh row must not be offered at all — a user setting it would watch the next frame-cap
        // change overwrite it.
        var manualRefresh = FrameLimitPairing.RefreshRateIsUserOwned(
            state.Config.Performance.FrameLimitStrategy);

        // The same capability the write goes to, on the device or a graphics package, so the toggle
        // cannot show a state the publisher disagrees with.
        var view = VariableRefreshCapabilities.Find(integrations.Device, integrations.Gpu, true)?.View;
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
            // None under the uncoupled strategy, where a cap changes no display state and the row
            // therefore has no rate to name — which is also what makes the label collapse from
            // "60 FPS (60 Hz)" to plain "60 FPS" without a second flag saying so.
            manualRefresh || paired.Count == 0 ? null : paired,
            refreshRates);
    }

    private async Task ApplyRunningApplicationTargetAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        integrations.AutoTdp?.ApplyRunningApplication(snapshot);
        if (integrations.Device is { } coordinator)
        {
            await coordinator.ApplyRunningApplicationAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void AttachOsdPowerStatus()
    {
        if (integrations.Device is { } coordinator)
        {
            coordinator.Capabilities.Changed += OnOsdPowerCapabilitiesChanged;
            coordinator.ConfigurationChanged += OnOsdPowerConfigurationChanged;
        }

        if (integrations.AutoTdp is { } autoTdp)
        {
            autoTdp.StatusChanged += OnOsdAutoTdpStatusChanged;
        }

        UpdateOsdPowerStatus();
    }

    /// <summary>Clears the OSD's power figures and stops following the device and AutoTDP, before both stop.</summary>
    internal void DetachOsdPowerStatus()
    {
        Service?.ApplyOsdPowerStatus(RtssOsdPowerStatus.Empty);
        if (integrations.Device is { } coordinator)
        {
            coordinator.Capabilities.Changed -= OnOsdPowerCapabilitiesChanged;
            coordinator.ConfigurationChanged -= OnOsdPowerConfigurationChanged;
        }

        if (integrations.AutoTdp is { } autoTdp)
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
        var performance = Service;
        var coordinator = integrations.Device;
        if (performance is null || coordinator is null)
        {
            return;
        }

        // Raw observed and desired watts: a device that has neither shows no figure, never the ceiling.
        views ??= coordinator.Capabilities.Snapshot();
        var tdp = IntegerRangeProjection.PowerLimit(views);
        var autoTdp = integrations.AutoTdp?.Status;
        var enabled = integrations.AutoTdp?.Enabled ?? false;
        var running = enabled && autoTdp?.State is AutoTdpState.Controlling;
        var reportedTdpWatts = tdp.Available
            ? tdp.Observed ?? tdp.Desired
            : null;
        var tdpWatts = enabled && autoTdp?.Watts is { } automaticWatts
            ? automaticWatts
            : reportedTdpWatts;
        performance.ApplyOsdPowerStatus(new RtssOsdPowerStatus(
            tdpWatts,
            enabled,
            running,
            running ? autoTdp?.Watts : null,
            running ? AutoTdpActivity(enabled, autoTdp) : string.Empty,
            BoostWatts(views, CapabilityRole.PowerSlowLimit),
            BoostWatts(views, CapabilityRole.PowerFastLimit)));
    }

    /// <summary>A boost limit's raw watts, or null when the device does not publish that limit.</summary>
    private static int? BoostWatts(IReadOnlyList<DeviceCapabilityView> views, CapabilityRole role)
    {
        var limit = IntegerRangeProjection.PowerLimit(views, role);
        return limit.Available ? limit.Observed ?? limit.Desired : null;
    }

    /// <summary>The OSD's one-word description of what AutoTDP is doing.</summary>
    /// <param name="enabled">Whether AutoTDP is enabled.</param>
    /// <param name="status">Its latest status, or null before the first.</param>
    /// <returns>The word, or empty while AutoTDP is off.</returns>
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

    /// <remarks>
    ///     Runs off the state event rather than inside <see cref="PerformanceService" />, because that
    ///     service owns RTSS profiles and this changes a display mode — two different pieces of hardware
    ///     with different failure modes and different restore obligations.
    ///     <para>
    ///         Only an actual change is acted on. The state event fires for any performance change, and
    ///         re-applying the same mode on each would put a driver round trip behind every one of them.
    ///     </para>
    /// </remarks>
    private void OnPerformanceStateForPairing(PerformanceState performanceState)
    {
        // A frame limit below AutoTDP's target lowers the deadline; the rows hear about it from here.
        integrations.AutoTdp?.RefreshPrerequisites();

        RefreshPairing?.Pair(performanceState.Desired.FrameLimit ?? 0, false);
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
        FanOut = new ProfileFanOut(profiles,
        [
            new ProfileConsumer("RTSS", (snapshot, token) => Service is { } performance
                ? performance.ApplyProfilesAsync(snapshot, PerformanceEnabled(state.Config), token)
                : Task.CompletedTask),
            new ProfileConsumer("device", (snapshot, token) => integrations.Device is { } coordinator
                ? coordinator.ApplyProfilesAsync(snapshot, token)
                : Task.CompletedTask),
            new ProfileConsumer("graphics", (snapshot, token) => integrations.Gpu is { } gpu
                ? gpu.ApplyProfilesAsync(snapshot, token)
                : Task.CompletedTask),
            new ProfileConsumer("power and refresh", (snapshot, token) =>
                applicationProfiles.ReconcileApplicationProfileAsync(snapshot, token))
        ]);
        FanOut.Queue(profiles.Current, ProfileChangeKind.Application);
    }

    /// <summary>Disposes the running-application targets, the foreground watcher and the monitor, in that order.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the steps, which never fault.</returns>
    internal async Task DisposeRunningApplicationsAsync(ShutdownSteps steps)
    {
        await steps.StepAsync("Disposing running-application targets during application shutdown failed",
            async () =>
            {
                var targets = _runningApplicationTargets;
                _runningApplicationTargets = null;
                if (targets is not null)
                {
                    await targets.DisposeAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        await steps.UiStepAsync("Disposing the foreground window watcher during application shutdown failed",
            () =>
            {
                if (_foregroundWindows is { } foreground)
                {
                    _foregroundWindows = null;
                    foreground.ApplicationChanged -= OnForegroundApplicationChanged;
                    foreground.Dispose();
                }
            }).ConfigureAwait(false);
        await steps.StepAsync("Disposing running applications during application shutdown failed", async () =>
        {
            var running = RunningApplications;
            RunningApplications = null;
            if (running is not null)
            {
                await running.DisposeAsync().ConfigureAwait(false);
            }

            // After the monitor, which is the only thing that reads it.
            _pairingFrametimes?.Dispose();
            _pairingFrametimes = null;
        }).ConfigureAwait(false);
    }

    /// <summary>Disposes the overlay's performance projection on the UI thread that built it.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DisposeOverlayBridge(ShutdownSteps steps)
    {
        var bridge = OverlayBridge;
        OverlayBridge = null;
        steps.Step("Disposing the performance overlay bridge failed", () => bridge?.Dispose());
    }

    /// <summary>
    ///     Disposes RTSS, then puts the pre-game refresh rate back: the paired rate is transient, but
    ///     leaving the desktop at a game's 48 Hz is a change the user never made and would have to hunt
    ///     for. A resolution picked from a menu is the user's own saved choice and stays.
    /// </summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the steps, which never fault.</returns>
    internal async Task StopAsync(ShutdownSteps steps)
    {
        await steps.StepAsync("Disposing performance monitoring during application shutdown failed", async () =>
        {
            var performance = Service;
            Service = null;
            if (performance is not null)
            {
                performance.StateChanged -= OnPerformanceStateForPairing;
                await performance.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        steps.Step("Restoring the pre-game display refresh rate during application shutdown failed", () =>
        {
            var pairing = RefreshPairing;
            RefreshPairing = null;
            if (pairing is not null && !pairing.Restore())
            {
                steps.Record(
                    new InvalidOperationException("The pre-game display refresh rate could not be restored."));
            }
        });
    }
}
