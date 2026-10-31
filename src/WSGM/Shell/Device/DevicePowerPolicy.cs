using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;

namespace WSGM.Shell;

/// <summary>
///     The device's power policy: the one power lane, manual TDP, presets and their power-source
///     assignments, per-application limits and AutoTDP.
/// </summary>
/// <remarks>
///     Every sustained, boost, scenario and preset write runs in <see cref="Lane" />. The coordinator
///     composes this owner and drives its loops from the device cycle; capability writes reach the
///     handheld through <see cref="DeviceCapabilityCommands" />.
/// </remarks>
internal sealed class DevicePowerPolicy
{
    private readonly RtssFrametimeReader _autoTdpFrametimes = new();
    private readonly AutoTdpTraceRecorder _autoTdpTrace;
    private readonly DeviceCapabilityRouter _capabilities;
    private readonly Func<bool> _closed;
    private readonly Func<AppConfig> _config;
    private readonly Func<HandheldDefinition?> _definition;
    private readonly Func<CancellationToken, ValueTask<DeviceCoordinator.TransitionLease>> _enterTransition;

    private readonly Func<string, string?, CapabilityValue?, CapabilityCommandOrigin, bool, CancellationToken,
        Task<CapabilityCommandResult>> _executeInLane;

    private readonly CancellationToken _lifetime;
    private readonly Lock _loopGate = new();
    private readonly Action<bool> _manualVariableRefresh;
    private readonly Action<Task, string> _observe;
    private readonly Func<Action<AppConfig>, CancellationToken, Task<AppConfig>> _persistConfiguration;

    /// <summary>
    ///     Wakes the power-assignment reconcile. One pending signal stands for any number of changes,
    ///     because the reconcile reads everything it needs afresh.
    /// </summary>
    private readonly Channel<bool> _powerAssignmentChanges = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ProfileService _profiles;
    private readonly Func<bool?> _readOnAcPower;
    private readonly Func<Action, IDisposable?> _registerPowerModeNotification;

    /// <summary>Started with the first device cycle, so integration off runs no power loop.</summary>
    /// <remarks>Assigned and read under <see cref="_loopGate" />.</remarks>
    private Task _assignmentLoop = Task.CompletedTask;

    private bool _loopsStarted;

    /// <summary>The power controls' last reading, so only a change to them wakes the assignment reconcile.</summary>
    /// <remarks>Read and written on the UI thread, where the router raises its change event.</remarks>
    private (PowerControlReading Sustained, PowerControlReading Slow, PowerControlReading Fast,
        PowerControlReading Scenario) _powerControls;

    private IDisposable? _powerModeNotification;

    /// <summary>Whether WSGM imposed the power limit in force for the running application.</summary>
    /// <remarks>Read and written only inside <see cref="Lane" />.</remarks>
    private bool _profilePowerImposed;

    /// <summary>Whether the imposed limit moved the sustained/boost pair together.</summary>
    /// <remarks>Read and written only inside <see cref="Lane" />.</remarks>
    private bool _profilePowerPaired;

    /// <summary>Composes the power lane, presets, assignments and AutoTDP for one device coordinator.</summary>
    /// <param name="capabilities">The device capability router power controls are read from.</param>
    /// <param name="profiles">The profile owner manual limits and assignments are saved to.</param>
    /// <param name="config">Reads the configuration in force.</param>
    /// <param name="definition">Reads the matched device definition, or null before detection.</param>
    /// <param name="closed">Whether the coordinator closed admission.</param>
    /// <param name="lifetime">The coordinator's lifetime token, cancelled when admission closes.</param>
    /// <param name="enterTransition">Enters the coordinator's transition gate.</param>
    /// <param name="persistConfiguration">Persists one configuration change; called with the transition gate held.</param>
    /// <param name="traceDirectory">Where AutoTDP writes its trace.</param>
    /// <param name="autoTdpTargetFrametimeMs">Reads the frame-time target in milliseconds; zero disables control.</param>
    /// <param name="autoTdpMetrics">Reads the sensor snapshot used by AutoTDP.</param>
    /// <param name="manualVariableRefresh">Persists an explicitly chosen variable-refresh value to the active profile.</param>
    /// <param name="powerModes">Shared Windows power-mode owner borrowed by preset application.</param>
    /// <param name="readOnAcPower">Reads AC/battery status; null means unknown.</param>
    /// <param name="registerPowerModeNotification">Registers the Windows power-mode wake; disposed during shutdown.</param>
    /// <param name="cycleRestore">Reads the device cycle's desired-value restore, which assignments wait for.</param>
    /// <param name="observe">Tracks fire-and-forget work until coordinator shutdown.</param>
    /// <param name="execute">Routes a command through the capability commands, taking the lane for a power role.</param>
    /// <param name="executeInLane">Routes a command through the capability commands from inside the lane.</param>
    internal DevicePowerPolicy(
        DeviceCapabilityRouter capabilities,
        ProfileService profiles,
        Func<AppConfig> config,
        Func<HandheldDefinition?> definition,
        Func<bool> closed,
        CancellationToken lifetime,
        Func<CancellationToken, ValueTask<DeviceCoordinator.TransitionLease>> enterTransition,
        Func<Action<AppConfig>, CancellationToken, Task<AppConfig>> persistConfiguration,
        string traceDirectory,
        Func<double> autoTdpTargetFrametimeMs,
        Func<RtssOsdMetrics> autoTdpMetrics,
        Action<bool> manualVariableRefresh,
        WindowsPowerModes powerModes,
        Func<bool?> readOnAcPower,
        Func<Action, IDisposable?> registerPowerModeNotification,
        Func<Task> cycleRestore,
        Action<Task, string> observe,
        Func<string, string?, CapabilityValue?, CapabilityCommandOrigin, bool, CancellationToken,
            Task<CapabilityCommandResult>> execute,
        Func<string, string?, CapabilityValue?, CapabilityCommandOrigin, bool, CancellationToken,
            Task<CapabilityCommandResult>> executeInLane)
    {
        _capabilities = capabilities;
        _profiles = profiles;
        _config = config;
        _definition = definition;
        _closed = closed;
        _lifetime = lifetime;
        _enterTransition = enterTransition;
        _persistConfiguration = persistConfiguration;
        _manualVariableRefresh = manualVariableRefresh;
        _readOnAcPower = readOnAcPower;
        _registerPowerModeNotification = registerPowerModeNotification;
        _observe = observe;
        _executeInLane = executeInLane;
        // AutoTDP writes through the same power lane as every other power write, and the coordinator
        // restores its original limit before the device stops.
        _autoTdpTrace = new AutoTdpTraceRecorder(
            traceDirectory,
            () => _definition() is { } matched
                ? (matched.FamilyId, HandheldDeviceRuntime.SourceVersion)
                : (null, null),
            new AutoTdpTraceSystemContext());
        AutoTdp = new AutoTdpService(
            _autoTdpFrametimes,
            _capabilities.Snapshot,
            (power, value, pair, token) => execute(
                power.Descriptor.CapabilityId,
                power.Descriptor.InstanceId,
                value,
                CapabilityCommandOrigin.AutoTdp,
                pair, token),
            autoTdpTargetFrametimeMs,
            _autoTdpTrace,
            autoTdpMetrics);
        // Scenario targets are one-shot preset steps. Persist only the watt controls through the
        // manual funnel; saving an AC scenario as desired state would replay it on battery later.
        PowerPresets = new DevicePowerPresets(() => IntegrationEnabled ? _capabilities.Snapshot() : [],
            ExecutePresetCapabilityAsync, powerModes, _readOnAcPower, Lane,
            () => AutoTdp.OwnsPower);
        PowerAssignments = new DevicePowerAssignments(PowerPresets,
            () => new DevicePowerAssignmentContext(_profiles.Current,
                _definition()?.FamilyId,
                IntegrationEnabled, _readOnAcPower()),
            SavePowerAssignmentAsync,
            cycleRestore);
    }

    /// <summary>
    ///     The one lane every sustained, boost, scenario and preset write runs in: user writes, presets,
    ///     per-application restores and AutoTDP.
    /// </summary>
    /// <remarks>
    ///     Nothing that holds this lane waits for the transition gate, so a transition may wait for the
    ///     lane: AutoTDP's restore before a device stop does exactly that.
    /// </remarks>
    internal SemaphoreSlim Lane { get; } = new(1, 1);

    /// <summary>The one AutoTDP service; it writes only through <see cref="Lane" />.</summary>
    internal AutoTdpService AutoTdp { get; }

    /// <summary>One-shot preset owner shared by device surfaces and the serialized power lane.</summary>
    internal DevicePowerPresets PowerPresets { get; }

    /// <summary>Owner of saved AC/battery preset assignments and their change-triggered reconciliation.</summary>
    internal DevicePowerAssignments PowerAssignments { get; }

    /// <summary>The power-assignment loop, or a completed task before the first device cycle started it.</summary>
    internal Task AssignmentLoop
    {
        get
        {
            lock (_loopGate)
            {
                return _assignmentLoop;
            }
        }
    }

    /// <summary>Whether AutoTDP is switched on in the persisted configuration.</summary>
    internal bool AutoTdpEnabled => _config().DeviceIntegration.AutoTdpEnabled;

    /// <summary>Whether the declared power pair permits manual control and whether the active profile couples its limits.</summary>
    internal (bool Available, bool Unified) ManualTdpMode =>
        (IntegrationEnabled && _capabilities.HasDescriptor(static descriptor =>
                descriptor is { Role: CapabilityRole.PowerSustainedLimit, PairedPowerLimitId: not null }),
            ManualTdpUnified);

    /// <summary>
    ///     Whether manual TDP is unified for the running application. Unlike
    ///     <see cref="ManualTdpMode" />, this builds no capability snapshot.
    /// </summary>
    internal bool ManualTdpUnified
    {
        get { return _profiles.Current.Layers.Value(values => values.TdpUnified).Value == true; }
    }

    /// <summary>The game profile overriding the manual power mode, or null when it comes from Global.</summary>
    internal string? ManualTdpOverrideId =>
        CapabilityProjection.OverrideId(_profiles.Current.Layers, new ProfileSettingKey(ProfileField.TdpUnified));

    private bool IntegrationEnabled => _config().DeviceIntegration.Enabled;

    /// <summary>Returns the manual power mode of the running application to the Global value.</summary>
    /// <returns>Completion of clearing the game's manual-mode override through the profile owner.</returns>
    internal Task UseGlobalManualTdpAsync()
    {
        ObjectDisposedException.ThrowIf(_closed(), typeof(DeviceCoordinator));
        return _profiles.ClearGameOverrideAsync(new ProfileSettingKey(ProfileField.TdpUnified), null, _lifetime);
    }

    /// <summary>Saves the active profile's manual sustained/boost coupling mode.</summary>
    /// <param name="unified">True to couple the limits; false to retain independent sustained and boost values.</param>
    /// <returns>Completion of the saved profile edit; shutdown cancels admission.</returns>
    /// <exception cref="InvalidOperationException">The current device does not expose an available power pair.</exception>
    internal async Task SetManualTdpModeAsync(bool unified)
    {
        using var transition = await _enterTransition(_lifetime).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_closed(), typeof(DeviceCoordinator));
        if (!ManualTdpMode.Available)
        {
            throw new InvalidOperationException("Paired TDP is unavailable.");
        }

        await _profiles.SetAsync(values => values.TdpUnified = unified, $"TdpUnified={unified}",
            cancellationToken: _lifetime).ConfigureAwait(false);
    }

    /// <summary>Turns AutoTDP on or off and persists the choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new setting is persisted.</returns>
    /// <remarks>
    ///     Persisted rather than session-only, and applied by the ordinary configuration reload, so the
    ///     overlay switch and the Settings checkbox are the same setting reached two ways.
    /// </remarks>
    internal Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default)
    {
        return SetAutoTdpEnabledAsync(null, cancellationToken);
    }

    /// <summary>Sets AutoTDP to an explicit state, or flips it, and persists the choice.</summary>
    /// <param name="requested">The state the caller asked for, or null to flip the persisted state.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the setting is persisted.</returns>
    /// <remarks>
    ///     The current state is read inside the transition gate, so concurrent surfaces cannot invert a
    ///     newer persisted choice and two quick toggles are both applied.
    /// </remarks>
    internal async Task SetAutoTdpEnabledAsync(
        bool? requested,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed(), typeof(DeviceCoordinator));
        using var transition = await _enterTransition(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_closed(), typeof(DeviceCoordinator));
        var enabled = requested ?? !AutoTdpEnabled;
        if (enabled && AutoTdp.Availability is { Available: false } availability)
        {
            throw new InvalidOperationException(availability.Detail);
        }

        if (AutoTdpEnabled == enabled)
        {
            Log.Info($"AutoTDP is already {(enabled ? "on" : "off")}; nothing to persist.");
            return;
        }

        await _persistConfiguration(
            config => config.DeviceIntegration.AutoTdpEnabled = enabled,
            cancellationToken).ConfigureAwait(false);
        Log.Info($"AutoTDP switched {(enabled ? "on" : "off")} from the Device surface.");
    }

    /// <summary>Stores AutoTDP's frame-rate target in the layer in force.</summary>
    /// <param name="fps">The target, within <see cref="AutoTdpTarget" />'s range.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task completing once the target is saved and announced.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The target lies outside the offered range.</exception>
    /// <remarks>
    ///     A profile value like the frame limit: it lands in the running game's profile while that is on
    ///     and in Global otherwise. Control reads it on its next window, so nothing restarts.
    /// </remarks>
    internal async Task SetAutoTdpTargetAsync(int fps, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed(), typeof(DeviceCoordinator));
        if (!AutoTdpTarget.IsValid(fps))
        {
            throw new ArgumentOutOfRangeException(nameof(fps), fps,
                $"The AutoTDP target must be between {AutoTdpTarget.MinimumFps} and {AutoTdpTarget.MaximumFps} FPS.");
        }

        await _profiles.SetAsync(ProfileField.AutoTdpTarget, fps, cancellationToken).ConfigureAwait(false);
        AutoTdp.RefreshPrerequisites();
    }

    /// <summary>Disables AutoTDP and waits for its restore before the device cycle stops.</summary>
    /// <param name="deadline">Bounds the wait like a device stop.</param>
    /// <param name="cancellationToken">The caller's token; only its cancellation propagates.</param>
    /// <returns>Completion once the restore finished or the bound passed.</returns>
    /// <remarks>
    ///     A restore still running at the bound keeps running and is never issued a second time; the
    ///     stop goes ahead and the log says the restore is unconfirmed.
    /// </remarks>
    internal async Task RestoreAutoTdpBeforeStopAsync(Deadline deadline, CancellationToken cancellationToken)
    {
        using var bounded = deadline.CreateCancellationSource(cancellationToken);
        try
        {
            await AutoTdp.DisableAsync().WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn("AutoTDP restoration is still running; the device stops without waiting for it.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Warn($"AutoTDP restoration was unverified before the device stop: {ex.Message}");
        }
    }

    /// <summary>Releases the AutoTDP trace once AutoTDP no longer writes rows.</summary>
    /// <returns>Completion of the trace's disposal.</returns>
    internal ValueTask DisposeAutoTdpTraceAsync()
    {
        return _autoTdpTrace.DisposeAsync();
    }

    /// <summary>Releases the AutoTDP frame-time reader once AutoTDP no longer reads frames.</summary>
    internal void DisposeAutoTdpFrametimes()
    {
        _autoTdpFrametimes.Dispose();
    }

    /// <summary>Releases the Windows power-mode notification subscription.</summary>
    internal void DisposePowerModeNotification()
    {
        _powerModeNotification?.Dispose();
    }

    /// <summary>Starts the power-assignment loop and the Windows power mode notification once.</summary>
    /// <remarks>
    ///     Only a device cycle needs them, so a session with integration off runs neither. Once started
    ///     they stay until shutdown; with integration switched off again the reconcile does nothing.
    /// </remarks>
    internal void EnsureLoops()
    {
        lock (_loopGate)
        {
            if (_closed() || _loopsStarted)
            {
                return;
            }

            _loopsStarted = true;
            _assignmentLoop = ObservePowerAssignmentsAsync();
            try
            {
                // A Windows power mode picked outside WSGM is adopted like an out-of-band watt change.
                _powerModeNotification = _registerPowerModeNotification(RequestAssignmentReconcile);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Windows power mode notifications are unavailable: {ex.Message}");
            }
        }
    }

    /// <summary>Wakes the power-assignment reconcile; any number of requests collapse into one pass.</summary>
    internal void RequestAssignmentReconcile()
    {
        _powerAssignmentChanges.Writer.TryWrite(true);
    }

    /// <summary>Wakes the assignment reconcile when a power control's reading or availability moves.</summary>
    /// <param name="views">The router's newest projection.</param>
    /// <remarks>
    ///     This is how presets that become available are applied and how a watt limit changed outside a
    ///     preset is adopted as Custom. The router raises this for every state publication, telemetry
    ///     included, so it compares three readings and allocates nothing.
    /// </remarks>
    internal void OnControlsChanged(IReadOnlyList<DeviceCapabilityView> views)
    {
        PowerControlReading sustained = default, slow = default, fast = default, scenario = default;
        for (var index = 0; index < views.Count; index++)
        {
            var view = views[index];
            switch (view.Descriptor.Role)
            {
                case CapabilityRole.PowerSustainedLimit:
                    sustained = PowerControlReading.Of(view);
                    break;
                case CapabilityRole.PowerSlowLimit:
                    slow = PowerControlReading.Of(view);
                    break;
                case CapabilityRole.PowerFastLimit:
                    fast = PowerControlReading.Of(view);
                    break;
                case CapabilityRole.ScenarioMode:
                    scenario = PowerControlReading.Of(view);
                    break;
            }
        }

        var reading = (sustained, slow, fast, scenario);
        if (reading == _powerControls)
        {
            return;
        }

        _powerControls = reading;
        RequestAssignmentReconcile();
    }

    /// <summary>
    ///     Restores the power limit the running application prefers, and takes back the one the outgoing
    ///     application imposed.
    /// </summary>
    /// <param name="manualProfile">The power limit the layers resolve to, or null when none is preferred.</param>
    /// <param name="applicationId">The running application, or null for the desktop.</param>
    /// <param name="cancellationToken">Cancels the device writes.</param>
    /// <returns>A task completing once the decision was carried out.</returns>
    /// <remarks>
    ///     Runs whole inside the power lane, so the imposed flags it decides from are the ones the manual
    ///     funnel last wrote, and no preset, AutoTDP or user write lands between the decision and its
    ///     writes. A power-source preset assignment in force owns the limits instead. The decision is
    ///     pure and tested (<see cref="PerApplicationPowerPolicy" />).
    /// </remarks>
    internal async Task ReconcileApplicationPowerAsync(
        ManualTdpProfile? manualProfile,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        if (_closed())
        {
            return;
        }

        await Lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed())
            {
                return;
            }

            var power = _capabilities.Snapshot().FirstOrDefault(view => view.Descriptor is
            {
                Role: CapabilityRole.PowerSustainedLimit,
                SupportsWrite: true,
                ValueKind: CapabilityValueKind.Integer
            });
            if (power is null || PowerAssignments.HasCurrentAssignment)
            {
                return;
            }

            var (effective, paired) = manualProfile is null
                ? (null, false)
                : manualProfile.Unified
                    ? (manualProfile.UnifiedWatts, true)
                    : (manualProfile.SustainedWatts, false);
            var ceiling = power.Descriptor.Maximum ?? 0;
            var autoTdpEnabled = AutoTdpEnabled;
            var decision = PerApplicationPowerPolicy.DecideOnTargetChange(
                effective,
                _profilePowerImposed,
                autoTdpEnabled,
                ceiling);
            var target = applicationId ?? "the global profile";
            switch (decision.Action)
            {
                case PerAppPowerAction.Apply:
                    var boost = manualProfile is { Unified: false }
                                && power.Descriptor.PairedPowerLimitId is { } peerId
                        ? _capabilities.TryGetView(new DeviceCapabilityKey(peerId, null))?.Projection.DesiredValue
                            ?.IntegerValue
                        : null;
                    // A value the user just set by hand is already on the device; writing it again would
                    // only pause AutoTDP a second time.
                    var applied = boost is { } boostWatts
                        ? await RestoreSplitPowerAsync(power, decision.Watts, boostWatts, cancellationToken)
                            .ConfigureAwait(false)
                        : power.Projection.State.ObservedValue?.IntegerValue == decision.Watts
                          || await ApplyProfilePowerLimitAsync(power, decision.Watts, paired, cancellationToken)
                              .ConfigureAwait(false);
                    if (applied)
                    {
                        // An explicit limit overrides automatic control exactly as moving the slider
                        // does; pausing while it is applied keeps AutoTDP from writing over it next tick.
                        if (autoTdpEnabled)
                        {
                            AutoTdp.NoteManualChange(decision.Watts);
                        }

                        _profilePowerImposed = true;
                        _profilePowerPaired = paired || boost is not null;
                        Log.Info($"Per-application power limit applied: {decision.Watts} W for {target}.");
                    }

                    break;
                case PerAppPowerAction.ResumeAutomatic:
                    AutoTdp.ResumeAutomaticControl();
                    _profilePowerImposed = false;
                    _profilePowerPaired = false;
                    Log.Info($"Per-application power limit released; automatic control resumes for {target}.");
                    break;
                case PerAppPowerAction.ReleaseToCeiling:
                    if (ceiling > 0
                        && await ApplyProfilePowerLimitAsync(power, ceiling, _profilePowerPaired, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        _profilePowerImposed = false;
                        _profilePowerPaired = false;
                        Log.Info(
                            $"Per-application power limit released to the device ceiling {ceiling} W for {target}.");
                    }

                    break;
                case PerAppPowerAction.Leave:
                    break;
                default:
                    Log.Warn($"Per-application power decision {decision.Action} is unknown; the limit is left as is.");
                    break;
            }
        }
        finally
        {
            Lane.Release();
        }
    }

    /// <summary>Pauses AutoTDP for a power write a person applied, and records a sustained limit.</summary>
    /// <param name="capabilityId">The capability that was commanded.</param>
    /// <param name="instanceId">Its instance, or null for a single-instance capability.</param>
    /// <param name="value">The requested value, or null for an action.</param>
    /// <param name="result">What the handheld reported.</param>
    /// <remarks>Runs from the manual funnel inside the power lane.</remarks>
    internal void NotifyManualPowerChange(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandResult result)
    {
        if (!result.Outcome.IsApplied())
        {
            return;
        }

        if (value?.Kind == CapabilityValueKind.Choice
            && FindDescriptor(capabilityId, instanceId)?.Role == CapabilityRole.ScenarioMode)
        {
            // AutoTDP's next limit write would leave the chosen firmware profile again (the Atlas
            // selects Manual for any PL1 write), so a hand-picked profile pauses it like a slider move.
            Log.Info($"AutoTDP paused: the {value.ChoiceValue} performance profile was chosen by hand.");
            AutoTdp.NoteManualChange(null);
            return;
        }

        if (value?.IntegerValue is not { } watts)
        {
            return;
        }

        var views = _capabilities.Snapshot();
        var primaryPowerLimit = views.Any(view =>
            view.Descriptor.Role is CapabilityRole.PowerSustainedLimit
            && string.Equals(view.Descriptor.CapabilityId, capabilityId, StringComparison.Ordinal)
            && string.Equals(view.Descriptor.InstanceId, instanceId, StringComparison.Ordinal));
        if (!primaryPowerLimit)
        {
            var primary = views.FirstOrDefault(view =>
                (view.Descriptor.PairedPowerLimitId == capabilityId
                 || (FindDescriptor(capabilityId, instanceId)?.Role is CapabilityRole.PowerFastLimit
                     or CapabilityRole.PowerPeakLimit
                     && view.Descriptor.Role == CapabilityRole.PowerSustainedLimit)) && instanceId is null);
            if (primary?.Projection.State.ObservedValue?.IntegerValue is { } sustained)
            {
                // A companion edit hands the runtime pair back without persisting observed
                // sustained wattage as a new primary preference.
                AutoTdp.NoteManualChange(sustained);
            }

            return;
        }

        // Permanent until the user resumes control, by specification: quietly taking the limit back
        // a few seconds after they set it by hand would make the manual control look broken.
        Log.Info($"AutoTDP paused: the sustained power limit was set to {watts} W by hand.");
        AutoTdp.NoteManualChange(watts);
        PersistManualPowerLimit(watts);
    }

    /// <summary>Hands a hand-set variable-refresh state to the performance profile that owns it.</summary>
    /// <param name="capabilityId">The capability that was commanded.</param>
    /// <param name="instanceId">Its instance, or null for a single-instance capability.</param>
    /// <param name="value">The requested value, or null for an action.</param>
    /// <param name="result">What the handheld reported.</param>
    /// <remarks>
    ///     The Device row and Steam's own control are two ways to press the same switch, and only the
    ///     second used to reach the profile. Routing both through here keeps one stored answer for the
    ///     feature instead of giving the overlay a second one under device integration.
    /// </remarks>
    internal void NotifyManualVariableRefreshChange(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandResult result)
    {
        if (value?.BooleanValue is not { } enabled
            || !result.Outcome.IsApplied()
            || FindDescriptor(capabilityId, instanceId)?.Role
                is not CapabilityRole.VariableRefreshRate)
        {
            return;
        }

        _manualVariableRefresh(enabled);
    }

    /// <summary>
    ///     Applies the power-source assignment whenever something it depends on changed: the power source,
    ///     the Windows power mode, the profile or running application, the device cycle and its restore, or
    ///     the power controls themselves.
    /// </summary>
    private async Task ObservePowerAssignmentsAsync()
    {
        var changes = _powerAssignmentChanges.Reader;
        try
        {
            while (await changes.WaitToReadAsync(_lifetime).ConfigureAwait(false))
            {
                changes.TryRead(out _);
                try
                {
                    await PowerAssignments.ReconcileAsync(_lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Change("power.assignment.failure", $"Power assignment failed: {ex.Message}", LogLevel.Warn);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task<CapabilityCommandResult> ExecutePresetCapabilityAsync(
        string id, CapabilityValue value, bool persist, CancellationToken token)
    {
        if (_closed())
        {
            return DeviceCapabilityCommands.ClosedAdmission();
        }

        var result = await _executeInLane(id, null, value,
            persist && value.Kind == CapabilityValueKind.Integer
                ? CapabilityCommandOrigin.User
                : CapabilityCommandOrigin.AutomaticControl,
            false, token).ConfigureAwait(false);
        if (!persist && value.IntegerValue is { } watts && result.Outcome.IsApplied()
            && FindDescriptor(id, null)?.Role == CapabilityRole.PowerSustainedLimit)
        {
            // An applied assignment pauses AutoTDP without saving its wattage as the user's limit.
            AutoTdp.NoteManualChange(watts);
        }

        return result;
    }

    private async Task SavePowerAssignmentAsync(DevicePowerAssignmentContext selection, bool ac,
        DevicePowerPresetReference? reference)
    {
        using var transition = await _enterTransition(_lifetime).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_closed(), typeof(DeviceCoordinator));
        if (_profiles.Current.Generation != selection.Profiles.Generation
            || _definition()?.FamilyId != selection.PluginId
            || IntegrationEnabled != selection.Enabled
            || selection.OnAc != _readOnAcPower())
        {
            throw new InvalidOperationException(
                "The running application, device or configuration changed before saving the assignment.");
        }

        await _profiles.SetAsync(values =>
            {
                if (ac)
                {
                    values.AcPowerPreset = reference;
                }
                else
                {
                    values.BatteryPowerPreset = reference;
                }
            }, $"{(ac ? "AC" : "battery")} power preset {reference?.PresetId ?? "(none)"}",
            cancellationToken: _lifetime).ConfigureAwait(false);
    }

    /// <summary>Writes a stored per-application limit; called inside the power lane.</summary>
    /// <returns>Whether the write was dispatched; a readback is never required.</returns>
    private async Task<bool> ApplyProfilePowerLimitAsync(
        DeviceCapabilityView power,
        int watts,
        bool paired,
        CancellationToken cancellationToken)
    {
        // Not a user action: the value is already the saved preference, so it must not re-enter the
        // manual funnel and be persisted again or re-resolved into the wrong layer.
        var result = await _executeInLane(power.Descriptor.CapabilityId, power.Descriptor.InstanceId,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
            CapabilityCommandOrigin.ProfileRestore,
            paired, cancellationToken).ConfigureAwait(false);
        var applied = result.Outcome.IsApplied();
        if (!applied)
        {
            Log.Warn($"Per-application power limit {watts} W was not applied: "
                     + (result.Reason?.Detail ?? result.Outcome.ToString()));
        }

        return applied;
    }

    /// <summary>Restores a split sustained/boost preference; called inside the power lane.</summary>
    /// <returns>Whether both writes were dispatched.</returns>
    private async Task<bool> RestoreSplitPowerAsync(DeviceCapabilityView primary, int sustained, int boost,
        CancellationToken cancellationToken)
    {
        var peerId = primary.Descriptor.PairedPowerLimitId;
        var peer = peerId is null ? null : _capabilities.TryGetView(new DeviceCapabilityKey(peerId, null));
        if (peer is null || !ManualTdpPolicy.Accepts(peer.Descriptor.Minimum, peer.Descriptor.Maximum,
                peer.Descriptor.Step, boost))
        {
            return false;
        }

        // The unified write moves both limits to the sustained target before the independent boost
        // preference is restored, so the boost write never finds the sustained limit above it.
        var pair = await _executeInLane(primary.Descriptor.CapabilityId, primary.Descriptor.InstanceId,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = sustained },
            CapabilityCommandOrigin.ProfileRestore,
            true, cancellationToken).ConfigureAwait(false);
        if (!pair.Outcome.IsApplied())
        {
            return false;
        }

        AutoTdp.NoteManualChange(sustained);
        var result = await _executeInLane(peerId!, null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = boost },
            CapabilityCommandOrigin.ProfileRestore,
            false, cancellationToken).ConfigureAwait(false);
        return result.Outcome.IsApplied();
    }

    /// <summary>Persists a hand-set power limit to whichever profile layer is in force.</summary>
    /// <param name="watts">The limit the user just set, already applied to the device.</param>
    /// <remarks>
    ///     Runs from the manual funnel inside the power lane, so the value has already reached the device
    ///     and paused AutoTDP. This only records it in the layer in force, the game profile while it is on
    ///     and Global otherwise, so the next launch restores it instead of the value leaking onto whatever
    ///     runs next.
    /// </remarks>
    private void PersistManualPowerLimit(int watts)
    {
        var layers = _profiles.Current.Layers;
        var manual = layers.ManualTdp();
        var key = layers.PowerTargetKey;
        var current = manual is null ? null : manual.Unified ? manual.UnifiedWatts : manual.SustainedWatts;

        // A drag that ends on the stored value writes no config.
        _profilePowerImposed = true;
        _profilePowerPaired = manual?.Unified == true;
        if (current == watts)
        {
            return;
        }

        _observe(_profiles.SetAsync(key.Field, watts), $"save of the {watts} W power limit");
    }

    private CapabilityDescriptor? FindDescriptor(string capabilityId, string? instanceId)
    {
        return _capabilities.TryGetView(new DeviceCapabilityKey(capabilityId, instanceId))?.Descriptor;
    }
}
