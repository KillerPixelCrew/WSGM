using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;
using WSGM.Device.Sdk.Windows;

namespace WSGM.Device.Msi.Claw;

/// <summary>The device plugin for the MSI Claw family; <see cref="ClawModels" /> lists every supported board.</summary>
public sealed class ClawPlugin : IDevicePlugin
{
    private const int MaxDiagnosticValueLength = 64;

    /// <summary>How often the plugin re-reads and republishes what it observes.</summary>
    /// <remarks>
    ///     Comfortably inside WSGM's 30-second freshness policy, so an observation is replaced twice
    ///     before it can expire. Without this loop the plugin published state at start, at resume, and
    ///     after a command, and never again — so every readable capability went stale thirty seconds
    ///     into the cycle and stayed that way until the user happened to change something. The visible
    ///     form was the QAM's TDP row disappearing, taking AutoTDP ("No primary power limit is
    ///     available to control") with it.
    /// </remarks>
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(10);

    /// <summary>The Claw's declared Device overlay layout.</summary>
    /// <remarks>
    ///     Titles and icons are WSGM-owned vocabulary; only the grouping is this plugin's. A section a
    ///     unit leaves empty is dropped by WSGM rather than declared conditionally, so the layout stays
    ///     one static fact.
    /// </remarks>
    private static readonly IReadOnlyList<CapabilitySection> OverlaySections =
    [
        DeviceSections.Power with
        {
            Categories =
            [
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Limits,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Limits",
                    SortOrder = 0
                },
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Charging,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Charging",
                    SortOrder = 1
                },
                // Fans and thermals were their own Cooling section; folded in here so Power is one
                // page instead of two that both read as "power" to the user (maintainer-directed).
                // The readings that used to follow them moved to Info for the same reason: this is
                // the page for changing how the device behaves, not for watching it.
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Control,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Fans",
                    SortOrder = 2
                }
            ]
        },
        DeviceSections.Rgb with
        {
            Categories =
            [
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Zones,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Zones",
                    SortOrder = 0
                }
            ]
        },
        DeviceSections.Info with
        {
            Categories =
            [
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Ownership,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Plugin ownership",
                    SortOrder = 0
                },
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Readings,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Readings",
                    SortOrder = 1
                }
            ]
        }
    ];

    /// <summary>Who currently owns a physical input source.</summary>
    /// <remarks>
    ///     Ordered so the first value is the resting state. <c>device</c> means the Claw's own firmware
    ///     still has it, <c>plugin</c> means this plugin acquired it, and <c>unavailable</c> covers both
    ///     a failed acquisition and a source this unit does not expose — a user reading the row needs
    ///     those to be distinguishable, which is exactly what the previous boolean threw away.
    /// </remarks>
    private static readonly string[] SourceOwnershipChoices = ["device", "plugin", "unavailable"];

    private readonly SemaphoreSlim _commandSerializer = new(1, 1);
    private readonly ClawHardwareServices _services;
    private bool _active;
    private ChargeLimitService? _chargeLimit;
    private ClawChargeLimitCapability? _chargeLimitCapability;
    private ControllerService? _controller;
    private long _cycleGeneration;
    private ClawIdentityState? _cycleIdentity;
    private ClawModel? _cycleModel;
    private IReadOnlyList<ClawCycleService> _cycleServices = [];
    private CapabilityDescriptorSet? _descriptorSet;
    private bool _disposed;
    private ClawFanCapability? _fanCapability;
    private FanService? _fans;
    private IPluginHostAdapter? _host;
    private ClawRecoveryJournal? _journal;
    private LightingService? _lighting;
    private ClawLightingCapability? _lightingCapability;
    private MotionService? _motion;

    private CancellationTokenSource? _observationLoop;
    private CancellationToken _observationToken;
    private OemEventService? _oem;
    private PowerService? _power;
    private ClawPowerCapability? _powerCapability;
    private bool _quiescing;
    private ChordSuppressorService? _suppressor;
    private IReadOnlyList<ClawSuspendableService> _suspendableServices = [];
    private TelemetryService? _telemetry;

    /// <summary>Creates the production plugin with Windows hardware transports.</summary>
    // ReSharper disable once UnusedMember.Global
    public ClawPlugin()
        : this(CreateWindowsServices())
    {
    }

    internal ClawPlugin(ClawHardwareServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>The model this cycle started on. Only a started cycle has one; nothing falls back to a default.</summary>
    private ClawModel Model => _cycleModel ?? throw new InvalidOperationException("No device cycle is active.");

    /// <inheritdoc />
    public string PackageId => ClawHardwareFacts.PackageId;

    /// <inheritdoc />
    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var model = ClawModels.Find(context.Identity);
        return ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = model is not null,
            DeviceDefinitionId = model?.DefinitionId,
            Reason = model is not null
                ? null
                : new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    "This package requires manufacturer Micro-Star and a Claw baseboard: "
                    + string.Join(", ", ClawModels.All.Select(candidate => candidate.BoardProduct)) + ".")
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

        var definedModel = ClawModels.FindByDefinition(context.DeviceDefinitionId)
                           ?? throw new InvalidOperationException("WSGM supplied a different device definition.");

        if (context.CycleGeneration != context.Host.CycleGeneration)
        {
            throw new InvalidOperationException("WSGM supplied an inconsistent cycle generation.");
        }

        // Installed before the first hardware read, because startup is when the failures worth
        // tracing happen and an ambient sink installed late traces nothing that mattered.
        PluginTrace.Install(context.Host);
        PluginTrace.Info(
            "lifecycle",
            $"start: definition={context.DeviceDefinitionId}, cycle={context.CycleGeneration}.");

        var identity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || identity.Model != definedModel)
        {
            PluginTrace.Error("lifecycle", $"the {definedModel.BoardProduct} activation gate no longer matches.");
            throw new InvalidOperationException($"The {definedModel.BoardProduct} activation gate no longer matches.");
        }

        PluginTrace.Info(
            "lifecycle",
            $"model: {definedModel.DisplayName} ({definedModel.BoardProduct}, HC {definedModel.HcClass}, "
            + (definedModel.HardwareVerified ? "hardware-verified" : "built from HC, no hardware pass")
            + $"); sku={identity.Snapshot.SystemSku ?? "<none>"}, ec={identity.Snapshot.EcFirmwareVersion ?? "<none>"}, "
            + $"mcu={identity.Snapshot.McuFirmwareVersion ?? "<none>"}.");

        _host = context.Host;
        _cycleGeneration = context.CycleGeneration;
        _cycleIdentity = identity;
        _quiescing = false;

        try
        {
            _cycleModel = definedModel;
            var powerCapability = new ClawPowerCapability(_services.Wmi, definedModel);
            var chargeLimitCapability = new ClawChargeLimitCapability(_services.Wmi);
            var fanCapability = new ClawFanCapability(_services.Wmi);
            var lightingCapability = new ClawLightingCapability(
                _services.Mcu,
                ClawModels.LightingProfileAddress(identity.Snapshot.McuFirmwareVersion));
            _powerCapability = powerCapability;
            _chargeLimitCapability = chargeLimitCapability;
            _fanCapability = fanCapability;
            _lightingCapability = lightingCapability;
            _journal = await ClawRecoveryJournal.OpenAsync(context.StateDirectory, cancellationToken)
                .ConfigureAwait(false);
            _oem = new OemEventService(_services.OemEvents, context.Host, _services.OemButtons);
            _power = new PowerService(powerCapability, _journal);
            _chargeLimit = new ChargeLimitService(chargeLimitCapability);
            _fans = new FanService(fanCapability, _journal);
            _telemetry = new TelemetryService(fanCapability);
            _lighting = new LightingService(_services.Mcu, lightingCapability);
            _motion = new MotionService(_services.Motion, definedModel);

            _controller = new ControllerService(
                _services.Mcu,
                _services.Controller,
                _motion,
                context.Host,
                _journal,
                definedModel)
            {
                Enabled = context.ControllerManagementEnabled
            };
            _suppressor = new ChordSuppressorService(
                _services.ChordSuppressor,
                _oem,
                context.Host);

            // Start and resume acquire in this order, and stop releases in reverse. Chord suppression
            // follows the OEM event source it requires, and the controller follows the motion service
            // it was built with. ClawPluginTests pins both orders.
            _cycleServices =
            [
                _oem,
                _power,
                _chargeLimit,
                _fans,
                _telemetry,
                _lighting,
                _motion,
                _controller,
                _suppressor
            ];
            // Suspend stops only the services that own a live source.
            _suspendableServices = [_oem, _motion, _controller, _suppressor];
            BuildCapabilitySurface();

            if (_journal.FailureReason is { } journalFailure)
            {
                BlockService(ServiceIds.Power, journalFailure);
                BlockService(ServiceIds.Fans, journalFailure);
                BlockService(ServiceIds.Controller, journalFailure);
            }
            else
            {
                await ReconcileOutstandingAsync(
                    _journal.OutstandingEntries,
                    identity,
                    powerCapability,
                    fanCapability,
                    cancellationToken).ConfigureAwait(false);
            }

            await context.Host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken)
                .ConfigureAwait(false);
            await context.Host.PublishOemControlsAsync(CreateOemControls(), cancellationToken)
                .ConfigureAwait(false);

            await StartServicesAsync(
                new ClawCycleContext(
                    context.CycleGeneration,
                    Deadline.After(TimeSpan.FromSeconds(15)),
                    identity),
                cancellationToken).ConfigureAwait(false);
            _active = true;
            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
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
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                "The Claw device cycle is inactive or quiescing.");
        }

        try
        {
            await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                "The command was cancelled before its serialized hardware turn began.");
        }

        try
        {
            if (!_active || _descriptorSet is null || _quiescing)
            {
                return ClawResults.Rejected(
                    command,
                    CapabilityReasonCode.Quiescing,
                    "The Claw device cycle started quiescing before this command could run.");
            }

            var result = await ExecuteBoundCommandAsync(command, cancellationToken)
                .ConfigureAwait(false);

            if (_host is null || result.Outcome is not CommandOutcome.AppliedVerified)
            {
                return result;
            }

            var published = await PublishPostCommandObservationAsync(command, cancellationToken).ConfigureAwait(false);
            if (!published && command.CapabilityId == CapabilityIds.Scenario)
            {
                return result with
                {
                    Outcome = CommandOutcome.Indeterminate,
                    ReadbackValue = null,
                    Rollback = RollbackResult.NotRequired,
                    Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable,
                        "Scenario was written, but its resulting power limits could not be published.")
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
    public async ValueTask SuspendAsync(
        PluginQuiesceContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_cycleServices.Count == 0)
        {
            return;
        }

        _quiescing = true;
        StopObservationLoop();
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SuspendServicesAsync(
                OperationContext(context.Deadline),
                cancellationToken).ConfigureAwait(false);
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
        if (_cycleServices.Count == 0)
        {
            return new PluginStartResult
            {
                State = PluginOperationalState.Passive,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.ResourceReleased,
                    "The Claw services have not been started.")
            };
        }

        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cycleGeneration = context.CycleGeneration;
            _cycleIdentity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (_journal is not null
                && await _journal.CheckHealthAsync(cancellationToken).ConfigureAwait(false)
                    is { } journalFailure)
            {
                BlockService(ServiceIds.Power, journalFailure);
                BlockService(ServiceIds.Fans, journalFailure);
                BlockService(ServiceIds.Controller, journalFailure);
            }

            await ResumeServicesAsync(
                OperationContext(context.Deadline),
                cancellationToken).ConfigureAwait(false);
            if (_host is null || _powerCapability is null || _chargeLimitCapability is null
                || _fanCapability is null
                || _lightingCapability is null)
            {
                throw new InvalidOperationException("Resume cannot rebuild the capability surface.");
            }

            BuildCapabilitySurface();
            await _host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken)
                .ConfigureAwait(false);
            _quiescing = false;
            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);

            // Resume rebuilds the capability surface, so the observation loop has to come back with
            // it — a resumed cycle that never refreshed would go stale exactly as the original did.
            StartObservationLoop();
            return CurrentStartResult();
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
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
            ["cycle"] = DiagnosticCycleState(),
            ["recovery"] = DiagnosticRecoveryState()
        };
        foreach (var service in _cycleServices)
        {
            values[service.ServiceId] = BoundDiagnosticValue(service.State.ToString());
        }

        return ValueTask.FromResult(new PluginDiagnostics { Values = values });
    }

    /// <inheritdoc />
    public async ValueTask ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReleaseControllerCoreAsync(context, cancellationToken).ConfigureAwait(false);
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

            // The device cycle continues: only the controller is taken or let go.
            _controller.Enabled = context.Enabled;
            if (context.Enabled)
            {
                _cycleIdentity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
                var result = await _controller.AcquireAsync(
                    OperationContext(context.Deadline),
                    cancellationToken).ConfigureAwait(false);
                await ApplyServiceLifecycleStateAsync(_controller, result, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ReleaseControllerCoreAsync(
                    new PluginControllerReleaseContext(HandoffScope.ControllerOnly, context.Deadline),
                    cancellationToken).ConfigureAwait(false);
            }

            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _quiescing = true;

        // Before taking the gate: the loop takes the same one, and a refresh must not be able to
        // read hardware the stop is releasing.
        StopObservationLoop();
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cycleServices.Count > 0)
            {
                await StopServicesAsync(OperationContext(context.Deadline), cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = CurrentStopResult();

            _active = false;
            _descriptorSet = null;
            _cycleServices = [];
            _suspendableServices = [];
            if (_journal is null)
            {
                return result;
            }

            await _journal.DisposeAsync().ConfigureAwait(false);
            _journal = null;

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
            await StopAsync(
                new PluginStopContext(
                    PluginStopReason.WsgmExiting,
                    Deadline.After(TimeSpan.FromSeconds(12))),
                CancellationToken.None).ConfigureAwait(false);
        }

        await _services.ChordSuppressor.DisposeAsync().ConfigureAwait(false);
        await _services.Motion.DisposeAsync().ConfigureAwait(false);
        await _services.Controller.DisposeAsync().ConfigureAwait(false);
        await _services.Mcu.DisposeAsync().ConfigureAwait(false);
        await _services.OemEvents.DisposeAsync().ConfigureAwait(false);
        await _services.Wmi.DisposeAsync().ConfigureAwait(false);
        if (_journal is not null)
        {
            await _journal.DisposeAsync().ConfigureAwait(false);
            _journal = null;
        }

        _commandSerializer.Dispose();
    }

    private async ValueTask ReleaseControllerCoreAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        if (_controller is null)
        {
            return;
        }

        await _controller.ReleaseControllerAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        await ApplyServiceLifecycleStateAsync(_controller, new ClawServiceResult(ClawServiceState.Idle),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RollBackFailedStartAsync()
    {
        StopObservationLoop();
        if (_cycleServices.Count > 0)
        {
            try
            {
                await StopServicesAsync(
                    new ClawCycleContext(
                        _cycleGeneration,
                        Deadline.After(TimeSpan.FromSeconds(12)),
                        _cycleIdentity ?? throw new InvalidOperationException("No cycle identity is available.")),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PluginTrace.Error(
                    "lifecycle",
                    $"startup rollback could not release every service: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (_journal is not null)
        {
            try
            {
                await _journal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PluginTrace.Error(
                    "recovery",
                    $"startup rollback could not close the recovery journal: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _journal = null;
            }
        }

        await RetractFailedStartPublicationsAsync().ConfigureAwait(false);

        _active = false;
        _descriptorSet = null;
        _cycleServices = [];
        _suspendableServices = [];
    }

    private async ValueTask RetractFailedStartPublicationsAsync()
    {
        if (_host is null)
        {
            return;
        }

        await TryRetractPublicationAsync(
                "physical devices",
                () => _host.PublishPhysicalDevicesAsync([], null, CancellationToken.None))
            .ConfigureAwait(false);
        await TryRetractPublicationAsync(
                "OEM controls",
                () => _host.PublishOemControlsAsync([], CancellationToken.None))
            .ConfigureAwait(false);

        if (_descriptorSet is not null)
        {
            var publishedDescriptors = _descriptorSet;
            await TryRetractPublicationAsync(
                    "capability descriptors",
                    () => _host.PublishDescriptorsAsync(
                        new CapabilityDescriptorSet
                        {
                            Generation = checked(publishedDescriptors.Generation + 1),
                            CycleGeneration = _cycleGeneration,
                            Descriptors = []
                        },
                        CancellationToken.None))
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask TryRetractPublicationAsync(
        string publication,
        Func<ValueTask> retract)
    {
        try
        {
            await retract().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Error(
                "lifecycle",
                $"startup rollback could not retract {publication}: "
                + $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string DiagnosticCycleState()
    {
        if (_disposed)
        {
            return "disposed";
        }

        if (_quiescing)
        {
            return _active ? "quiescing" : "stopped";
        }

        if (_active)
        {
            return "started";
        }

        return _host is not null && _descriptorSet is not null
            ? "starting"
            : "stopped";
    }

    private string DiagnosticRecoveryState()
    {
        if (_journal is null)
        {
            return "unavailable";
        }

        if (_journal.FailureReason is not null)
        {
            return "blocked";
        }

        return _journal.OutstandingEntries.Count == 0 ? "healthy" : "pending";
    }

    private static string BoundDiagnosticValue(string value)
    {
        return value.Length <= MaxDiagnosticValueLength ? value : value[..MaxDiagnosticValueLength];
    }

    private async ValueTask StartServicesAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await ResumeServicesAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopServicesAsync(context, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask SuspendServicesAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        foreach (var service in _suspendableServices)
        {
            await OperateOneAsync(
                service,
                () => service.SuspendAsync(context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Acquires every service in start order; resume and start share this walk.</summary>
    private async ValueTask ResumeServicesAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        foreach (var service in _cycleServices)
        {
            await StartOneAsync(
                service,
                () => service.AcquireAsync(context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask StopServicesAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        for (var index = _cycleServices.Count - 1; index >= 0; index--)
        {
            var service = _cycleServices[index];
            await StopOneAsync(
                service,
                () => service.ReleaseAsync(context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask StartOneAsync(
        ClawServiceStatus service,
        Func<ValueTask<ClawServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await InvokeServiceAsync(service, operation, cancellationToken)
            .ConfigureAwait(false);
        service.ApplyResult(NormalizeAcquisitionResult(result));
    }

    private static async ValueTask OperateOneAsync(
        ClawServiceStatus service,
        Func<ValueTask<ClawServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await InvokeServiceAsync(service, operation, cancellationToken)
            .ConfigureAwait(false);
        service.ApplyResult(result);
    }

    private static async ValueTask StopOneAsync(
        ClawServiceStatus service,
        Func<ValueTask<ClawServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        ClawServiceResult result;
        try
        {
            result = await InvokeServiceAsync(service, operation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new ClawServiceResult(
                ClawServiceState.ReleasedUnverified,
                new CapabilityReason(
                    CapabilityReasonCode.Quiescing,
                    $"Release of service '{service.ServiceId}' exceeded its deadline."));
        }

        service.ApplyResult(NormalizeReleaseResult(result));
    }

    private static async ValueTask<ClawServiceResult> InvokeServiceAsync(
        ClawServiceStatus service,
        Func<ValueTask<ClawServiceResult>> operation,
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
            // The message is what says which check failed. Without it the charge-limit fault after
            // BIOS E1T52IMS.114 read only "InvalidOperationException" in the log, and the raw value
            // that caused it had to be guessed.
            return new ClawServiceResult(
                ClawServiceState.Faulted,
                new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Service '{service.ServiceId}' operation failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private static ValueTask ApplyServiceLifecycleStateAsync(
        ClawServiceStatus service,
        ClawServiceResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (result.State is not ClawServiceState.Acquiring and not ClawServiceState.Releasing)
        {
            service.ApplyResult(result);
        }

        return ValueTask.CompletedTask;
    }

    private static ClawServiceResult NormalizeAcquisitionResult(
        ClawServiceResult result)
    {
        return result.State switch
        {
            ClawServiceState.Owned or ClawServiceState.Passive or ClawServiceState.Degraded
                or ClawServiceState.Faulted => result,
            _ => new ClawServiceResult(
                ClawServiceState.Faulted,
                new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Acquisition returned invalid state {result.State}."))
        };
    }

    private static ClawServiceResult NormalizeReleaseResult(
        ClawServiceResult result)
    {
        return result.State switch
        {
            ClawServiceState.Idle or ClawServiceState.ReleasedUnverified or ClawServiceState.Faulted => result,
            _ => new ClawServiceResult(
                ClawServiceState.ReleasedUnverified,
                new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Release returned invalid state {result.State}."))
        };
    }

    private void BuildCapabilitySurface()
    {
        if (_power is null || _chargeLimit is null || _fans is null || _telemetry is null
            || _lighting is null
            || _motion is null || _controller is null)
        {
            throw new InvalidOperationException("Services must exist before descriptors are built.");
        }

        IReadOnlyList<CapabilityDescriptor> descriptors =
        [
            IntegerDescriptor(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                    // PL1 shares PL2's ceiling: on the A2VM that is 37 W, not the 30 W it ships at.
                    DisplayKey.SustainedPowerLimit, Model.MinimumWatts, Model.MaximumWatts,
                    CapabilityUnit.Watt, true,
                    section: SectionIds.Power, category: CategoryIds.Limits, order: 0) with
                {
                    PowerPresets = Model.PowerPresets,
                    PairedPowerLimitId = CapabilityIds.PowerBoost,
                    Prominence = CapabilityProminence.Primary,
                    LayoutPair = new CapabilityLayoutPair(CapabilityIds.PowerBoost)
                },
            IntegerDescriptor(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                DisplayKey.BoostPowerLimit, Model.MinimumWatts, Model.MaximumWatts, CapabilityUnit.Watt, true,
                section: SectionIds.Power, category: CategoryIds.Limits, order: 1),
            IntegerDescriptor(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit,
                    DisplayKey.ChargeLimit,
                    ClawChargeLimitCapability.MinimumPercent,
                    ClawChargeLimitCapability.MaximumPercent,
                    CapabilityUnit.Percent,
                    true,
                    persistence: CapabilityPersistence.DevicePersistent,
                    section: SectionIds.Power,
                    category: CategoryIds.Charging) with
                {
                    // HC's BatteryBypassStep: the limit is 60, 80 or 100 percent.
                    Step = ClawChargeLimitCapability.StepPercent
                },
            ChoiceDescriptor(
                CapabilityIds.Scenario,
                CapabilityRole.ScenarioMode,
                DisplayKey.PerformanceProfile,
                ["comfort", "green", "eco", "user", "sport", "inactive"],
                true,
                SectionIds.Power),
            ChoiceDescriptor(
                CapabilityIds.FanMode,
                CapabilityRole.FanMode,
                DisplayKey.FanMode,
                ["automatic", "custom", "full-speed"],
                true,
                SectionIds.Power,
                CategoryIds.Control),
            FanCurveDescriptor(1),
            IntegerDescriptor(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                DisplayKey.Brightness, 0, 100, CapabilityUnit.Percent, true,
                persistence: CapabilityPersistence.DevicePersistent,
                section: SectionIds.Lighting),
            LightingColorDescriptor(CapabilityInstances.LeftRing, "Left ring", 0),
            LightingColorDescriptor(CapabilityInstances.RightRing, "Right ring", 1),
            LightingColorDescriptor(CapabilityInstances.Buttons, "Buttons", 2),
            // These three carry the value kinds their roles require. ControllerSource and
            // MotionSource are choices because "who owns this source" has more than two answers —
            // the plugin can hold it, the device can still have it, or acquisition can have failed —
            // and a boolean flattened all three into "not owned". HapticSink carries no value at
            // all: it is a target rumble is written to, not something with a readable state.
            //
            // Declaring them as booleans made the descriptor set fail the SDK's role/value-kind
            // check, and a rejected SET means every capability is rejected — so the whole device
            // published nothing at all because of these three lines.
            ChoiceDescriptor(
                CapabilityIds.Controller,
                CapabilityRole.ControllerSource,
                DisplayKey.Controller,
                SourceOwnershipChoices,
                false,
                SectionIds.Info,
                CategoryIds.Ownership),
            ChoiceDescriptor(
                CapabilityIds.Motion,
                CapabilityRole.MotionSource,
                DisplayKey.Motion,
                SourceOwnershipChoices,
                false,
                SectionIds.Info,
                CategoryIds.Ownership,
                1),
            ActionDescriptor(
                CapabilityIds.Rumble,
                CapabilityRole.HapticSink,
                DisplayKey.Rumble,
                SectionIds.Info,
                CategoryIds.Ownership,
                2),
            // Readings, not controls. They left the Power page because a person opens it to change
            // how the device behaves, not to watch numbers; the CPU temperature stays published
            // because the fan-curve editor draws the live temperature against the curve.
            IntegerDescriptor(CapabilityIds.Temperature, CapabilityRole.Telemetry,
                DisplayKey.CpuTemperature, 0, 110, CapabilityUnit.Celsius, false,
                section: SectionIds.Info, category: CategoryIds.Readings, order: 0),
            // Both fans are still measured separately even though they are driven together: one
            // failing fan is exactly the fault this page exists to make visible.
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanLeft, 0, 10_000, CapabilityUnit.Rpm, false,
                CapabilityInstances.Left,
                section: SectionIds.Info, category: CategoryIds.Readings, order: 1),
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanRight, 0, 10_000, CapabilityUnit.Rpm, false,
                CapabilityInstances.Right,
                section: SectionIds.Info, category: CategoryIds.Readings, order: 2)
        ];

        EnsureUniqueCapabilityKeys(descriptors);
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
            string.Equals(candidate.CapabilityId, command.CapabilityId, StringComparison.Ordinal)
            && string.Equals(candidate.InstanceId, command.InstanceId, StringComparison.Ordinal));
        var service = ServiceForCapability(command.CapabilityId);
        if (descriptor is null || service is null)
        {
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                $"Capability '{CapabilityKey(command.CapabilityId, command.InstanceId)}' is not available.");
        }

        if (command.ApplyPowerPair && descriptor.PairedPowerLimitId is null)
        {
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                "This capability does not declare a power pair.");
        }

        ClawIdentityState identity;
        try
        {
            identity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (identity.ExactMachineMatch && identity.Model != _cycleModel)
            {
                // Refused before RefreshObservedAsync, so a changed model gets no hardware read either.
                return ClawResults.Rejected(command, new CapabilityReason(
                    CapabilityReasonCode.GenerationChanged,
                    "The Claw model no longer matches the one this cycle started on.",
                    true));
            }

            if (service.State is ClawServiceState.Owned
                && identity.ExactMachineMatch
                && FirmwareVerified(identity, FirmwareForCapability(command.CapabilityId)))
            {
                await RefreshObservedAsync(command.CapabilityId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                "Command was cancelled before hardware application began.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"Current-state revalidation failed: {ex.GetType().Name}.");
        }

        var refusal = RefusalFor(
            service,
            FirmwareForCapability(command.CapabilityId),
            identity);
        refusal ??= ValidateCommand(command, descriptor, identity.OnAcPower);
        if (refusal is not null)
        {
            return ClawResults.Rejected(command, refusal);
        }

        CapabilityCommandResult result;
        try
        {
            result = await ApplyCapabilityCommandAsync(command, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Indeterminate(
                command,
                "Command was cancelled after hardware application began.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Indeterminate(
                command,
                $"Capability handler failed after admission: {ex.GetType().Name}.");
        }

        return NormalizeCommandResult(command, result);
    }

    private ValueTask<CapabilityCommandResult> ApplyCapabilityCommandAsync(
        CapabilityCommand command,
        ClawIdentityState identity,
        CancellationToken cancellationToken)
    {
        // Admission refuses a WMI capability without the provider, and the provider's availability is
        // the binding itself, so only a WMI-free capability, which never journals, finds none.
        string WmiFirmware()
        {
            return identity.WmiFirmwareIdentity
                   ?? throw new InvalidOperationException("A WMI capability was admitted without a firmware binding.");
        }

        var power = _powerCapability
                    ?? throw new InvalidOperationException("The power capability is unavailable.");
        var chargeLimit = _chargeLimitCapability
                          ?? throw new InvalidOperationException("The charge-limit capability is unavailable.");
        var fans = _fanCapability
                   ?? throw new InvalidOperationException("The fan capability is unavailable.");
        var lighting = _lightingCapability
                       ?? throw new InvalidOperationException("The lighting capability is unavailable.");

        return command.CapabilityId switch
        {
            CapabilityIds.PowerSustained => JournalCommandAsync(
                ServiceIds.Power,
                WmiFirmware(),
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => power.ApplySustainedAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.IntegerValue!.Value,
                    token),
                cancellationToken),
            CapabilityIds.PowerBoost => JournalCommandAsync(
                ServiceIds.Power,
                WmiFirmware(),
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => power.ApplyBoostAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.IntegerValue!.Value,
                    token),
                cancellationToken),
            CapabilityIds.Scenario => JournalCommandAsync(
                ServiceIds.Power,
                WmiFirmware(),
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => power.ApplyScenarioAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.ChoiceValue!,
                    token),
                cancellationToken),
            CapabilityIds.ChargeLimit => chargeLimit.ApplyAsync(
                command,
                command.RequestedValue!.IntegerValue!.Value,
                cancellationToken),
            CapabilityIds.FanMode => JournalCommandAsync(
                ServiceIds.Fans,
                WmiFirmware(),
                command,
                async token => ClawRecoveryValues.Fans(
                    await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => fans.ApplyModeAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.ChoiceValue!,
                    token),
                cancellationToken),
            CapabilityIds.FanCurve => ApplyFanCurveCommandAsync(command, fans, WmiFirmware(), cancellationToken),
            CapabilityIds.LightingBrightness => lighting.ApplyAsync(
                command,
                state => state with
                {
                    Brightness = command.RequestedValue!.IntegerValue!.Value
                },
                cancellationToken),
            CapabilityIds.LightingColor => lighting.ApplyAsync(
                command,
                state => command.InstanceId switch
                {
                    CapabilityInstances.RightRing => state with
                    {
                        RightRingColor = command.RequestedValue!.ColorValue!.Value
                    },
                    CapabilityInstances.LeftRing => state with
                    {
                        LeftRingColor = command.RequestedValue!.ColorValue!.Value
                    },
                    CapabilityInstances.Buttons => state with
                    {
                        ButtonsColor = command.RequestedValue!.ColorValue!.Value
                    },
                    _ => state
                },
                cancellationToken),
            _ => ReadOnlyHandler(command, cancellationToken)
        };
    }

    private ValueTask<CapabilityCommandResult> ApplyFanCurveCommandAsync(
        CapabilityCommand command,
        ClawFanCapability fans,
        string wmiFirmware,
        CancellationToken cancellationToken)
    {
        return JournalCommandAsync(
            ServiceIds.Fans,
            wmiFirmware,
            command,
            async token => ClawRecoveryValues.Fans(
                await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
            (journalCommand, token) => fans.ApplyCurveAsync(
                journalCommand,
                journalCommand.RequestedValue!.CurveValue,
                token),
            cancellationToken);
    }

    private ClawServiceStatus? ServiceForCapability(string capabilityId)
    {
        return capabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power,
            CapabilityIds.ChargeLimit => _chargeLimit,
            CapabilityIds.FanMode or CapabilityIds.FanCurve => _fans,
            CapabilityIds.FanRpm or CapabilityIds.Temperature => _telemetry,
            CapabilityIds.LightingBrightness or CapabilityIds.LightingColor => _lighting,
            CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
            CapabilityIds.Motion => _motion,
            _ => null
        };
    }

    private static FirmwareKind FirmwareForCapability(string capabilityId)
    {
        return capabilityId switch
        {
            // MCU-backed capabilities carry no revision gate. The controller mode switch is not an
            // addressed write, and lighting takes its address from HC's firmware table and, like HC,
            // writes without first confirming what the MCU holds.
            CapabilityIds.LightingBrightness or CapabilityIds.LightingColor
                or CapabilityIds.Controller or CapabilityIds.Rumble => FirmwareKind.None,
            // Read from the Windows sensor stack, not MSI firmware, so there is no firmware revision to
            // gate it on and gating it on the WMI one would refuse it whenever that path is degraded.
            CapabilityIds.Motion => FirmwareKind.None,
            _ => FirmwareKind.Wmi
        };
    }

    private static CapabilityReason? RefusalFor(
        ClawServiceStatus service,
        FirmwareKind firmwareKind,
        ClawIdentityState identity)
    {
        if (!identity.ExactMachineMatch)
        {
            return new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Exact device identity no longer matches the Claw implementation.",
                true);
        }

        if (!FirmwareVerified(identity, firmwareKind))
        {
            return new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "Current firmware is outside the Claw implementation's verified gate.");
        }

        return service.State switch
        {
            ClawServiceState.Owned => null,
            ClawServiceState.Passive => new CapabilityReason(
                CapabilityReasonCode.ResourceConflict,
                "The device service is passive or held by another owner.", true),
            ClawServiceState.Releasing => new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "The device service is being released."),
            ClawServiceState.Degraded => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The device service is degraded and cannot accept commands."),
            ClawServiceState.Faulted or ClawServiceState.ReleasedUnverified => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The device service is faulted or its release could not be verified."),
            _ => new CapabilityReason(
                CapabilityReasonCode.ResourceReleased,
                "The plugin does not currently own this device service.", true)
        };
    }

    private CapabilityReason? ValidateCommand(
        CapabilityCommand command,
        CapabilityDescriptor descriptor,
        bool onAcPower)
    {
        if (_descriptorSet is null
            || command.ExpectedDescriptorGeneration != _descriptorSet.Generation
            || command.ExpectedCycleGeneration != _cycleGeneration)
        {
            return new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Command targets a descriptor or device generation that is no longer current.",
                true);
        }

        if (command.Deadline.HasExpired)
        {
            return new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "Command deadline passed before it could be applied.",
                true);
        }

        if (onAcPower ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc)
        {
            return new CapabilityReason(
                CapabilityReasonCode.UnavailableOnPowerSource,
                onAcPower
                    ? "Capability is unavailable on AC power."
                    : "Capability is unavailable on battery power.");
        }

        if (command.RequestedValue is null)
        {
            return descriptor.SupportsAction
                ? null
                : new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    "Capability does not support being invoked as an action.");
        }

        if (!descriptor.SupportsWrite)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported, "Capability is read-only.");
        }

        var value = command.RequestedValue;
        if (value.Kind != descriptor.ValueKind)
        {
            return new CapabilityReason(
                CapabilityReasonCode.Unsupported,
                $"Value kind {value.Kind} does not match descriptor kind {descriptor.ValueKind}.");
        }

        return ValidateCommandValue(value, descriptor);
    }

    private static CapabilityReason? ValidateCommandValue(
        CapabilityValue value,
        CapabilityDescriptor descriptor)
    {
        switch (descriptor.ValueKind)
        {
            case CapabilityValueKind.Integer:
                if (value.IntegerValue is not { } integer)
                {
                    return ValueOutOfRange("No integer value was supplied.");
                }

                if (descriptor.Minimum is { } minimum && integer < minimum)
                {
                    return ValueOutOfRange($"{integer} is below the minimum of {minimum}.");
                }

                if (descriptor.Maximum is { } maximum && integer > maximum)
                {
                    return ValueOutOfRange($"{integer} is above the maximum of {maximum}.");
                }

                if (descriptor.Step is not ({ } step and > 0))
                {
                    return null;
                }

                var origin = descriptor.Minimum ?? 0;
                if ((integer - origin) % step != 0)
                {
                    return ValueOutOfRange(
                        $"{integer} is not on the {step} step boundary from {origin}.");
                }

                return null;

            case CapabilityValueKind.Choice:
                if (value.ChoiceValue is not { Length: > 0 } choice)
                {
                    return ValueOutOfRange("No choice was supplied.");
                }

                return descriptor.Choices.Any(candidate => string.Equals(
                    candidate.Value,
                    choice,
                    StringComparison.Ordinal))
                    ? null
                    : ValueOutOfRange($"'{choice}' is not one of the declared options.");

            case CapabilityValueKind.Boolean:
                return value.BooleanValue is not null
                    ? null
                    : ValueOutOfRange("No boolean value was supplied.");

            case CapabilityValueKind.Color:
                if (value.ColorValue is not { } color)
                {
                    return ValueOutOfRange("No colour was supplied.");
                }

                return color is >= 0 and <= 0xFFFFFF
                    ? null
                    : ValueOutOfRange("Colour must be 24-bit RGB.");

            case CapabilityValueKind.Curve:
                if (value.CurveValue.Count == 0)
                {
                    return ValueOutOfRange("Curve has no points.");
                }

                for (var index = 1; index < value.CurveValue.Count; index++)
                {
                    if (value.CurveValue[index].Input <= value.CurveValue[index - 1].Input)
                    {
                        return ValueOutOfRange(
                            "Curve points must be strictly increasing in input.");
                    }
                }

                return null;

            case CapabilityValueKind.None:
            case CapabilityValueKind.Text:
            default:
                return new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    $"Value kind {descriptor.ValueKind} carries no value.");
        }
    }

    private static CapabilityReason ValueOutOfRange(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange, detail);
    }

    private static bool FirmwareVerified(ClawIdentityState identity, FirmwareKind kind)
    {
        return kind is not FirmwareKind.Wmi || identity.WmiAvailable;
    }

    private static CapabilityCommandResult NormalizeCommandResult(
        CapabilityCommand command,
        CapabilityCommandResult result)
    {
        if (result.CommandId != command.CommandId)
        {
            return Indeterminate(command, "Capability handler returned a result for another command.");
        }

        if (result.Outcome is CommandOutcome.AppliedVerified && result.ReadbackValue is null)
        {
            return result with
            {
                Outcome = CommandOutcome.AppliedUnverified,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Handler claimed verified application without readback evidence.")
            };
        }

        return result.Outcome is not CommandOutcome.AppliedVerified && result.ReadbackValue is not null
            ? result with { ReadbackValue = null }
            : result;
    }

    private static void EnsureUniqueCapabilityKeys(IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            var key = CapabilityKey(
                descriptor.CapabilityId,
                descriptor.InstanceId);
            if (!keys.Add(key))
            {
                throw new InvalidOperationException($"Capability '{key}' is registered more than once.");
            }
        }
    }

    private static string CapabilityKey(string capabilityId, string? instanceId)
    {
        return instanceId is null ? capabilityId : $"{capabilityId}/{instanceId}";
    }

    /// <summary>The one fan curve, applied to both channels.</summary>
    /// <remarks>
    ///     One capability rather than a left and a right instance. The A2VM's fans share a heatsink and
    ///     the firmware ramps them together, so two independently authored curves described a machine
    ///     that does not exist and made the user set the same thing twice.
    ///     <para>
    ///         The 0-100 bounds are declared, not implied: they are what the firmware accepts for a duty
    ///         byte, and WSGM's curve editor needs a stated range to draw an axis and clamp a drag against.
    ///         An undeclared bound means "no limit" to the router, which would let the editor offer values
    ///         the write would then refuse.
    ///     </para>
    /// </remarks>
    private static CapabilityDescriptor FanCurveDescriptor(int order)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.FanCurve,
            Role = CapabilityRole.FanCurve,
            SectionId = SectionIds.Power,
            CategoryId = CategoryIds.Control,
            SortOrder = order,
            ValueKind = CapabilityValueKind.Curve,
            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            Minimum = 0,
            Maximum = 100,
            Unit = CapabilityUnit.Percent,
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor LightingColorDescriptor(
        string instance,
        string label,
        int order)
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
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent
        };
    }

    private async ValueTask PublishCapabilityStatesAsync(CancellationToken cancellationToken)
    {
        if (_host is null || _descriptorSet is null)
        {
            return;
        }

        foreach (var descriptor in _descriptorSet.Descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var service = ServiceForCapability(descriptor.CapabilityId);
            if (service is null)
            {
                continue;
            }

            // A descriptor that cannot be read has no observed value to publish. The haptic sink is
            // the only one: rumble is written to it and never read back, so a state carrying a value
            // for it is rejected against its own descriptor shape. Its availability still matters,
            // so the state is published — with no value, which is what "not readable" means.
            var value = descriptor.SupportsRead ? CurrentState(descriptor) : null;
            await _host.PublishCapabilityStateAsync(
                new CapabilityState
                {
                    CapabilityId = descriptor.CapabilityId,
                    InstanceId = descriptor.InstanceId,
                    Available = service.State is ClawServiceState.Owned,
                    Reason = service.Reason ?? ReasonFor(service.State),
                    ObservedValue = value,
                    Quality = value is null
                        ? HardwareStateQuality.Unknown
                        : HardwareStateQuality.Observed,
                    ObservedAt = value is null ? null : DateTimeOffset.UtcNow,
                    DescriptorGeneration = _descriptorSet.Generation,
                    CycleGeneration = _cycleGeneration
                },
                cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private CapabilityValue? CurrentState(CapabilityDescriptor descriptor)
    {
        switch (descriptor.CapabilityId)
        {
            case CapabilityIds.PowerSustained:
            {
                return _power!.LastObserved is { } value ? CapabilityValue.Integer(value.SustainedWatts) : null;
            }
            case CapabilityIds.PowerBoost:
            {
                return _power!.LastObserved is { } value ? CapabilityValue.Integer(value.BoostWatts) : null;
            }
            case CapabilityIds.Scenario:
            {
                return _power!.LastObserved is { } value ? Scenario(value.Scenario, Model) : null;
            }
            case CapabilityIds.ChargeLimit:
            {
                // A percentage outside the declared 60-100 bounds (0 after a BIOS reset on the
                // reference unit) is published as unknown rather than as a value the slider cannot
                // show; the capability stays available so the configured limit is written over it.
                return _chargeLimit!.LastObserved is
                {
                    Percent: >= ClawChargeLimitCapability.MinimumPercent
                    and <= ClawChargeLimitCapability.MaximumPercent
                } value
                    ? CapabilityValue.Integer(value.Percent)
                    : null;
            }
            case CapabilityIds.FanMode:
            {
                return _fans!.LastObserved is { } value ? FanMode(value) : null;
            }
            case CapabilityIds.FanCurve:
            {
                // The left channel stands for both. Every write installs one curve on the pair, so the
                // two tables can only disagree if something outside WSGM wrote one of them, and the
                // next write puts them back together.
                var value = _fans!.LastObserved;
                return value is null ? null : CapabilityValue.Curve(ClawFanCapability.DecodeCurve(value.Left));
            }
            case CapabilityIds.FanRpm:
            {
                var value = _telemetry!.LastTelemetry;
                return value is null
                    ? null
                    : CapabilityValue.Integer(
                        descriptor.InstanceId == CapabilityInstances.Left ? value.LeftRpm : value.RightRpm);
            }
            case CapabilityIds.Temperature:
            {
                return _telemetry!.LastTelemetry is { } value
                    ? CapabilityValue.Integer(value.TemperatureCelsius)
                    : null;
            }
            case CapabilityIds.LightingBrightness:
            {
                return _lighting!.LastObserved is { } value ? CapabilityValue.Integer(value.Brightness) : null;
            }
            case CapabilityIds.LightingColor:
            {
                var value = _lighting!.LastObserved;
                return value is null
                    ? null
                    : CapabilityValue.Color(descriptor.InstanceId switch
                    {
                        CapabilityInstances.RightRing => value.RightRingColor,
                        CapabilityInstances.LeftRing => value.LeftRingColor,
                        CapabilityInstances.Buttons => value.ButtonsColor,
                        _ => 0
                    });
            }
            case CapabilityIds.Rumble:
                // A sink has no value to report. Its descriptor says so, and its state has to agree or
                // the state is rejected for a kind mismatch the way the descriptor set was.
                return CapabilityValue.None();
        }

        return CapabilityValue.Choice(OwnershipOf(
            descriptor.CapabilityId == CapabilityIds.Motion ? _motion!.State : _controller!.State));
    }

    /// <summary>Projects a service's state onto the ownership vocabulary the descriptor offers.</summary>
    /// <param name="state">The service's current state.</param>
    /// <returns>One of the descriptor's declared choices.</returns>
    /// <remarks>
    ///     Acquiring and Releasing report the ownership they are moving away from rather than inventing
    ///     a transient value: the row is read continuously, and a state that flickers through a fourth
    ///     value on every transition reads as a fault rather than as progress.
    /// </remarks>
    private static string OwnershipOf(ClawServiceState state)
    {
        return state switch
        {
            ClawServiceState.Owned or ClawServiceState.Releasing => "plugin",
            ClawServiceState.Idle or ClawServiceState.Passive or ClawServiceState.Acquiring => "device",
            _ => "unavailable"
        };
    }

    /// <summary>Starts the periodic observation refresh for this cycle.</summary>
    private void StartObservationLoop()
    {
        StopObservationLoop();
        CancellationTokenSource loop = new();
        _observationLoop = loop;
        _observationToken = loop.Token;
        _ = Task.Run(() => ObservationLoopAsync(loop.Token), loop.Token);
    }

    /// <summary>Stops and forgets the observation loop, if one is running.</summary>
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
            // Already torn down by a concurrent stop; nothing left to cancel.
        }

        loop.Dispose();
    }

    /// <summary>Re-reads the hardware and republishes state until the cycle ends.</summary>
    /// <param name="cancellationToken">Ends the loop when the cycle does.</param>
    /// <remarks>
    ///     Serialized behind the same gate as commands, so a refresh can never interleave with a
    ///     hardware write. Failures are traced and the loop continues: a device that cannot be read for
    ///     one interval is a stale reading, which WSGM already models, not a reason to stop observing.
    /// </remarks>
    private async Task ObservationLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(ObservationInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_active || _quiescing || _disposed)
            {
                continue;
            }

            if (!await _commandSerializer.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false))
            {
                // A command is in flight and will republish on its own; skipping is correct.
                continue;
            }

            try
            {
                await RefreshAllObservedAsync(cancellationToken).ConfigureAwait(false);
                await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A pass abandoned by an inner linked or timeout token, not by this loop. It is not
                // a failure — the next pass retries — but the filter above only recognises this
                // loop's own token, so every one of these reached the failure branch below: 3,492
                // warnings in one archived log, all of them a device quiescing normally. Keep
                // polling, and keep the evidence at a level that does not bury the log.
                PluginTrace.Debug("observe", "periodic observation refresh was cancelled; retrying.");
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

    /// <summary>Re-reads every observable service that is currently owned.</summary>
    private async ValueTask RefreshAllObservedAsync(CancellationToken cancellationToken)
    {
        if (_power is { State: ClawServiceState.Owned })
        {
            await _power.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_chargeLimit is { State: ClawServiceState.Owned })
        {
            await _chargeLimit.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_fans is { State: ClawServiceState.Owned })
        {
            await _fans.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_telemetry is { State: ClawServiceState.Owned })
        {
            await _telemetry.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_lighting is { State: ClawServiceState.Owned })
        {
            await _lighting.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RefreshObservedAsync(
        string capabilityId,
        CancellationToken cancellationToken)
    {
        switch (capabilityId)
        {
            case CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario:
                await _power!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                break;
            case CapabilityIds.ChargeLimit:
                await _chargeLimit!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                break;
            case CapabilityIds.FanMode or CapabilityIds.FanCurve:
                await _fans!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                await _telemetry!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                break;
            case CapabilityIds.LightingBrightness or CapabilityIds.LightingColor:
                await _lighting!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Publishes best-effort fresh state after a command already verified its own write.</summary>
    /// <remarks>
    ///     Capability handlers own command verification. This secondary refresh updates adjacent rows
    ///     such as paired power and fan telemetry; losing it must not rewrite a verified command result
    ///     or terminate the plugin cycle. Scenario selection requires the resulting pair before a host
    ///     can order its next watt writes, so its caller reports uncertainty when publication fails.
    /// </remarks>
    private async ValueTask<bool> PublishPostCommandObservationAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _observationToken);
        var remaining = command.Deadline.Remaining;
        bounded.CancelAfter(remaining <= TimeSpan.Zero ? TimeSpan.Zero
            : remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2));
        try
        {
            bounded.Token.ThrowIfCancellationRequested();
            await RefreshObservedAsync(command.CapabilityId, bounded.Token).ConfigureAwait(false);
            await PublishCapabilityStatesAsync(bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Failure(
                "observation",
                $"Post-command refresh for '{command.CapabilityId}' failed; scenario results require adjacent power readback",
                ex);
            return false;
        }
    }

    private async ValueTask<CapabilityCommandResult> JournalCommandAsync(
        string serviceId,
        string firmwareIdentity,
        CapabilityCommand command,
        Func<CancellationToken, ValueTask<ClawRecoveryState>> readOriginal,
        ClawCommandHandler apply,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            throw new InvalidOperationException("The recovery journal is unavailable.");
        }

        ClawWriteBudget.Require(command.Deadline, "journalled command preparation");
        ClawRecoveryState originalState;
        try
        {
            originalState = await readOriginal(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // HC writes without capturing anything. When the original cannot be read, the command is
            // written the same way and there is nothing to restore; a read never gates a write.
            PluginTrace.Failure(serviceId, "The original state could not be captured; writing without a restore", ex);
            return await apply(command, cancellationToken).ConfigureAwait(false);
        }

        var operation = await _journal.BeginAsync(
            serviceId,
            command.CapabilityId,
            firmwareIdentity,
            originalState,
            cancellationToken).ConfigureAwait(false);
        ClawWriteBudget.Require(command.Deadline, "journalled hardware application");
        // A transport exception does not prove whether the firmware accepted a write. Let it
        // propagate while the exact pre-command journal entry remains outstanding for recovery.
        var result = await apply(command, cancellationToken).ConfigureAwait(false);

        _ = await _journal.CompleteCommandAsync(operation, result, CancellationToken.None)
            .ConfigureAwait(false);
        if (result.Rollback is not RollbackResult.RestoreFailed)
        {
            return result;
        }

        ClawServiceStatus? service = serviceId switch
        {
            ServiceIds.Power => _power,
            ServiceIds.Fans => _fans,
            _ => null
        };
        if (service is null)
        {
            return result;
        }

        CapabilityReason reason = new(
            CapabilityReasonCode.TransportFaulted,
            "A command rollback failed; the resource is faulted until reconciliation.");
        service.Fault(reason);
        await ApplyServiceLifecycleStateAsync(
            service,
            new ClawServiceResult(ClawServiceState.Faulted, reason),
            CancellationToken.None).ConfigureAwait(false);

        return result;
    }

    private async ValueTask ReconcileOutstandingAsync(
        IReadOnlyList<ClawRecoveryEntry> entries,
        ClawIdentityState identity,
        ClawPowerCapability powerCapability,
        ClawFanCapability fanCapability,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (ServiceFor(entry.ServiceId)?.ReconciliationBlockReason is not null)
            {
                continue;
            }

            var currentFirmware = entry.ServiceId switch
            {
                ServiceIds.Power or ServiceIds.Fans when identity.WmiAvailable =>
                    identity.WmiFirmwareIdentity,
                ServiceIds.Controller when identity.ExactMachineMatch =>
                    ClawFirmwareIdentities.Mcu,
                _ => null
            };
            var action = ClawRecoveryJournal.Decide(entry, currentFirmware);
            if (action is ClawReconciliationAction.Discard)
            {
                PluginTrace.Warn(
                    "recovery",
                    $"dropped the {entry.ServiceId} entry bound to {entry.FirmwareIdentity}: the firmware now reads "
                    + $"{currentFirmware}, so its captured state is not restored.");
                _ = await _journal.CompleteExistingAsync(
                    entry,
                    ClawRecoveryStatus.RestoredVerified,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (action is not ClawReconciliationAction.Restore)
            {
                BlockService(entry.ServiceId, new CapabilityReason(
                    action is ClawReconciliationAction.Block
                        ? CapabilityReasonCode.TransportFaulted
                        : CapabilityReasonCode.FirmwareNotVerified,
                    "An outstanding recovery entry is not safe to restore automatically."));
                continue;
            }

            bool restored;
            var restoreFailed = false;
            try
            {
                restored = entry.ServiceId switch
                {
                    ServiceIds.Power when ClawRecoveryValues.TryPower(
                            entry.OriginalState,
                            out var power) =>
                        await powerCapability.RestoreAsync(power!, cancellationToken).ConfigureAwait(false),
                    ServiceIds.Fans when ClawRecoveryValues.TryFans(
                            entry.OriginalState,
                            out var fans) =>
                        await fanCapability.RestoreAsync(fans!, cancellationToken).ConfigureAwait(false),
                    ServiceIds.Controller =>
                        await RestoreControllerJournalEntryAsync(entry, cancellationToken)
                            .ConfigureAwait(false),
                    _ => false
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                restored = false;
                restoreFailed = true;
                _ = await _journal.CompleteExistingAsync(
                    entry,
                    ClawRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (restored)
            {
                _ = await _journal.CompleteExistingAsync(
                    entry,
                    ClawRecoveryStatus.RestoredVerified,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!restoreFailed)
            {
                _ = await _journal.CompleteExistingAsync(
                    entry,
                    ClawRecoveryStatus.RestoredUnverified,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!restored)
            {
                BlockService(entry.ServiceId, new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "An outstanding hardware state could not be restored and verified."));
            }
        }
    }

    private async ValueTask<bool> RestoreControllerJournalEntryAsync(
        ClawRecoveryEntry entry,
        CancellationToken cancellationToken)
    {
        if (!ClawRecoveryValues.TryControllerMode(entry.OriginalState, out var mode))
        {
            return false;
        }

        var current = await _services.Controller.DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null || string.IsNullOrWhiteSpace(current.PhysicalLocation))
        {
            return false;
        }

        if (current.Mode == mode)
        {
            return true;
        }

        var deadline = Deadline.After(TimeSpan.FromSeconds(6));
        var restored = await _services.Mcu.SwitchModeAsync(
            mode,
            current.PhysicalLocation,
            deadline,
            cancellationToken).ConfigureAwait(false);
        return restored.Mode == mode
               && HidDevices.SamePhysicalLocation(
                   restored.PhysicalLocation,
                   current.PhysicalLocation);
    }

    private void BlockService(string serviceId, CapabilityReason reason)
    {
        var service = ServiceFor(serviceId);
        service?.ReconciliationBlockReason = reason;
    }

    private ClawServiceStatus? ServiceFor(string serviceId)
    {
        return serviceId switch
        {
            ServiceIds.Power => _power,
            ServiceIds.Fans => _fans,
            ServiceIds.Controller => _controller,
            _ => null
        };
    }

    private ClawCycleContext OperationContext(Deadline deadline)
    {
        return new ClawCycleContext(
            _cycleGeneration,
            deadline,
            _cycleIdentity ?? throw new InvalidOperationException("No cycle identity is available."));
    }

    private PluginStartResult CurrentStartResult()
    {
        // A controller WSGM asked to keep off is not a service that failed.
        var requiredServices = _cycleServices
            .Where(service => service != _controller || _controller.Enabled)
            .ToArray();
        var owned = requiredServices.Count(service => service.State is ClawServiceState.Owned);
        var unhealthy = requiredServices.Any(service => service.State is not ClawServiceState.Owned);
        var firstUnhealthy = requiredServices.FirstOrDefault(service => service.State is not ClawServiceState.Owned);
        PluginStartResult result = new()
        {
            State = owned == 0
                ? PluginOperationalState.Passive
                : unhealthy
                    ? PluginOperationalState.Degraded
                    : PluginOperationalState.Active,
            Reason = firstUnhealthy?.Reason ?? (owned == 0
                ? new CapabilityReason(
                    CapabilityReasonCode.PrerequisiteMissing,
                    "No Claw hardware service could be acquired.")
                : null)
        };

        // "Degraded" names the aggregate and carries only the FIRST unhealthy service's reason,
        // which is what made "why is the device only partially available?" unanswerable from a
        // pasted log: the state that reached the user described one service out of eight and never
        // said which of the others were fine. This lists all of them, every time.
        TraceServiceStates(result.State);
        return result;
    }

    /// <summary>Records the state of every service behind one aggregate operational state.</summary>
    private void TraceServiceStates(PluginOperationalState aggregate)
    {
        if (_host is null)
        {
            return;
        }

        StringBuilder detail = new();
        foreach (var service in _cycleServices)
        {
            if (detail.Length > 0)
            {
                detail.Append(", ");
            }

            detail.Append(service.ServiceId).Append('=').Append(service.State);
            if (service.State is ClawServiceState.Owned || service.Reason is not { } reason)
            {
                continue;
            }

            detail.Append('(').Append(reason.Code);
            if (!string.IsNullOrWhiteSpace(reason.Detail))
            {
                detail.Append(": ").Append(reason.Detail);
            }

            detail.Append(')');
        }

        _host.Trace(
            aggregate is PluginOperationalState.Active
                ? DeviceTraceLevel.Info
                : DeviceTraceLevel.Warn,
            "lifecycle",
            $"start state {aggregate}: {detail}");
    }

    private PluginStopResult CurrentStopResult()
    {
        ClawServiceStatus? failed = _cycleServices.FirstOrDefault(service => service.State is ClawServiceState.Faulted);
        ClawServiceStatus? unverified =
            _cycleServices.FirstOrDefault(service => service.State is ClawServiceState.ReleasedUnverified);
        if (failed is not null)
        {
            return new PluginStopResult
            {
                Status = PluginStopStatus.Failed,
                Reason = failed.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Service '{failed.ServiceId}' cleanup failed.")
            };
        }

        if (unverified is not null)
        {
            return new PluginStopResult
            {
                Status = PluginStopStatus.Unverified,
                Reason = unverified.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Service '{unverified.ServiceId}' cleanup was not verified.")
            };
        }

        return new PluginStopResult { Status = PluginStopStatus.Clean };
    }

    private static IReadOnlyList<OemControlDescriptor> CreateOemControls()
    {
        return
        [
            Oem("oem1", "Claw button", OemControlPlacement.Front, false,
                false),
            Oem("oem2", "Quick Settings", OemControlPlacement.Front, true,
                false),
            Oem("oem3", "M1", OemControlPlacement.Rear, false,
                true),
            Oem("oem4", "M2", OemControlPlacement.Rear, false,
                true)
        ];
    }

    private static OemControlDescriptor Oem(
        string id,
        string label,
        OemControlPlacement placement,
        bool supportsLongPress,
        bool requiresController)
    {
        return new OemControlDescriptor
        {
            ControlId = id,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
            Placement = placement,
            SupportsLongPress = supportsLongPress,
            RequiresControllerAcquisition = requiresController
        };
    }

    private static CapabilityDescriptor IntegerDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        int minimum,
        int maximum,
        CapabilityUnit unit,
        bool writable,
        string? instance = null,
        CapabilityPersistence persistence = CapabilityPersistence.Volatile,
        string? section = null,
        string? category = null,
        int order = 0)
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
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Unit = unit,
            Persistence = persistence
        };
    }

    private static CapabilityDescriptor ChoiceDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        IReadOnlyList<string> choices,
        bool writable,
        string? section = null,
        string? category = null,
        int order = 0)
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
            SupportsRead = true,
            SupportsWrite = writable,
            Choices =
            [
                .. choices.Select(choice => new CapabilityChoice(
                    choice,
                    new CapabilityDisplay
                    {
                        Key = DisplayKey.Custom, CustomLabel = choice switch
                        {
                            "comfort" => "Comfort", "green" => "Green", "eco" => "Eco", "sport" => "Sport",
                            "user" => "User", "inactive" => "Inactive", "automatic" => "Automatic",
                            "custom" => "Custom", "full-speed" => "Full speed",
                            _ => choice
                        }
                    }))
            ],
            Persistence = CapabilityPersistence.Volatile
        };
    }

    /// <summary>A capability that is invoked rather than read or written.</summary>
    /// <param name="id">Capability id.</param>
    /// <param name="role">Semantic role, which must be one the SDK maps to <c>None</c>.</param>
    /// <param name="display">Display key.</param>
    /// <param name="section">Overlay section id the row is grouped under, or null for the default.</param>
    /// <param name="category">Category within that section, or null for its lead group.</param>
    /// <param name="order">Sort order within the section; lower sorts first.</param>
    /// <returns>The descriptor.</returns>
    private static CapabilityDescriptor ActionDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        string? section = null,
        string? category = null,
        int order = 0)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,
            SectionId = section,
            CategoryId = category,
            SortOrder = order,
            ValueKind = CapabilityValueKind.None,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = false,
            SupportsWrite = false,
            // A descriptor has to offer at least one operation, and for a sink the operation is the
            // invoke: rumble is written to it, never read back from it.
            SupportsAction = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor BooleanDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        bool writable,
        string? section = null,
        int order = 0)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,
            SectionId = section,
            SortOrder = order,
            ValueKind = CapabilityValueKind.Boolean,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    internal static CapabilityValue Scenario(byte raw, ClawModel model)
    {
        var mode = raw & 0x3F;
        return CapabilityValue.Choice(
            (raw & 0xC0) != 0xC0
                ? "inactive"
                : mode == model.UserScenario
                    ? "user"
                    : mode switch
                    {
                        0 => "comfort",
                        1 => "green",
                        2 => "eco",
                        4 => "sport",
                        _ => "unknown"
                    });
    }

    private static CapabilityValue FanMode(FanSnapshot snapshot)
    {
        return CapabilityValue.Choice(
            (snapshot.CustomFlag & 0x80) != 0
                ? "custom"
                : (snapshot.FullSpeedFlag & 0x80) != 0
                    ? "full-speed"
                    : "automatic");
    }

    // Admission succeeded and the handler failed without confirming a rollback. Journalled resources
    // remain outstanding, so claiming any restoration here would be fabricated.
    private static CapabilityCommandResult Indeterminate(CapabilityCommand command, string detail)
    {
        return ClawResults.Indeterminate(
            command,
            CapabilityReasonCode.TransportFaulted,
            detail,
            RollbackResult.RestoreFailed);
    }

    private static ValueTask<CapabilityCommandResult> ReadOnlyHandler(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ClawResults.Rejected(
            command,
            CapabilityReasonCode.Unsupported,
            "This capability is read-only."));
    }

    private static CapabilityReason? ReasonFor(ClawServiceState state)
    {
        return state switch
        {
            ClawServiceState.Owned => null,
            ClawServiceState.Passive => new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing),
            ClawServiceState.Degraded or ClawServiceState.Faulted or ClawServiceState.ReleasedUnverified =>
                new CapabilityReason(CapabilityReasonCode.TransportFaulted),
            ClawServiceState.Releasing => new CapabilityReason(CapabilityReasonCode.Quiescing),
            _ => new CapabilityReason(CapabilityReasonCode.ResourceReleased)
        };
    }

    private static ClawHardwareServices CreateWindowsServices()
    {
        MsiWmiPlatform wmi = new();

        // One latch, shared by the two services that need it: the OEM event source latches a press
        // and the controller reader merges it into the next samples. The buttons are physical
        // controller buttons that the firmware happens to deliver out of band.
        OemButtonLatch oemButtons = new();
        return new ClawHardwareServices(
            new WindowsClawIdentityReader(wmi),
            wmi,
            new MsiOemEventSource(),
            new WindowsClawMcuTransport(),
            new WindowsClawControllerSource(oemButtons),
            new WindowsClawMotionSource(),
            new FirmwareChordSuppressor(),
            oemButtons);
    }

    private delegate ValueTask<CapabilityCommandResult> ClawCommandHandler(
        CapabilityCommand command,
        CancellationToken cancellationToken);

    private enum FirmwareKind
    {
        None,
        Wmi
    }
}
