// SPDX-License-Identifier: MIT

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

namespace WSGM.Device.Asus.RogAlly;

/// <summary>The hardware transports one plugin instance uses; replaced by fakes in tests.</summary>
internal sealed record AllyHardwareServices(
    IAllyIdentityReader Identity,
    IAsusAcpi Acpi,
    Func<AllyModel, IAllyVendorHid> Vendor,
    Func<AllyModel, IAllyAuraHid> Aura,
    Func<AllyModel, AllyOemButtonState, IAllyControllerSource> Controller,
    Func<AllyModel, IAllyMotionSource> Motion,
    IAllyKeyboardSource Keyboard,
    Func<TimeSpan, CancellationToken, Task> Delay);

/// <summary>The device plugin for the ASUS ROG Ally, Ally X, Xbox Ally and Xbox Ally X.</summary>
/// <remarks>
///     Built from Handheld Companion 1.3.1.6 and HHD without hardware. Each model's facts live in
///     <c>AllyModels</c>; PROVENANCE.md cites the source of every one and lists what a Device Lab report
///     must confirm.
/// </remarks>
public sealed partial class RogAllyPlugin : IDevicePlugin
{
    private readonly AllyHardwareServices _hardware;
    private readonly DeviceCommandSerializer _serializer;

    /// <summary>Values this cycle wrote, keyed by capability and instance, for firmware without readback.</summary>
    private readonly Dictionary<(string CapabilityId, string? InstanceId), CapabilityValue> _written = [];

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
    private PowerService? _power;
    private bool _quiescing;
    private IReadOnlyList<DeviceService<AllyIdentityState>> _services = [];
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
        _serializer = new DeviceCommandSerializer(
            "Ally",
            () => _active && !_quiescing ? _descriptorSet : null,
            RefreshAsync,
            PublishStatesAsync);
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
        _written.Clear();
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
            _power = new PowerService(_hardware.Acpi, _hardware.Delay, _journal);
            _fans = new FanService(_hardware.Acpi, _hardware.Delay, _journal);
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
            await DeviceServiceLifecycle.AcquireAllAsync(_services, Context(Deadline.After(TimeSpan.FromSeconds(15))),
                cancellationToken).ConfigureAwait(false);
            _active = true;
            await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
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
    public async ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services.Count == 0)
        {
            return;
        }

        _quiescing = true;
        _serializer.StopObservation();
        await _serializer.RunAsync(
            () => DeviceServiceLifecycle.SuspendAllAsync(_services, Context(context.Deadline), cancellationToken),
            cancellationToken).ConfigureAwait(false);
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

        return await _serializer.RunAsync(async () =>
        {
            _cycleGeneration = context.CycleGeneration;
            _written.Clear();
            _cycleIdentity = await _hardware.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (_journal is not null && await _journal.CheckHealthAsync(cancellationToken).ConfigureAwait(false)
                    is { } journalFailure)
            {
                Block(journalFailure, _power, _fans, _controller);
            }

            await DeviceServiceLifecycle.AcquireAllAsync(
                _services.Where(service => service.Suspendable || service.State is not DeviceServiceState.Owned),
                Context(context.Deadline),
                cancellationToken).ConfigureAwait(false);
            BuildCapabilitySurface();
            await _host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken).ConfigureAwait(false);
            _quiescing = false;
            await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
            _serializer.StartObservation();
            return CurrentStartResult();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
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
            ["model"] = _model?.DefinitionId ?? "none",
            ["cycle"] = _disposed ? "disposed" : _active ? _quiescing ? "quiescing" : "started" : "stopped",
            ["recovery"] = _journal?.DiagnosticState ?? "unavailable",
            ["validation"] = "blind"
        };
        foreach (var service in _services)
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
                _cycleIdentity = await _hardware.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
                await DeviceServiceLifecycle.AcquireAsync(_controller, Context(context.Deadline), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ReleaseControllerCoreAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
            }

            await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _quiescing = true;
        _serializer.StopObservation();
        return await _serializer.RunAsync(async () =>
        {
            if (_services.Count > 0)
            {
                await DeviceServiceLifecycle.ReleaseAllAsync(_services, Context(context.Deadline), cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = DeviceServiceLifecycle.StopResult(_services);
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
                new PluginStopContext(PluginStopReason.WsgmExiting, Deadline.After(TimeSpan.FromSeconds(12))),
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

        _serializer.Dispose();
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
            _services,
            Context(Deadline.After(TimeSpan.FromSeconds(12))),
            _host,
            _descriptorSet).ConfigureAwait(false);
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

    private DeviceCycleContext<AllyIdentityState> Context(Deadline deadline)
    {
        return new DeviceCycleContext<AllyIdentityState>(_cycleGeneration, deadline,
            _cycleIdentity ?? throw new InvalidOperationException("No cycle identity is available."));
    }

    private PluginStartResult CurrentStartResult()
    {
        return DeviceServiceLifecycle.StartResult(
            _services,
            service => service != _controller || _controller.Enabled,
            "No Ally hardware service could be acquired.",
            _host);
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
            new WindowsAllyKeyboardHook(),
            Task.Delay);
    }
}
