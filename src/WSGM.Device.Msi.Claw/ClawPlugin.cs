using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Msi.Claw;

/// <summary>The device plugin for the MSI Claw family; <see cref="ClawModels" /> lists every supported board.</summary>
public sealed partial class ClawPlugin : IDevicePlugin
{
    private readonly Dictionary<string, CapabilityReason> _observationFailures = new(StringComparer.Ordinal);
    private readonly DeviceCommandSerializer _serializer;
    private readonly ClawHardwareServices _services;
    private bool _active;
    private DisplayService? _arcSync;
    private ChargeLimitService? _chargeLimit;
    private ClawChargeLimitCapability? _chargeLimitCapability;
    private ControllerService? _controller;
    private long _cycleGeneration;
    private ClawIdentityState? _cycleIdentity;
    private ClawModel? _cycleModel;
    private IReadOnlyList<DeviceService<ClawIdentityState>> _cycleServices = [];
    private CapabilityDescriptorSet? _descriptorSet;
    private bool _disposed;
    private ClawFanCapability? _fanCapability;
    private FanService? _fans;
    private IPluginHostAdapter? _host;
    private ClawRecoveryJournal? _journal;
    private LightingService? _lighting;
    private ClawLightingCapability? _lightingCapability;
    private MotionService? _motion;
    private OemEventService? _oem;
    private PowerService? _power;
    private ClawPowerCapability? _powerCapability;
    private bool _quiescing;
    private ChordSuppressorService? _suppressor;
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
        _serializer = new DeviceCommandSerializer(
            "Claw",
            () => _active && !_quiescing ? _descriptorSet : null,
            RefreshAsync,
            PublishCapabilityStatesAsync);
    }

    /// <summary>The model this cycle started on. Only a started cycle has one; nothing falls back to a default.</summary>
    private ClawModel Model => _cycleModel ?? throw new InvalidOperationException("No device cycle is active.");

    /// <summary>The cycle's services in start order; stop releases them in reverse.</summary>
    internal IReadOnlyList<DeviceService<ClawIdentityState>> Services => _cycleServices;

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
            var powerCapability = new ClawPowerCapability(_services.Wmi, definedModel, _services.Delay);
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

            // Opened before descriptors are built, because whether the variable-refresh row exists at
            // all depends on whether a capable panel answered.
            _arcSync = new DisplayService();
            _ = _arcSync.TryAcquire();
            _controller = new ControllerService(
                _services.Mcu,
                _services.Controller,
                _motion,
                context.Host,
                _journal,
                definedModel,
                _services.Delay)
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

            await AcquireServicesAsync(OperationContext(Deadline.After(TimeSpan.FromSeconds(15))), cancellationToken)
                .ConfigureAwait(false);
            _active = true;
            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
            _serializer.StartObservation();
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
    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _serializer.ExecuteAsync(command, token => ExecuteBoundCommandAsync(command, token), cancellationToken);
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
        _serializer.StopObservation();
        await _serializer.RunAsync(
            () => DeviceServiceLifecycle.SuspendAllAsync(
                _cycleServices,
                OperationContext(context.Deadline),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
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

        return await _serializer.RunAsync(async () =>
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

            await AcquireServicesAsync(OperationContext(context.Deadline), cancellationToken)
                .ConfigureAwait(false);
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
            // it: a resumed cycle that never refreshed would go stale exactly as the original did.
            _serializer.StartObservation();
            return CurrentStartResult();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
    {
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
            ["recovery"] = _journal?.DiagnosticState ?? "unavailable"
        };
        foreach (var service in _cycleServices)
        {
            values[service.ServiceId] = service.State.ToString();
        }

        return ValueTask.FromResult(new PluginDiagnostics { Values = values });
    }

    /// <inheritdoc />
    public ValueTask ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _serializer.RunAsync(() => ReleaseControllerCoreAsync(context.Deadline, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _serializer.RunAsync(async () =>
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
                await DeviceServiceLifecycle.AcquireAsync(_controller, OperationContext(context.Deadline),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ReleaseControllerCoreAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
            }

            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
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
        _serializer.StopObservation();
        return await _serializer.RunAsync(async () =>
        {
            if (_cycleServices.Count > 0)
            {
                await DeviceServiceLifecycle.ReleaseAllAsync(_cycleServices, OperationContext(context.Deadline),
                    cancellationToken).ConfigureAwait(false);
            }

            // Restored before the result is taken, and outside the service walk, because the
            // display is held by the graphics driver rather than by anything a service
            // releases. Leaving variable refresh off after WSGM exits would be a change the user
            // never made and has no obvious way to undo.
            var displayRestored = true;
            if (_arcSync is not null)
            {
                displayRestored = _arcSync.Restore();
                _arcSync.Dispose();
                _arcSync = null;
            }

            var result = DeviceServiceLifecycle.StopResult(_cycleServices);
            if (!displayRestored && result.Status is not PluginStopStatus.Failed)
            {
                result = new PluginStopResult
                {
                    Status = PluginStopStatus.Failed,
                    Reason = new CapabilityReason(
                        CapabilityReasonCode.TransportFaulted,
                        "The panel's captured variable-refresh profile could not be restored.")
                };
            }

            _active = false;
            _descriptorSet = null;
            _cycleServices = [];
            if (_journal is null)
            {
                return result;
            }

            await _journal.DisposeAsync().ConfigureAwait(false);
            _journal = null;

            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _serializer.StopObservation();
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

        _serializer.Dispose();
    }

    private async ValueTask ReleaseControllerCoreAsync(
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        if (_controller is not null)
        {
            await _controller.ReleaseControllerAsync(deadline, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RollBackFailedStartAsync()
    {
        _serializer.StopObservation();
        await DeviceServiceLifecycle.RollBackStartAsync(
            _cycleServices,
            OperationContext(Deadline.After(TimeSpan.FromSeconds(12))),
            _host,
            _descriptorSet).ConfigureAwait(false);

        if (_arcSync is not null)
        {
            try
            {
                if (!_arcSync.Restore())
                {
                    PluginTrace.Error(
                        "display",
                        "startup rollback could not verify the captured variable-refresh profile.");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PluginTrace.Failure("display", "startup rollback failed while restoring variable refresh", ex);
            }
            finally
            {
                _arcSync.Dispose();
                _arcSync = null;
            }
        }

        if (_journal is not null)
        {
            await _journal.DisposeAsync().ConfigureAwait(false);
            _journal = null;
        }

        _active = false;
        _descriptorSet = null;
        _cycleServices = [];
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

    /// <summary>Acquires every service in start order and clears the observation failure of each one now owned.</summary>
    private async ValueTask AcquireServicesAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        await DeviceServiceLifecycle.AcquireAllAsync(_cycleServices, context, cancellationToken).ConfigureAwait(false);
        foreach (var service in _cycleServices)
        {
            if (service.State is DeviceServiceState.Owned)
            {
                _observationFailures.Remove(service.ServiceId);
            }
        }
    }

    private DeviceCycleContext<ClawIdentityState> OperationContext(Deadline deadline)
    {
        return new DeviceCycleContext<ClawIdentityState>(
            _cycleGeneration,
            deadline,
            _cycleIdentity ?? throw new InvalidOperationException("No cycle identity is available."));
    }

    private PluginStartResult CurrentStartResult()
    {
        // A controller WSGM asked to keep off is not a service that failed.
        return DeviceServiceLifecycle.StartResult(
            _cycleServices,
            service => service != _controller || _controller.Enabled,
            "No Claw hardware service could be acquired.",
            _host);
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
            oemButtons,
            Task.Delay);
    }
}
