// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Device.Asus.RogAlly;

/// <summary>The hardware transports one plugin instance uses; replaced by fakes in tests.</summary>
internal sealed record AllyHardwareServices(
    IAllyIdentityReader Identity,
    IAsusAcpi Acpi,
    Func<AllyModel, IAllyVendorHid> Vendor,
    Func<AllyModel, IAllyAuraHid> Aura,
    Func<AllyModel, AllyOemButtonState, IAllyControllerSource> Controller,
    Func<AllyModel, IAllyMotionSource> Motion,
    IAllyKeyboardSource Keyboard);

/// <summary>The device plugin for the ASUS ROG Ally, Ally X, Xbox Ally and Xbox Ally X.</summary>
/// <remarks>
///     Built from Handheld Companion 1.3.1.6 and HHD without hardware. Each model's facts live in
///     <c>AllyModels</c>; PROVENANCE.md cites the source of every one and lists what a Device Lab report
///     must confirm.
/// </remarks>
public sealed class RogAllyPlugin : IDevicePlugin
{
    private const int MaxDiagnosticValueLength = 64;

    /// <summary>Well inside WSGM's 30-second freshness window, as the Claw plugin found necessary.</summary>
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(10);

    private static readonly string[] SourceOwnershipChoices = ["device", "plugin", "unavailable"];

    private static readonly IReadOnlyList<CapabilitySection> OverlaySections =
    [
        DeviceSections.Power with
        {
            Categories =
            [
                Category(CategoryIds.Limits, "Limits", 0),
                Category(CategoryIds.Charging, "Charging", 1),
                Category(CategoryIds.Fans, "Fans", 2)
            ]
        },
        DeviceSections.Rgb with { Categories = [Category(CategoryIds.Zones, "Zones", 0)] },
        DeviceSections.Info with
        {
            Categories =
            [
                Category(CategoryIds.Ownership, "Plugin ownership", 0),
                Category(CategoryIds.Readings, "Readings", 1)
            ]
        }
    ];

    private readonly SemaphoreSlim _commandSerializer = new(1, 1);
    private readonly AllyHardwareServices _hardware;
    private bool _active;
    private IAllyAuraHid? _aura;
    private AllyOemButtonState? _buttons;
    private ChargeLimitService? _charge;
    private ControllerService? _controller;
    private long _cycleGeneration;
    private AllyIdentityState? _cycleIdentity;
    private CapabilityDescriptorSet? _descriptorSet;
    private bool _disposed;
    private FanService? _fans;
    private IPluginHostAdapter? _host;
    private AllyRecoveryJournal? _journal;
    private KeyboardOemService? _keyboard;
    private LightingService? _lighting;
    private AllyModel? _model;
    private AllyMotionService? _motion;
    private IAllyMotionSource? _motionSource;
    private CancellationTokenSource? _observationLoop;
    private PowerService? _power;
    private bool _quiescing;
    private IReadOnlyList<AllyService> _services = [];
    private IAllyControllerSource? _source;
    private IAllyVendorHid? _vendor;
    private VendorEventService? _vendorEvents;

    /// <summary>Creates the production plugin with Windows transports.</summary>
    // ReSharper disable once UnusedMember.Global
    public RogAllyPlugin()
        : this(CreateWindowsServices())
    {
    }

    internal RogAllyPlugin(AllyHardwareServices hardware)
    {
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
    }

    /// <inheritdoc />
    public string PackageId => AllyModels.PackageId;

    /// <inheritdoc />
    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var model = AllyModels.Match(context.Identity);
        return ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = model is not null,
            DeviceDefinitionId = model?.DefinitionId,
            Reason = model is not null
                ? null
                : new CapabilityReason(CapabilityReasonCode.Unsupported,
                    "This package requires an ASUSTeK baseboard RC71L, RC72LA, RC72L, RC73YA or RC73XA.")
        });
    }

    /// <inheritdoc />
    public async ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
        {
            throw new InvalidOperationException("The plugin already owns a device cycle.");
        }

        if (AllyModels.ById(context.DeviceDefinitionId) is not { } definition)
        {
            throw new InvalidOperationException("WSGM supplied a device definition this package does not own.");
        }

        if (context.CycleGeneration != context.Host.CycleGeneration)
        {
            throw new InvalidOperationException("WSGM supplied an inconsistent cycle generation.");
        }

        PluginTrace.Install(context.Host);
        PluginTrace.Info("lifecycle", $"start: definition={definition.DefinitionId}, cycle={context.CycleGeneration}.");

        var identity = await _hardware.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (identity.Model != definition)
        {
            PluginTrace.Error("lifecycle",
                $"live identity {identity.Snapshot.BaseboardProduct ?? "<none>"} is not {definition.DefinitionId}.");
            throw new InvalidOperationException("The live SMBIOS identity no longer matches the detected Ally model.");
        }

        _host = context.Host;
        _model = definition;
        _cycleGeneration = context.CycleGeneration;
        _cycleIdentity = identity;
        _quiescing = false;
        try
        {
            _journal = await AllyRecoveryJournal.OpenAsync(context.StateDirectory, cancellationToken)
                .ConfigureAwait(false);
            _buttons = new AllyOemButtonState();
            _vendor = _hardware.Vendor(definition);
            _aura = _hardware.Aura(definition);
            _source = _hardware.Controller(definition, _buttons);
            _motionSource = _hardware.Motion(definition);
            _vendorEvents = new VendorEventService(_vendor, context.Host, _buttons);
            _keyboard = new KeyboardOemService(_hardware.Keyboard, context.Host, _buttons);
            _power = new PowerService(_hardware.Acpi, _journal);
            _fans = new FanService(_hardware.Acpi, _journal);
            _charge = new ChargeLimitService(_hardware.Acpi);
            _lighting = new LightingService(_aura);
            _motion = new AllyMotionService(_motionSource);
            _controller = new ControllerService(_source, _vendor, _motion, _keyboard, _buttons, context.Host,
                _journal)
            {
                Enabled = context.ControllerManagementEnabled
            };

            // Acquired in this order and released in reverse. The keyboard service precedes the
            // controller, which enables its rear keys, and motion precedes the controller it rides.
            _services = [_vendorEvents, _keyboard, _power, _fans, _charge, _lighting, _motion, _controller];
            BuildCapabilitySurface();

            if (_journal.FailureReason is { } journalFailure)
            {
                Block(journalFailure, _power, _fans, _controller);
            }
            else
            {
                await ReconcileOutstandingAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            await context.Host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken).ConfigureAwait(false);
            await context.Host.PublishOemControlsAsync(AllyModels.OemControls(definition), cancellationToken)
                .ConfigureAwait(false);
            await AcquireServicesAsync(Context(DateTimeOffset.UtcNow.AddSeconds(15)), cancellationToken)
                .ConfigureAwait(false);
            _active = true;
            await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
            StartObservationLoop();
            return CurrentStartResult();
        }
        catch
        {
            _quiescing = true;
            await RollBackFailedStartAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_active || _descriptorSet is null || _quiescing)
        {
            return AllyResults.Rejected(command, CapabilityReasonCode.Quiescing,
                "The Ally device cycle is inactive or quiescing.");
        }

        try
        {
            await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AllyResults.Rejected(command, CapabilityReasonCode.Quiescing,
                "The command was cancelled before its serialized hardware turn began.");
        }

        try
        {
            if (!_active || _descriptorSet is null || _quiescing)
            {
                return AllyResults.Rejected(command, CapabilityReasonCode.Quiescing,
                    "The Ally device cycle started quiescing before this command could run.");
            }

            var result = await ExecuteBoundCommandAsync(command, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is not (CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified))
            {
                return result;
            }

            var published = await PublishAfterCommandAsync(command, cancellationToken).ConfigureAwait(false);
            if (!published && command.CapabilityId == CapabilityIds.Scenario)
            {
                // A mode change resets the limits; a host must see them before ordering its next writes.
                return result with
                {
                    Outcome = CommandOutcome.Indeterminate,
                    ReadbackValue = null,
                    Rollback = RollbackResult.NotRequired,
                    Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable,
                        "The performance mode was written, but the resulting power limits could not be published.")
                };
            }

            return result;
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services.Count == 0)
        {
            return;
        }

        _quiescing = true;
        StopObservationLoop();
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var service in _services.Where(service => service.Suspendable).Reverse())
            {
                await OperateAsync(service, () => service.SuspendAsync(Context(context.Deadline), cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services.Count == 0 || _host is null)
        {
            return new PluginStartResult
            {
                State = PluginOperationalState.Passive,
                Reason = new CapabilityReason(CapabilityReasonCode.ResourceReleased,
                    "The Ally services have not been started.")
            };
        }

        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cycleGeneration = context.CycleGeneration;
            _cycleIdentity = await _hardware.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (_journal is not null && await _journal.CheckHealthAsync(cancellationToken).ConfigureAwait(false)
                    is { } journalFailure)
            {
                Block(journalFailure, _power, _fans, _controller);
            }

            foreach (var service in _services.Where(service =>
                         service.Suspendable || service.State is not AllyServiceState.Owned))
            {
                await AcquireAsync(service, Context(context.Deadline), cancellationToken).ConfigureAwait(false);
            }

            BuildCapabilitySurface();
            await _host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken).ConfigureAwait(false);
            _quiescing = false;
            await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
            StartObservationLoop();
            return CurrentStartResult();
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _controller is null || _quiescing
            ? ValueTask.CompletedTask
            : _controller.ApplyHapticsAsync(frame, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = _model?.DefinitionId ?? "none",
            ["cycle"] = _disposed ? "disposed" : _active ? _quiescing ? "quiescing" : "started" : "stopped",
            ["recovery"] = _journal is null ? "unavailable"
                : _journal.FailureReason is not null ? "blocked"
                : _journal.OutstandingEntries.Count == 0 ? "healthy" : "pending",
            ["validation"] = "blind"
        };
        foreach (var service in _services)
        {
            var state = service.State.ToString();
            values[service.ServiceId] =
                state.Length <= MaxDiagnosticValueLength ? state : state[..MaxDiagnosticValueLength];
        }

        return ValueTask.FromResult(new PluginDiagnostics { Values = values });
    }

    /// <inheritdoc />
    public async ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReleaseControllerCoreAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_controller is null)
            {
                return;
            }

            var previous = _cycleGeneration;
            _controller.Enabled = context.Enabled;
            _cycleGeneration = context.CycleGeneration;
            // The Claw plugin's rule: a fresh cycle generation resets the descriptor generation WSGM
            // accepts, so the surface is republished before any state under it.
            if (_cycleGeneration != previous && _host is not null && _descriptorSet is not null)
            {
                BuildCapabilitySurface();
                await _host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken).ConfigureAwait(false);
            }

            if (context.Enabled)
            {
                _cycleIdentity = await _hardware.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
                await AcquireAsync(_controller, Context(context.Deadline), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _ = await ReleaseControllerCoreAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
            }

            await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _quiescing = true;
        StopObservationLoop();
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_services.Count > 0)
            {
                await ReleaseServicesAsync(Context(context.Deadline), cancellationToken).ConfigureAwait(false);
            }

            var result = CurrentStopResult();
            _active = false;
            _descriptorSet = null;
            _services = [];
            await DisposeCycleTransportsAsync().ConfigureAwait(false);
            if (_journal is not null)
            {
                await _journal.DisposeAsync().ConfigureAwait(false);
                _journal = null;
            }

            return result;
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopObservationLoop();
        if (_active)
        {
            await StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting, DateTimeOffset.UtcNow.AddSeconds(12)),
                CancellationToken.None).ConfigureAwait(false);
        }

        await DisposeCycleTransportsAsync().ConfigureAwait(false);
        await _hardware.Keyboard.DisposeAsync().ConfigureAwait(false);
        _hardware.Acpi.Dispose();
        if (_journal is not null)
        {
            await _journal.DisposeAsync().ConfigureAwait(false);
            _journal = null;
        }

        _commandSerializer.Dispose();
    }

    private async ValueTask DisposeCycleTransportsAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
            _source = null;
        }

        if (_motionSource is not null)
        {
            await _motionSource.DisposeAsync().ConfigureAwait(false);
            _motionSource = null;
        }

        if (_vendor is not null)
        {
            await _vendor.DisposeAsync().ConfigureAwait(false);
            _vendor = null;
        }

        if (_aura is not null)
        {
            await _aura.DisposeAsync().ConfigureAwait(false);
            _aura = null;
        }
    }

    private async ValueTask<PluginControllerRelease> ReleaseControllerCoreAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (_controller is null)
        {
            return new PluginControllerRelease
            {
                Step = ControllerHandoffStep.TopologyVerified,
                Result = ControllerHandoffResult.ReleasedVerified
            };
        }

        var result = await _controller.ReleaseControllerAsync(deadline, cancellationToken).ConfigureAwait(false);
        return new PluginControllerRelease
        {
            // No mode switch happens on an Ally, so the topology is the one found at acquisition.
            Step = result is ControllerHandoffResult.ReleasedVerified
                ? ControllerHandoffStep.TopologyVerified
                : ControllerHandoffStep.TopologyUnverified,
            Result = result,
            ReleasedDevices = _controller.LastReleasedDevices
        };
    }

    private async ValueTask RollBackFailedStartAsync()
    {
        StopObservationLoop();
        if (_services.Count > 0 && _cycleIdentity is not null)
        {
            try
            {
                await ReleaseServicesAsync(Context(DateTimeOffset.UtcNow.AddSeconds(12)), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PluginTrace.Failure("lifecycle", "startup rollback could not release every service", ex);
            }
        }

        if (_host is not null)
        {
            await TryRetractAsync(() => _host.PublishPhysicalDevicesAsync([], null, CancellationToken.None))
                .ConfigureAwait(false);
            await TryRetractAsync(() => _host.PublishOemControlsAsync([], CancellationToken.None))
                .ConfigureAwait(false);
            if (_descriptorSet is { } published)
            {
                await TryRetractAsync(() => _host.PublishDescriptorsAsync(new CapabilityDescriptorSet
                {
                    Generation = checked(published.Generation + 1),
                    CycleGeneration = _cycleGeneration,
                    Descriptors = []
                }, CancellationToken.None)).ConfigureAwait(false);
            }
        }

        await DisposeCycleTransportsAsync().ConfigureAwait(false);
        if (_journal is not null)
        {
            await _journal.DisposeAsync().ConfigureAwait(false);
            _journal = null;
        }

        _active = false;
        _descriptorSet = null;
        _services = [];
    }

    private static async ValueTask TryRetractAsync(Func<ValueTask> retract)
    {
        try
        {
            await retract().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Failure("lifecycle", "startup rollback could not retract a publication", ex);
        }
    }

    private async ValueTask AcquireServicesAsync(AllyCycleContext context, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var service in _services)
            {
                await AcquireAsync(service, context, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseServicesAsync(context, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask ReleaseServicesAsync(AllyCycleContext context, CancellationToken cancellationToken)
    {
        for (var index = _services.Count - 1; index >= 0; index--)
        {
            var service = _services[index];
            AllyServiceResult result;
            try
            {
                result = await Invoke(service, () => service.ReleaseAsync(context, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new AllyServiceResult(AllyServiceState.ReleasedUnverified, new CapabilityReason(
                    CapabilityReasonCode.Quiescing, $"Release of '{service.ServiceId}' exceeded its deadline."));
            }

            service.ApplyResult(result.State is AllyServiceState.Idle or AllyServiceState.ReleasedUnverified
                or AllyServiceState.Faulted
                ? result
                : new AllyServiceResult(AllyServiceState.ReleasedUnverified, result.Reason));
        }
    }

    private static async ValueTask AcquireAsync(AllyService service, AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Invoke(service, () => service.AcquireAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        service.ApplyResult(result.State is AllyServiceState.Owned or AllyServiceState.Passive
            or AllyServiceState.Degraded or AllyServiceState.Faulted
            ? result
            : new AllyServiceResult(AllyServiceState.Faulted, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted, $"Acquisition returned invalid state {result.State}.")));
        PluginTrace.Info("lifecycle", $"{service.ServiceId}: {service.State}"
                                      + (service.Reason?.Detail is { } detail ? $" ({detail})" : string.Empty));
    }

    private static async ValueTask OperateAsync(AllyService service, Func<ValueTask<AllyServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        service.ApplyResult(await Invoke(service, operation, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<AllyServiceResult> Invoke(
        AllyService service,
        Func<ValueTask<AllyServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new AllyServiceResult(AllyServiceState.Faulted, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                AllyDiagnosticText.FromException($"Service '{service.ServiceId}' failed", ex)));
        }
    }

    private void BuildCapabilitySurface()
    {
        var model = _model ?? throw new InvalidOperationException("No model is selected.");
        List<CapabilityDescriptor> descriptors =
        [
            Integer(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit, DisplayKey.SustainedPowerLimit,
                    model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true, SectionIds.Power,
                    CategoryIds.Limits, 0) with
                {
                    PowerPresets = AllyModels.PowerPresets(model),
                    PairedPowerLimitId = CapabilityIds.PowerBoost,
                    Prominence = CapabilityProminence.Primary,
                    LayoutPair = new CapabilityLayoutPair(CapabilityIds.PowerBoost)
                },
            Integer(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit, DisplayKey.BoostPowerLimit,
                model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true, SectionIds.Power,
                CategoryIds.Limits, 1),
            Choice(CapabilityIds.Scenario, CapabilityRole.ScenarioMode, DisplayKey.PerformanceProfile,
                [Scenarios.Silent, Scenarios.Performance, Scenarios.Turbo], true, true, SectionIds.Power,
                CategoryIds.Limits, 2),
            Integer(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit, DisplayKey.ChargeLimit,
                    AllyChargeLimitCapability.MinimumPercent, AllyChargeLimitCapability.MaximumPercent,
                    CapabilityUnit.Percent, true, SectionIds.Power, CategoryIds.Charging, 0) with
                {
                    Persistence = CapabilityPersistence.DevicePersistent
                },
            // Write-only: the firmware has no readable "custom curve active" flag.
            Choice(CapabilityIds.FanMode, CapabilityRole.FanMode, DisplayKey.FanMode,
                [FanModes.Automatic, FanModes.Custom], false, true, SectionIds.Power, CategoryIds.Fans, 0),
            new()
            {
                CapabilityId = CapabilityIds.FanCurve,
                Role = CapabilityRole.FanCurve,
                SectionId = SectionIds.Power,
                CategoryId = CategoryIds.Fans,
                SortOrder = 1,
                ValueKind = CapabilityValueKind.Curve,
                Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
                Minimum = 0,
                Maximum = 100,
                Unit = CapabilityUnit.Percent,
                SupportsRead = true,
                SupportsWrite = true,
                Persistence = CapabilityPersistence.Volatile
            },
            Integer(CapabilityIds.FanReading, CapabilityRole.Telemetry, DisplayKey.Custom, 0, 100,
                    CapabilityUnit.Percent, false, SectionIds.Info, CategoryIds.Readings, 0,
                    CapabilityInstances.Cpu) with
                {
                    Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "CPU fan" }
                },
            Integer(CapabilityIds.FanReading, CapabilityRole.Telemetry, DisplayKey.Custom, 0, 100,
                    CapabilityUnit.Percent, false, SectionIds.Info, CategoryIds.Readings, 1,
                    CapabilityInstances.Gpu) with
                {
                    Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "GPU fan" }
                },
            // Aura is write-only (HC and HHD both only write it), so none of these declare a read.
            Integer(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness, DisplayKey.Brightness, 0, 100,
                    CapabilityUnit.Percent, true, SectionIds.Lighting, null, 0) with
                {
                    SupportsRead = false,
                    Persistence = CapabilityPersistence.Unknown
                },
            Choice(CapabilityIds.LightingEffect, CapabilityRole.LightingEffect, DisplayKey.LightingEffect,
                    [Effects.Solid, Effects.Breathing, Effects.ColorCycle, Effects.Rainbow], false, true,
                    SectionIds.Lighting, null, 1) with
                {
                    Persistence = CapabilityPersistence.Unknown
                },
            Integer(CapabilityIds.LightingSpeed, CapabilityRole.LightingEffectSpeed, DisplayKey.LightingEffectSpeed,
                    0, 100, CapabilityUnit.Percent, true, SectionIds.Lighting, null, 2) with
                {
                    SupportsRead = false,
                    Persistence = CapabilityPersistence.Unknown
                },
            Color(CapabilityInstances.Left, "Left stick ring", 0),
            Color(CapabilityInstances.Right, "Right stick ring", 1),
            Choice(CapabilityIds.Controller, CapabilityRole.ControllerSource, DisplayKey.Controller,
                SourceOwnershipChoices, true, false, SectionIds.Info, CategoryIds.Ownership, 0),
            Choice(CapabilityIds.Motion, CapabilityRole.MotionSource, DisplayKey.Motion, SourceOwnershipChoices, true,
                false, SectionIds.Info, CategoryIds.Ownership, 1),
            new()
            {
                CapabilityId = CapabilityIds.Rumble,
                Role = CapabilityRole.HapticSink,
                SectionId = SectionIds.Info,
                CategoryId = CategoryIds.Ownership,
                SortOrder = 2,
                ValueKind = CapabilityValueKind.None,
                Display = new CapabilityDisplay { Key = DisplayKey.Rumble },
                SupportsAction = true,
                Persistence = CapabilityPersistence.Volatile
            }
        ];

        _descriptorSet = new CapabilityDescriptorSet
        {
            Generation = 1,
            CycleGeneration = _cycleGeneration,
            Sections = OverlaySections,
            Descriptors = descriptors
        };
    }

    private async ValueTask<CapabilityCommandResult> ExecuteBoundCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        var descriptor = _descriptorSet?.Descriptors.FirstOrDefault(candidate =>
            candidate.CapabilityId == command.CapabilityId && candidate.InstanceId == command.InstanceId);
        var service = ServiceFor(command.CapabilityId);
        if (descriptor is null || service is null)
        {
            return AllyResults.Rejected(command, CapabilityReasonCode.Unsupported,
                $"Capability '{command.CapabilityId}' is not available.");
        }

        if (command.ApplyPowerPair && descriptor.PairedPowerLimitId is null)
        {
            return AllyResults.Rejected(command, CapabilityReasonCode.Unsupported,
                "This capability declares no power pair.");
        }

        AllyIdentityState identity;
        try
        {
            identity = await _hardware.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AllyResults.Rejected(command, CapabilityReasonCode.TransportFaulted,
                $"Identity revalidation failed: {ex.GetType().Name}.");
        }

        var refusal = Refusal(service, identity) ?? Validate(command, descriptor, identity.OnAcPower);
        if (refusal is not null)
        {
            return AllyResults.Rejected(command, refusal);
        }

        CapabilityCommandResult result;
        try
        {
            result = await ApplyAsync(command, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (AllyBudgetException ex)
        {
            // Thrown before any write, so nothing needs restoring and the service stays healthy.
            return AllyResults.Rejected(command, CapabilityReasonCode.Quiescing, ex.Message, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AllyResults.Indeterminate(command, CapabilityReasonCode.Quiescing,
                "The command was cancelled after hardware application began.", RollbackResult.RestoreFailed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AllyResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
                AllyDiagnosticText.FromException("The capability handler failed after admission", ex),
                RollbackResult.RestoreFailed);
        }

        if (result.Rollback is RollbackResult.RestoreFailed && service is PowerService or FanService)
        {
            service.Fault(new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                "A command rollback failed; the resource stays faulted until it is acquired again, and stop "
                + "restores the journalled original."));
        }

        return result.Outcome is not CommandOutcome.AppliedVerified && result.ReadbackValue is not null
            ? result with { ReadbackValue = null }
            : result;
    }

    private async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        AllyIdentityState identity,
        CancellationToken cancellationToken)
    {
        var value = command.RequestedValue!;
        switch (command.CapabilityId)
        {
            case CapabilityIds.PowerSustained:
                AllyWriteBudget.Require(command.Deadline, "power limit");
                if (!await _power!.PrepareWriteAsync(identity, cancellationToken).ConfigureAwait(false))
                {
                    return AllyResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "The current power limits and mode must be readable before they can be changed.");
                }

                return await _power.Capability!
                    .ApplySustainedAsync(command, value.IntegerValue!.Value, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.PowerBoost:
                AllyWriteBudget.Require(command.Deadline, "power limit");
                if (!await _power!.PrepareWriteAsync(identity, cancellationToken).ConfigureAwait(false))
                {
                    return AllyResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "The current power limits and mode must be readable before they can be changed.");
                }

                return await _power.Capability!.ApplyBoostAsync(command, value.IntegerValue!.Value, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.Scenario:
                AllyWriteBudget.Require(command.Deadline, "performance mode");
                if (!await _power!.PrepareWriteAsync(identity, cancellationToken).ConfigureAwait(false))
                {
                    return AllyResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "The current power limits and mode must be readable before they can be changed.");
                }

                return await _power.Capability!.ApplyScenarioAsync(command, value.ChoiceValue!, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.ChargeLimit:
                return _charge!.Capability!.Apply(command, value.IntegerValue!.Value);
            case CapabilityIds.FanCurve:
                AllyWriteBudget.Require(command.Deadline, "fan curve");
                if (!await _fans!.PrepareWriteAsync(identity, cancellationToken).ConfigureAwait(false))
                {
                    return AllyResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "The current fan curves must be readable before they can be changed.");
                }

                return await _fans.Capability!.ApplyCurveAsync(command, value.CurveValue, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.FanMode:
                return await ApplyFanModeAsync(command, identity, value.ChoiceValue!, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.LightingBrightness:
                return await _lighting!.ApplyAsync(command,
                        state => state with { Brightness = value.IntegerValue!.Value }, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.LightingSpeed:
                return await _lighting!.ApplyAsync(command,
                    state => state with { Speed = value.IntegerValue!.Value }, cancellationToken).ConfigureAwait(false);
            case CapabilityIds.LightingEffect:
                return await _lighting!.ApplyAsync(command,
                        state => state with { Effect = Effects.Parse(value.ChoiceValue!) }, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.LightingColor:
                return await _lighting!.ApplyAsync(command, state => command.InstanceId == CapabilityInstances.Left
                    ? state with { LeftColor = value.ColorValue!.Value }
                    : state with { RightColor = value.ColorValue!.Value }, cancellationToken).ConfigureAwait(false);
            default:
                return AllyResults.Rejected(command, CapabilityReasonCode.Unsupported, "This capability is read-only.");
        }
    }

    private async ValueTask<CapabilityCommandResult> ApplyFanModeAsync(
        CapabilityCommand command,
        AllyIdentityState identity,
        string mode,
        CancellationToken cancellationToken)
    {
        var fans = _fans!;
        if (mode == FanModes.Automatic)
        {
            AllyWriteBudget.Require(command.Deadline, "fan mode");
            if (!await fans.PrepareWriteAsync(identity, cancellationToken).ConfigureAwait(false))
            {
                return AllyResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                    "The current fan curves must be readable before they can be changed.");
            }

            return await fans.Capability!.ApplyAutomaticAsync(command, fans.Original, cancellationToken)
                .ConfigureAwait(false);
        }

        // There is no custom-mode firmware switch. The next curve command changes the fans.
        return AllyResults.Unverified(command, "Custom mode is ready; send a fan curve to change the fans.");
    }

    private AllyService? ServiceFor(string capabilityId)
    {
        return capabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power,
            CapabilityIds.ChargeLimit => _charge,
            CapabilityIds.FanMode or CapabilityIds.FanCurve or CapabilityIds.FanReading => _fans,
            CapabilityIds.LightingBrightness or CapabilityIds.LightingEffect or CapabilityIds.LightingSpeed
                or CapabilityIds.LightingColor => _lighting,
            CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
            CapabilityIds.Motion => _motion,
            _ => null
        };
    }

    private static CapabilityReason? Refusal(AllyService service, AllyIdentityState identity)
    {
        if (!identity.ExactMachineMatch)
        {
            return new CapabilityReason(CapabilityReasonCode.GenerationChanged,
                "The live identity no longer matches the Ally model.", true);
        }

        return service.State switch
        {
            AllyServiceState.Owned => null,
            AllyServiceState.Passive => new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                service.Reason?.Detail ?? "The device service is unavailable."),
            AllyServiceState.Releasing => new CapabilityReason(CapabilityReasonCode.Quiescing,
                "The device service is being released."),
            AllyServiceState.Degraded or AllyServiceState.Faulted or AllyServiceState.ReleasedUnverified =>
                new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    service.Reason?.Detail ?? "The device service is faulted."),
            _ => new CapabilityReason(CapabilityReasonCode.ResourceReleased,
                "The plugin does not currently own this device service.", true)
        };
    }

    private CapabilityReason? Validate(CapabilityCommand command, CapabilityDescriptor descriptor, bool onAcPower)
    {
        if (_descriptorSet is null || command.ExpectedDescriptorGeneration != _descriptorSet.Generation
                                   || command.ExpectedCycleGeneration != _cycleGeneration)
        {
            return new CapabilityReason(CapabilityReasonCode.GenerationChanged,
                "The command targets a descriptor or device generation that is no longer current.", true);
        }

        if (!AllyWriteBudget.IsAvailable(command.Deadline))
        {
            return new CapabilityReason(CapabilityReasonCode.Quiescing,
                "The command deadline leaves too little time for a hardware write.", true);
        }

        if (onAcPower ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc)
        {
            return new CapabilityReason(CapabilityReasonCode.UnavailableOnPowerSource,
                "The capability is unavailable on this power source.");
        }

        if (command.RequestedValue is not { } value)
        {
            return descriptor.SupportsAction
                ? new CapabilityReason(CapabilityReasonCode.Unsupported, "Rumble is driven through haptic frames.")
                : new CapabilityReason(CapabilityReasonCode.Unsupported, "The capability is not an action.");
        }

        if (!descriptor.SupportsWrite)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported, "The capability is read-only.");
        }

        if (value.Kind != descriptor.ValueKind)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported,
                $"Value kind {value.Kind} does not match {descriptor.ValueKind}.");
        }

        return descriptor.ValueKind switch
        {
            CapabilityValueKind.Integer when value.IntegerValue is not { } integer
                                             || integer < descriptor.Minimum || integer > descriptor.Maximum =>
                OutOfRange("The value is outside the declared range."),
            CapabilityValueKind.Choice when descriptor.Choices.All(choice => choice.Value != value.ChoiceValue) =>
                OutOfRange("The choice is not one of the declared options."),
            CapabilityValueKind.Color when value.ColorValue is not (>= 0 and <= 0xFFFFFF) =>
                OutOfRange("The colour must be 24-bit RGB."),
            CapabilityValueKind.Curve when value.CurveValue.Count == 0 => OutOfRange("The curve has no points."),
            _ => null
        };
    }

    private static CapabilityReason OutOfRange(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange, detail);
    }

    private async ValueTask PublishStatesAsync(CancellationToken cancellationToken)
    {
        if (_host is null || _descriptorSet is null)
        {
            return;
        }

        foreach (var descriptor in _descriptorSet.Descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ServiceFor(descriptor.CapabilityId) is not { } service)
            {
                continue;
            }

            var value = descriptor.SupportsRead ? CurrentState(descriptor) : null;
            await _host.PublishCapabilityStateAsync(new CapabilityState
            {
                CapabilityId = descriptor.CapabilityId,
                InstanceId = descriptor.InstanceId,
                Available = service.State is AllyServiceState.Owned,
                Reason = service.State is AllyServiceState.Owned ? null : service.Reason ?? ReasonFor(service.State),
                ObservedValue = value,
                Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
                ObservedAt = value is null ? null : DateTimeOffset.UtcNow,
                DescriptorGeneration = _descriptorSet.Generation,
                CycleGeneration = _cycleGeneration
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private CapabilityValue? CurrentState(CapabilityDescriptor descriptor)
    {
        switch (descriptor.CapabilityId)
        {
            case CapabilityIds.PowerSustained:
                return _power?.LastObserved?.Sustained is { } sustained && InRange(sustained, descriptor)
                    ? CapabilityValue.Integer(sustained)
                    : null;
            case CapabilityIds.PowerBoost:
                // The boost descriptor drives SPPT and FPPT together; SPPT stands for the pair.
                return _power?.LastObserved?.Slow is { } slow && InRange(slow, descriptor)
                    ? CapabilityValue.Integer(slow)
                    : null;
            case CapabilityIds.Scenario:
                return _power?.LastObserved?.Mode is { } mode && AllyModels.ScenarioName(mode) is { } name
                    ? CapabilityValue.Choice(name)
                    : null;
            case CapabilityIds.ChargeLimit:
                return _charge?.LastObserved is { } percent && InRange(percent, descriptor)
                    ? CapabilityValue.Integer(percent)
                    : null;
            case CapabilityIds.FanCurve:
                return _fans?.LastObserved?.Cpu is { } curve
                    ? CapabilityValue.Curve(AllyFanCapability.Decode(curve))
                    : null;
            case CapabilityIds.FanReading:
                var reading = descriptor.InstanceId == CapabilityInstances.Cpu
                    ? _fans?.LastFans.Cpu
                    : _fans?.LastFans.Gpu;
                return reading is { } fan && InRange(fan, descriptor) ? CapabilityValue.Integer(fan) : null;
            case CapabilityIds.Rumble:
                return CapabilityValue.None();
            case CapabilityIds.Controller:
                return CapabilityValue.Choice(Ownership(_controller?.State));
            case CapabilityIds.Motion:
                return CapabilityValue.Choice(Ownership(_motion?.State));
            default:
                return null;
        }
    }

    private static bool InRange(int value, CapabilityDescriptor descriptor)
    {
        return value >= descriptor.Minimum && value <= descriptor.Maximum;
    }

    private static string Ownership(AllyServiceState? state)
    {
        return state switch
        {
            AllyServiceState.Owned or AllyServiceState.Releasing => "plugin",
            AllyServiceState.Idle or AllyServiceState.Passive or AllyServiceState.Acquiring => "device",
            _ => "unavailable"
        };
    }

    private void StartObservationLoop()
    {
        StopObservationLoop();
        CancellationTokenSource loop = new();
        _observationLoop = loop;
        _ = Task.Run(() => ObservationLoopAsync(loop.Token), loop.Token);
    }

    private void StopObservationLoop()
    {
        var loop = _observationLoop;
        _observationLoop = null;
        if (loop is null)
        {
            return;
        }

        try
        {
            loop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        loop.Dispose();
    }

    private async Task ObservationLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(ObservationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_active || _quiescing || _disposed)
                {
                    continue;
                }

                if (!await _commandSerializer.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    Refresh(null);
                    await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PluginTrace.Failure("observe", "periodic observation refresh failed", ex);
                }
                finally
                {
                    _commandSerializer.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Re-reads what one capability, or every capability, observes.</summary>
    private void Refresh(string? capabilityId)
    {
        try
        {
            if (_power is { State: AllyServiceState.Owned } && capabilityId is null
                    or CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario)
            {
                _power.Refresh();
            }

            if (_fans is { State: AllyServiceState.Owned } && capabilityId is null
                    or CapabilityIds.FanCurve or CapabilityIds.FanMode or CapabilityIds.Scenario)
            {
                _fans.Refresh();
            }

            if (_charge is { State: AllyServiceState.Owned } && capabilityId is null or CapabilityIds.ChargeLimit)
            {
                _charge.Refresh();
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            // A stale reading is modelled by WSGM's freshness policy; the loop keeps observing.
            PluginTrace.Change("observe", "acpi", AllyDiagnosticText.FromException("ATKACPI read failed", ex));
        }
    }

    private async ValueTask<bool> PublishAfterCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        if (_quiescing)
        {
            // Suspend or stop has begun and publishes the final states itself.
            PluginTrace.Info("observe", $"post-command refresh for '{command.CapabilityId}' skipped while quiescing.");
            return false;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = command.Deadline - DateTimeOffset.UtcNow;
        bounded.CancelAfter(remaining <= TimeSpan.Zero ? TimeSpan.Zero
            : remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2));
        try
        {
            Refresh(command.CapabilityId);
            await PublishStatesAsync(bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Failure("observe", $"post-command refresh for '{command.CapabilityId}' failed", ex);
            return false;
        }
    }

    private async ValueTask ReconcileOutstandingAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        if (_journal is null || _model is null)
        {
            return;
        }

        foreach (var entry in _journal.OutstandingEntries)
        {
            var current = entry.ServiceId == AllyServiceIds.Controller
                ? AllyServiceIds.McuFirmware
                : identity.FirmwareIdentity;
            var action = AllyRecoveryJournal.Decide(entry, current);
            var service = entry.ServiceId switch
            {
                AllyServiceIds.Power => (AllyService?)_power,
                AllyServiceIds.Fans => _fans,
                AllyServiceIds.Controller => _controller,
                _ => null
            };
            if (action is AllyReconciliationAction.Block)
            {
                // Not retried and not blocking: the service stays usable, and the next explicit command
                // re-arms the captured original for the following release.
                PluginTrace.Warn("recovery",
                    $"'{entry.ServiceId}' kept an unresolved {entry.Status} restore; it is not retried automatically.");
                continue;
            }

            if (action is AllyReconciliationAction.ReportOnly)
            {
                Block(new CapabilityReason(CapabilityReasonCode.FirmwareNotVerified,
                    "An outstanding recovery entry is not safe to restore on this firmware."), service);
                continue;
            }

            bool restored;
            try
            {
                restored = await RestoreEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                PluginTrace.Failure("recovery", $"restoring '{entry.ServiceId}' failed", ex);
                restored = false;
            }

            await _journal.CompleteAsync(entry.ServiceId,
                restored ? AllyRecoveryStatus.RestoredVerified : AllyRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
            PluginTrace.Info("recovery", $"outstanding '{entry.ServiceId}' restored={restored}.");
            if (!restored)
            {
                Block(new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    "An outstanding hardware state could not be restored and verified."), service);
            }
        }
    }

    private async ValueTask<bool> RestoreEntryAsync(AllyRecoveryEntry entry, CancellationToken cancellationToken)
    {
        switch (entry.ServiceId)
        {
            case AllyServiceIds.Power when entry.OriginalState.ToPower() is { } power && _hardware.Acpi.TryOpen():
                return await new AllyPowerCapability(_hardware.Acpi, _model!).RestoreAsync(power, cancellationToken)
                    .ConfigureAwait(false);
            case AllyServiceIds.Fans when entry.OriginalState.ToFans() is { } fans && _hardware.Acpi.TryOpen():
                var capability = new AllyFanCapability(_hardware.Acpi);
                capability.Probe();
                return await capability.RestoreAsync(fans, cancellationToken).ConfigureAwait(false);
            case AllyServiceIds.Controller when _vendor is not null
                                                && await _vendor.IsAvailableAsync(cancellationToken)
                                                    .ConfigureAwait(false):
                foreach (var report in AllyProtocol.DefaultConfiguration)
                {
                    await _vendor.WriteConfigurationAsync(report, cancellationToken).ConfigureAwait(false);
                }

                // Acknowledged is the most the MCU can say about its tables.
                return true;
            default:
                return false;
        }
    }

    private static void Block(CapabilityReason reason, params AllyService?[] services)
    {
        foreach (var service in services)
        {
            service?.ReconciliationBlockReason = reason;
        }
    }

    private AllyCycleContext Context(DateTimeOffset deadline)
    {
        return new AllyCycleContext(_cycleGeneration, deadline,
            _cycleIdentity ?? throw new InvalidOperationException("No cycle identity is available."));
    }

    private PluginStartResult CurrentStartResult()
    {
        var required = _services.Where(service => service != _controller || _controller.Enabled).ToArray();
        var owned = required.Count(service => service.State is AllyServiceState.Owned);
        var firstUnhealthy = required.FirstOrDefault(service => service.State is not AllyServiceState.Owned);
        StringBuilder detail = new();
        foreach (var service in _services)
        {
            detail.Append(detail.Length > 0 ? ", " : string.Empty).Append(service.ServiceId).Append('=')
                .Append(service.State);
        }

        var state = owned == 0 ? PluginOperationalState.Passive
            : firstUnhealthy is null ? PluginOperationalState.Active
            : PluginOperationalState.Degraded;
        _host?.Trace(state is PluginOperationalState.Active ? DeviceTraceLevel.Info : DeviceTraceLevel.Warn,
            "lifecycle", $"start state {state}: {detail}");
        return new PluginStartResult
        {
            State = state,
            Reason = firstUnhealthy?.Reason ?? (owned == 0
                ? new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                    "No Ally hardware service could be acquired.")
                : null)
        };
    }

    private PluginStopResult CurrentStopResult()
    {
        if (_services.FirstOrDefault(service => service.State is AllyServiceState.Faulted) is { } failed)
        {
            return new PluginStopResult
            {
                Status = PluginStopStatus.Failed,
                Reason = failed.Reason ?? new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    $"Service '{failed.ServiceId}' cleanup failed.")
            };
        }

        return _services.FirstOrDefault(service => service.State is AllyServiceState.ReleasedUnverified) is
            { } unverified
            ? new PluginStopResult
            {
                Status = PluginStopStatus.Unverified,
                Reason = unverified.Reason ?? new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    $"Service '{unverified.ServiceId}' cleanup was not verified.")
            }
            : new PluginStopResult { Status = PluginStopStatus.Clean };
    }

    private static CapabilityReason ReasonFor(AllyServiceState state)
    {
        return state switch
        {
            AllyServiceState.Passive => new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing),
            AllyServiceState.Releasing => new CapabilityReason(CapabilityReasonCode.Quiescing),
            AllyServiceState.Degraded or AllyServiceState.Faulted or AllyServiceState.ReleasedUnverified =>
                new CapabilityReason(CapabilityReasonCode.TransportFaulted),
            _ => new CapabilityReason(CapabilityReasonCode.ResourceReleased)
        };
    }

    private static CapabilityCategory Category(string id, string title, int order)
    {
        return new CapabilityCategory
        {
            CategoryId = id,
            Key = SettingSectionKey.Custom,
            CustomTitle = title,
            SortOrder = order
        };
    }

    private static CapabilityDescriptor Integer(
        string id,
        CapabilityRole role,
        DisplayKey display,
        int minimum,
        int maximum,
        CapabilityUnit unit,
        bool writable,
        string section,
        string? category,
        int order,
        string? instance = null)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            InstanceId = instance,
            Role = role,
            SectionId = section,
            CategoryId = category,
            SortOrder = order,
            Prominence = writable ? CapabilityProminence.Normal : CapabilityProminence.Compact,
            ValueKind = CapabilityValueKind.Integer,
            Display = display is DisplayKey.Custom
                ? new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = id }
                : new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Unit = unit,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor Choice(
        string id,
        CapabilityRole role,
        DisplayKey display,
        IReadOnlyList<string> choices,
        bool readable,
        bool writable,
        string section,
        string? category,
        int order)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,
            SectionId = section,
            CategoryId = category,
            SortOrder = order,
            ValueKind = CapabilityValueKind.Choice,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = readable,
            SupportsWrite = writable,
            Choices =
            [
                .. choices.Select(choice => new CapabilityChoice(choice,
                    new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = Label(choice) }))
            ],
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor Color(string instance, string label, int order)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.LightingColor,
            InstanceId = instance,
            Role = CapabilityRole.LightingZoneColor,
            SectionId = SectionIds.Lighting,
            CategoryId = CategoryIds.Zones,
            SortOrder = order,
            ValueKind = CapabilityValueKind.Color,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
            SupportsRead = false,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Unknown
        };
    }

    private static string Label(string choice)
    {
        return choice switch
        {
            Scenarios.Silent => "Silent",
            Scenarios.Performance => "Performance",
            Scenarios.Turbo => "Turbo",
            FanModes.Automatic => "Automatic",
            FanModes.Custom => "Custom",
            Effects.Solid => "Solid",
            Effects.Breathing => "Breathing",
            Effects.ColorCycle => "Colour cycle",
            Effects.Rainbow => "Rainbow",
            "device" => "Device",
            "plugin" => "Plugin",
            "unavailable" => "Unavailable",
            _ => choice
        };
    }

    private static AllyHardwareServices CreateWindowsServices()
    {
        return new AllyHardwareServices(
            new WindowsAllyIdentityReader(),
            new WindowsAsusAcpi(),
            model => new WindowsAllyVendorHid(model.ControllerProductIds),
            model => new WindowsAllyAuraHid(model.ControllerProductIds),
            (model, buttons) => new WindowsAllyControllerSource(model, buttons),
            model => new WindowsAllyMotionSource(model),
            new WindowsAllyKeyboardHook());
    }
}

internal static class CapabilityIds
{
    public const string PowerSustained = "power.primary-limit";
    public const string PowerBoost = "power.boost-limit";
    public const string Scenario = "power.scenario";
    public const string ChargeLimit = "battery.charge-limit";
    public const string FanMode = "fan.mode";
    public const string FanCurve = "fan.curve";
    public const string FanReading = "fan.reading";
    public const string LightingBrightness = "lighting.brightness";
    public const string LightingEffect = "lighting.effect";
    public const string LightingSpeed = "lighting.effect-speed";
    public const string LightingColor = "lighting.zone-color";
    public const string Controller = "controller.source";
    public const string Motion = "motion.source";
    public const string Rumble = "haptic.rumble";
}

internal static class CapabilityInstances
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Left = "left";
    public const string Right = "right";
}

internal static class SectionIds
{
    public const string Power = DeviceSections.PowerId;
    public const string Lighting = DeviceSections.RgbId;
    public const string Info = DeviceSections.InfoId;
}

internal static class CategoryIds
{
    public const string Limits = "limits";
    public const string Charging = "charging";
    public const string Fans = "fans";
    public const string Zones = "zones";
    public const string Ownership = "ownership";
    public const string Readings = "readings";
}

internal static class Effects
{
    public const string Solid = "solid";
    public const string Breathing = "breathing";
    public const string ColorCycle = "color-cycle";
    public const string Rainbow = "rainbow";

    public static AuraEffect Parse(string value)
    {
        return value switch
        {
            Breathing => AuraEffect.Breathing,
            ColorCycle => AuraEffect.ColorCycle,
            Rainbow => AuraEffect.Rainbow,
            _ => AuraEffect.Solid
        };
    }
}
