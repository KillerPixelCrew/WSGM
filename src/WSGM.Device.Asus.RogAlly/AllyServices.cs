// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Asus.RogAlly;

internal static class AllyServiceIds
{
    public const string VendorEvents = "asus-vendor-events";
    public const string Keyboard = "asus-oem-keyboard";
    public const string Power = "asus-power";
    public const string Fans = "asus-fans";
    public const string ChargeLimit = "asus-charge-limit";
    public const string Lighting = "asus-aura";
    public const string Motion = "ally-motion";
    public const string Controller = "physical-controller";

    /// <summary>Journal identity for the controller tables, which do not depend on BIOS.</summary>
    public const string McuFirmware = "mcu";
}

/// <summary>Front OEM buttons from the vendor collection's 0x5A input reports.</summary>
internal sealed class VendorEventService(
    IAllyVendorHid vendor,
    IPluginHostAdapter host,
    AllyOemButtonState buttons) : AllyService(AllyServiceIds.VendorEvents)
{
    private long _cycleGeneration;
    private AllyModel? _model;
    private long _sequence;

    public override bool Suspendable => true;

    public override async ValueTask<AllyServiceResult> AcquireAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        if (context.Identity.Model is not { } model)
        {
            return Set(AllyServiceState.Passive, Missing("The exact Ally identity no longer matches."));
        }

        _model = model;
        _cycleGeneration = context.CycleGeneration;
        return await vendor.StartAsync(OnEventAsync, cancellationToken).ConfigureAwait(false)
            ? Set(AllyServiceState.Owned)
            : Set(AllyServiceState.Passive, Missing("The ASUS vendor collection (FF31:0080) was not found."));
    }

    public override async ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        await vendor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(AllyServiceState.Idle);
    }

    internal ValueTask OnEventAsync(byte code, DateTimeOffset timestamp)
    {
        if (_model is null || AllyModels.VendorAction(_model, code) is not { } action)
        {
            return ValueTask.CompletedTask;
        }

        if (code is 0xA7 or 0xA8)
        {
            buttons.Hold(action.Button, action.Edge is OemControlEdge.Pressed);
        }
        else if (action.Button is not CanonicalButtons.None)
        {
            buttons.Latch(action.Button, timestamp);
        }

        return host.PublishOemEventAsync(
            new OemControlEvent(action.ControlId, action.Press, _cycleGeneration, timestamp,
                $"asus-5a-{code:X2}-{Interlocked.Increment(ref _sequence)}", action.Edge),
            CancellationToken.None);
    }
}

/// <summary>OEM buttons the firmware sends as keyboard keys: M1/M2 and the Xbox models' front keys.</summary>
internal sealed class KeyboardOemService(
    IAllyKeyboardSource keyboard,
    IPluginHostAdapter host,
    AllyOemButtonState buttons) : AllyService(AllyServiceIds.Keyboard)
{
    private readonly HashSet<uint> _down = [];
    private readonly Lock _gate = new();
    private long _cycleGeneration;
    private IReadOnlyList<AllyKeyboardControl> _front = [];
    private bool _rearEnabled;

    public override bool Suspendable => true;

    /// <summary>
    ///     Rear keys by what the Device Lab run on RC73XA observed after HHD's table was applied: the
    ///     left button (M1) sent F18 and the right (M2) F17. HHD reads F17 as left (<c>base.py:396-403</c>)
    ///     and HC labels F18 as M1 (<c>ROGAlly.cs:263-282</c>); the lab agrees with HC here.
    /// </summary>
    internal static IReadOnlyList<AllyKeyboardControl> Rear { get; } =
    [
        new(AllyModels.VkF18, OemControlIds.M1, CanonicalButtons.RearPaddle1),
        new(AllyModels.VkF17, OemControlIds.M2, CanonicalButtons.RearPaddle2)
    ];

    public override async ValueTask<AllyServiceResult> AcquireAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        if (context.Identity.Model is not { } model)
        {
            return Set(AllyServiceState.Passive, Missing("The exact Ally identity no longer matches."));
        }

        _cycleGeneration = context.CycleGeneration;
        _front = model.FrontKeyboardControls;
        UpdateWatch();
        return await keyboard.StartAsync(OnKeyAsync, OnFault, cancellationToken).ConfigureAwait(false)
            ? Set(AllyServiceState.Owned)
            : Set(AllyServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                "The low-level keyboard hook could not be installed."));
    }

    public override async ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        keyboard.Watch([]);
        await keyboard.StopAsync(cancellationToken).ConfigureAwait(false);
        ReleaseHeld();
        return Set(AllyServiceState.Idle);
    }

    /// <summary>Claims M1/M2 only while the controller tables that make them F-keys are applied.</summary>
    public void SetRearEnabled(bool enabled)
    {
        lock (_gate)
        {
            _rearEnabled = enabled;
        }

        UpdateWatch();
        if (!enabled)
        {
            buttons.Hold(CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2, false);
        }
    }

    internal async ValueTask OnKeyAsync(AllyKeyEvent key)
    {
        AllyKeyboardControl? control;
        bool changed;
        lock (_gate)
        {
            control = Watched().Where(item => item.VirtualKey == key.VirtualKey)
                .Select(item => (AllyKeyboardControl?)item)
                .FirstOrDefault();
            changed = key.Down ? _down.Add(key.VirtualKey) : _down.Remove(key.VirtualKey);
        }

        // A held key repeats its down edge; only the first down and the up are edges.
        if (control is not { } mapped || !changed)
        {
            return;
        }

        buttons.Hold(mapped.Button, key.Down);
        await host.PublishOemEventAsync(
            new OemControlEvent(mapped.ControlId, OemPressKind.Short, _cycleGeneration, key.Timestamp,
                $"asus-key-{mapped.VirtualKey:X2}-{key.Timestamp.UtcTicks}",
                key.Down ? OemControlEdge.Pressed : OemControlEdge.Released),
            CancellationToken.None).ConfigureAwait(false);
    }

    private void OnFault(Exception exception)
    {
        var detail = AllyDiagnosticText.FromException("The OEM keyboard hook stopped", exception);
        Fault(new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
        host.ReportFault(ServiceId, detail);
    }

    private void UpdateWatch()
    {
        lock (_gate)
        {
            keyboard.Watch([.. Watched().Select(item => item.VirtualKey)]);
        }
    }

    private IEnumerable<AllyKeyboardControl> Watched()
    {
        return _rearEnabled ? _front.Concat(Rear) : _front;
    }

    private void ReleaseHeld()
    {
        lock (_gate)
        {
            _down.Clear();
        }

        buttons.Clear();
    }
}

/// <summary>Power limits and performance mode.</summary>
internal sealed class PowerService(
    IAsusAcpi acpi,
    AllyRecoveryJournal journal) : AllyService(AllyServiceIds.Power)
{
    public AllyPowerCapability? Capability { get; private set; }

    public AllyPowerState? LastObserved { get; private set; }

    public override ValueTask<AllyServiceResult> AcquireAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReconciliationBlockReason is not null)
        {
            return ValueTask.FromResult(Set(AllyServiceState.Faulted, ReconciliationBlockReason));
        }

        if (context.Identity.Model is not { } model)
        {
            return ValueTask.FromResult(Set(AllyServiceState.Passive,
                Missing("The exact Ally identity no longer matches.")));
        }

        if (!acpi.TryOpen())
        {
            return ValueTask.FromResult(Set(AllyServiceState.Passive,
                Missing("The ASUS System Control Interface driver (ATKACPI) is not installed.")));
        }

        Capability = new AllyPowerCapability(acpi, model);
        LastObserved = Capability.Read();
        PluginTrace.Info("power",
            $"SPL={LastObserved.Sustained?.ToString() ?? "?"} SPPT={LastObserved.Slow?.ToString() ?? "?"} "
            + $"FPPT={LastObserved.Fast?.ToString() ?? "?"} mode={LastObserved.Mode?.ToString() ?? "?"}.");
        return ValueTask.FromResult(Set(AllyServiceState.Owned));
    }

    public void Refresh()
    {
        LastObserved = Capability?.Read();
    }

    /// <summary>Journals the original state before the first write of this cycle, when it can be read.</summary>
    public async ValueTask<bool> PrepareWriteAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        var original = Capability!.Read();
        if (!original.LimitsReadable || original.Mode is null)
        {
            return false;
        }

        _ = await journal.BeginAsync(ServiceId, identity.FirmwareIdentity, AllyRecoveryState.Power(original),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override async ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(AllyServiceState.Faulted, ReconciliationBlockReason);
        }

        if (State is not AllyServiceState.Owned || Capability is null)
        {
            return Set(AllyServiceState.Idle);
        }

        if (journal.OriginalStateFor(ServiceId)?.ToPower() is { } original)
        {
            AllyWriteBudget.Require(context.Deadline, "power restoration");
            bool restored;
            try
            {
                restored = await Capability.RestoreAsync(original, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception)
            {
                await journal.CompleteAsync(ServiceId, AllyRecoveryStatus.RestoreFailed, CancellationToken.None)
                    .ConfigureAwait(false);
                return Set(AllyServiceState.Faulted, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    AllyDiagnosticText.FromException("Power restoration failed", ex)));
            }

            await journal.CompleteAsync(ServiceId,
                restored ? AllyRecoveryStatus.RestoredVerified : AllyRecoveryStatus.RestoredUnverified,
                cancellationToken).ConfigureAwait(false);
            if (!restored)
            {
                return Set(AllyServiceState.ReleasedUnverified, new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "The captured power limits did not read back after restoration."));
            }
        }

        return Set(AllyServiceState.Idle);
    }
}

/// <summary>Fan curves and fan readings.</summary>
internal sealed class FanService(
    IAsusAcpi acpi,
    AllyRecoveryJournal journal) : AllyService(AllyServiceIds.Fans)
{
    public AllyFanCapability? Capability { get; private set; }

    public AllyFanSnapshot? LastObserved { get; private set; }

    public (int? Cpu, int? Gpu) LastFans { get; private set; }

    public AllyFanSnapshot? Original => journal.OriginalStateFor(ServiceId)?.ToFans();

    public override ValueTask<AllyServiceResult> AcquireAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReconciliationBlockReason is not null)
        {
            return ValueTask.FromResult(Set(AllyServiceState.Faulted, ReconciliationBlockReason));
        }

        if (!context.Identity.ExactMachineMatch)
        {
            return ValueTask.FromResult(Set(AllyServiceState.Passive,
                Missing("The exact Ally identity no longer matches.")));
        }

        if (!acpi.TryOpen())
        {
            return ValueTask.FromResult(Set(AllyServiceState.Passive,
                Missing("The ASUS System Control Interface driver (ATKACPI) is not installed.")));
        }

        Capability = new AllyFanCapability(acpi);
        Capability.Probe();
        Refresh();
        PluginTrace.Info("fans",
            $"curves readable={LastObserved?.Readable == true}, mid fan={Capability.HasMidFan}.");
        return ValueTask.FromResult(Set(AllyServiceState.Owned));
    }

    public void Refresh()
    {
        if (Capability is null)
        {
            return;
        }

        LastObserved = Capability.Read();
        LastFans = Capability.ReadFans();
    }

    public async ValueTask<bool> PrepareWriteAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        var original = Capability!.Read();
        if (!original.Readable || original.Mode is null || (Capability.HasMidFan && original.Mid is null))
        {
            return false;
        }

        _ = await journal.BeginAsync(ServiceId, identity.FirmwareIdentity, AllyRecoveryState.Fans(original),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override async ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(AllyServiceState.Faulted, ReconciliationBlockReason);
        }

        if (State is not AllyServiceState.Owned || Capability is null || Original is not { } original)
        {
            return Set(AllyServiceState.Idle);
        }

        AllyWriteBudget.Require(context.Deadline, "fan restoration");
        bool restored;
        try
        {
            restored = await Capability.RestoreAsync(original, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            await journal.CompleteAsync(ServiceId, AllyRecoveryStatus.RestoreFailed, CancellationToken.None)
                .ConfigureAwait(false);
            return Set(AllyServiceState.Faulted, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                AllyDiagnosticText.FromException("Fan restoration failed", ex)));
        }

        // Keep an unverified restore visible to the next cycle. It must not be called verified.
        await journal.CompleteAsync(ServiceId,
                restored ? AllyRecoveryStatus.RestoredVerified : AllyRecoveryStatus.RestoredUnverified,
                cancellationToken)
            .ConfigureAwait(false);
        return restored
            ? Set(AllyServiceState.Idle)
            : Set(AllyServiceState.ReleasedUnverified, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                "The captured fan curves were written back but did not read back."));
    }
}

/// <summary>Battery charge ceiling: a persistent user choice, never reverted on stop.</summary>
internal sealed class ChargeLimitService(IAsusAcpi acpi) : AllyService(AllyServiceIds.ChargeLimit)
{
    public AllyChargeLimitCapability? Capability { get; private set; }

    public int? LastObserved { get; private set; }

    public override ValueTask<AllyServiceResult> AcquireAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.Identity.ExactMachineMatch)
        {
            return ValueTask.FromResult(Set(AllyServiceState.Passive,
                Missing("The exact Ally identity no longer matches.")));
        }

        if (!acpi.TryOpen())
        {
            return ValueTask.FromResult(Set(AllyServiceState.Passive,
                Missing("The ASUS System Control Interface driver (ATKACPI) is not installed.")));
        }

        Capability = new AllyChargeLimitCapability(acpi);
        Refresh();
        return ValueTask.FromResult(Set(AllyServiceState.Owned));
    }

    public void Refresh()
    {
        LastObserved = Capability?.Read();
    }

    public override ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Set(AllyServiceState.Idle));
    }
}

/// <summary>What the plugin last sent to Aura. Aura cannot be read back, so this is intent, not observation.</summary>
internal sealed record AllyLightingState(int Brightness, AuraEffect Effect, int Speed, int LeftColor, int RightColor)
{
    public static AllyLightingState Initial { get; } = new(100, AuraEffect.Solid, 50, 0xFF0000, 0xFF0000);
}

/// <summary>Aura RGB on the joystick rings.</summary>
/// <remarks>
///     HC's sequence: brightness as a feature report, then the colour message, apply and set as output
///     reports (<c>ROGAlly.cs:507-593</c>). Solid colour uses HC's per-zone path (<c>ApplyColorFast</c>)
///     so the left and right rings can differ; the animated effects address all zones, with the right
///     ring's colour as the breathing effect's second colour.
/// </remarks>
internal sealed class LightingService(IAllyAuraHid aura) : AllyService(AllyServiceIds.Lighting)
{
    private bool _dynamicLightingHandled;
    private AllyModel? _model;

    public AllyLightingState Desired { get; private set; } = AllyLightingState.Initial;

    public override async ValueTask<AllyServiceResult> AcquireAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        if (context.Identity.Model is not { } model)
        {
            return Set(AllyServiceState.Passive, Missing("The exact Ally identity no longer matches."));
        }

        _model = model;
        _dynamicLightingHandled = false;
        return await aura.IsAvailableAsync(cancellationToken).ConfigureAwait(false)
            ? Set(AllyServiceState.Owned)
            : Set(AllyServiceState.Passive, Missing("No ASUS collection answered Aura report 0x5D."));
    }

    public override ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Lighting is a persistent user choice; stopping never rewrites it.
        return ValueTask.FromResult(Set(AllyServiceState.Idle));
    }

    public async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        Func<AllyLightingState, AllyLightingState> update,
        CancellationToken cancellationToken)
    {
        var wanted = update(Desired);
        if (wanted.Brightness is < 0 or > 100 || wanted.Speed is < 0 or > 100
                                              || wanted.LeftColor is < 0 or > 0xFFFFFF
                                              || wanted.RightColor is < 0 or > 0xFFFFFF)
        {
            return AllyResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "Lighting values are outside their range.");
        }

        try
        {
            if (_model?.DisableDynamicLighting == true && !_dynamicLightingHandled)
            {
                // One attempt per cycle; the result is traced, never retried.
                _dynamicLightingHandled = true;
                var disabled = await aura.DisableDynamicLightingAsync(cancellationToken).ConfigureAwait(false);
                PluginTrace.Info("lighting", $"lamp array autonomous mode requested: {disabled}.");
            }

            foreach (var report in Encode(wanted))
            {
                if (report.Feature)
                {
                    await aura.SetFeatureAsync(report.Bytes, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await aura.WriteOutputAsync(report.Bytes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            PluginTrace.Failure("lighting", "Aura write failed", ex);
            // The previous intent is kept: nothing reads Aura back, so the device state is unknown.
            return AllyResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
                AllyDiagnosticText.FromException("The Aura write failed", ex), RollbackResult.RestoreFailed);
        }

        Desired = wanted;
        return AllyResults.Unverified(command, "Aura lighting is write-only; the device cannot report what it shows.");
    }

    /// <summary>The reports one lighting state needs, in HC's order.</summary>
    internal static IReadOnlyList<(byte[] Bytes, bool Feature)> Encode(AllyLightingState state)
    {
        List<(byte[], bool)> reports = [(AllyProtocol.Brightness(state.Brightness), true)];
        var speed = AllyProtocol.Speed(state.Speed);
        if (state.Effect is AuraEffect.Solid)
        {
            reports.Add((
                AllyProtocol.Color(AuraEffect.Solid, AuraZone.LeftStickLeft, state.LeftColor, state.LeftColor, speed),
                false));
            reports.Add((
                AllyProtocol.Color(AuraEffect.Solid, AuraZone.LeftStickRight, state.LeftColor, state.LeftColor, speed),
                false));
            reports.Add((
                AllyProtocol.Color(AuraEffect.Solid, AuraZone.RightStickLeft, state.RightColor, state.RightColor,
                    speed), false));
            reports.Add((
                AllyProtocol.Color(AuraEffect.Solid, AuraZone.RightStickRight, state.RightColor, state.RightColor,
                    speed), false));
        }
        else
        {
            reports.Add((AllyProtocol.Color(state.Effect, AuraZone.All, state.LeftColor, state.RightColor, speed),
                false));
        }

        reports.Add((AllyProtocol.Apply(), false));
        reports.Add((AllyProtocol.Set(), false));
        return reports;
    }
}

/// <summary>The IMU, attached to controller samples with the Claw's frame resampler.</summary>
internal sealed class AllyMotionService(IAllyMotionSource source) : AllyService(AllyServiceIds.Motion)
{
    /// <summary>How long a reading may still ride a controller sample. The Claw's value.</summary>
    internal static readonly TimeSpan MaximumMotionAge = TimeSpan.FromMilliseconds(50);

    private readonly Lock _gate = new();
    private readonly GyroFrameResampler _resampler = new();
    private MotionSample? _latest;

    public override bool Suspendable => true;

    public MotionSample? Current(DateTimeOffset now)
    {
        MotionSample sample;
        lock (_gate)
        {
            if (_latest is not { } latest)
            {
                return null;
            }

            sample = latest;
        }

        if (sample.SensorTimestamp is null)
        {
            return sample;
        }

        var average = _resampler.FrameAverage(now);
        return sample with { GyroX = average.X, GyroY = average.Y, GyroZ = average.Z };
    }

    public override async ValueTask<AllyServiceResult> AcquireAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _latest = null;
        }

        _resampler.Reset();
        var started = await source.StartAsync(sample =>
        {
            lock (_gate)
            {
                _latest = sample;
            }

            if (sample.SensorTimestamp is { } stamp)
            {
                _resampler.OnReading(new Vector3(sample.GyroX, sample.GyroY, sample.GyroZ), stamp);
            }

            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return started
            ? Set(AllyServiceState.Owned)
            : Set(AllyServiceState.Passive, Missing("No Windows gyrometer and accelerometer pair was found."));
    }

    public override async ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        await source.StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _latest = null;
        }

        _resampler.Reset();
        return Set(AllyServiceState.Idle);
    }
}

/// <summary>The physical pad: controller tables, sample stream, haptics and the HidHide identities.</summary>
internal sealed class ControllerService(
    IAllyControllerSource source,
    IAllyVendorHid vendor,
    AllyMotionService motion,
    KeyboardOemService keyboard,
    AllyOemButtonState buttons,
    IPluginHostAdapter host,
    AllyRecoveryJournal journal) : AllyService(AllyServiceIds.Controller)
{
    /// <summary>What the Ally's motors can do: two rumble motors, no trigger haptics.</summary>
    /// <remarks>
    ///     XInput carries one large and one small motor. The frame rate matches the pad poll. Motor
    ///     floors are not declared: nothing has measured them on an Ally.
    /// </remarks>
    internal static readonly HapticCapabilities OutputCapabilities = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        LeftTrigger = OutputChannelSupport.Unsupported,
        RightTrigger = OutputChannelSupport.Unsupported,
        MaxFramesPerSecond = 125
    };

    private readonly Lock _hapticGate = new();
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private bool _configured;
    private float _lastHigh;
    private float _lastLow;
    private AllyControllerTopology? _topology;

    public bool Enabled { get; set; }

    public override bool Suspendable => true;

    public IReadOnlyList<PhysicalDeviceIdentity> LastReleasedDevices { get; private set; } = [];

    public override async ValueTask<AllyServiceResult> AcquireAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Set(AllyServiceState.Passive, new CapabilityReason(CapabilityReasonCode.ResourceReleased,
                "Controller management is disabled."));
        }

        if (ReconciliationBlockReason is not null)
        {
            return Set(AllyServiceState.Faulted, ReconciliationBlockReason);
        }

        if (!context.Identity.ExactMachineMatch)
        {
            return Set(AllyServiceState.Passive, Missing("The exact Ally identity no longer matches."));
        }

        LastReleasedDevices = [];
        var topology = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        host.Trace(topology is null ? DeviceTraceLevel.Warn : DeviceTraceLevel.Info, "controller",
            topology is null
                ? "no ASUS XInput slot or Windows.Gaming.Input pad was found."
                : $"route={topology.Route}, devices={topology.PhysicalDevices.Count}, observed=[{topology.Observed}]");
        if (topology is null)
        {
            return Set(AllyServiceState.Passive, Missing("The Ally gamepad was not found."));
        }

        if (topology.PhysicalDevices.Count == 0)
        {
            // Without an identity to hide, Steam would see the physical pad beside the virtual one.
            return Set(AllyServiceState.Passive, Missing(
                $"No hideable XUSB, GIP or XInput HID node was found for the pad ({topology.Observed})."));
        }

        _topology = topology;
        await ConfigureAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            await source.StartAsync(topology, context.CycleGeneration, PublishSampleAsync, OnReaderFault,
                cancellationToken).ConfigureAwait(false);
            await host.PublishPhysicalDevicesAsync(topology.PhysicalDevices, OutputCapabilities, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            host.Trace(DeviceTraceLevel.Error, "controller",
                AllyDiagnosticText.FromException("controller acquisition failed", ex));
            _ = await ReleaseControllerAsync(context.Deadline, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return Set(AllyServiceState.Owned);
    }

    public override async ValueTask<AllyServiceResult> SuspendAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        var result = await ReleaseControllerAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        return result is ControllerHandoffResult.ReleasedVerified
            ? Set(AllyServiceState.Idle)
            : Set(AllyServiceState.ReleasedUnverified, Reason);
    }

    public override async ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        var result = await ReleaseControllerAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        return result is ControllerHandoffResult.ReleasedVerified
            ? Set(AllyServiceState.Idle)
            : Set(AllyServiceState.ReleasedUnverified, Reason ?? new CapabilityReason(
                CapabilityReasonCode.TransportFaulted, "The controller release could not be verified."));
    }

    public async ValueTask<ControllerHandoffResult> ReleaseControllerAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        _ = Set(AllyServiceState.Releasing);
        CapabilityReason? failure = null;
        await _outputGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await source.WriteRumbleAsync(0, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var detail = AllyDiagnosticText.FromException("zero rumble failed", ex);
            host.Trace(DeviceTraceLevel.Warn, "controller", detail);
            failure = new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail);
        }
        finally
        {
            _outputGate.Release();
        }

        try
        {
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                AllyDiagnosticText.FromException("The controller reader did not stop cleanly", ex));
        }

        keyboard.SetRearEnabled(false);
        buttons.Hold(CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2, false);
        lock (_hapticGate)
        {
            _lastLow = 0;
            _lastHigh = 0;
        }

        if (_configured)
        {
            var restoreFailure = await RestoreConfigurationAsync(deadline).ConfigureAwait(false);
            failure ??= restoreFailure;
        }

        LastReleasedDevices = _topology?.PhysicalDevices ?? [];
        _topology = null;
        if (failure is not null)
        {
            _ = Set(AllyServiceState.ReleasedUnverified, failure);
            return ControllerHandoffResult.ReleasedUnverified;
        }

        _ = Set(AllyServiceState.Idle);
        return ControllerHandoffResult.ReleasedVerified;
    }

    public async ValueTask ApplyHapticsAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        var clamped = OutputCapabilities.Clamp(frame);
        await _outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not AllyServiceState.Owned)
            {
                return;
            }

            lock (_hapticGate)
            {
                if (Math.Abs(clamped.LowFrequency - _lastLow) < 0.002f
                    && Math.Abs(clamped.HighFrequency - _lastHigh) < 0.002f)
                {
                    return;
                }
            }

            await source.WriteRumbleAsync(clamped.LowFrequency, clamped.HighFrequency, cancellationToken)
                .ConfigureAwait(false);
            lock (_hapticGate)
            {
                _lastLow = clamped.LowFrequency;
                _lastHigh = clamped.HighFrequency;
            }
        }
        finally
        {
            _outputGate.Release();
        }
    }

    /// <summary>Writes the tables that turn M1/M2 into F17/F18, journalled first.</summary>
    private async ValueTask ConfigureAsync(AllyCycleContext context, CancellationToken cancellationToken)
    {
        if (!await vendor.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            host.Trace(DeviceTraceLevel.Warn, "controller",
                "no vendor collection for the controller tables; M1 and M2 stay unavailable.");
            return;
        }

        AllyWriteBudget.Require(context.Deadline, "controller configuration");
        _ = await journal.BeginAsync(ServiceId, AllyServiceIds.McuFirmware, AllyRecoveryState.Controller(),
            cancellationToken).ConfigureAwait(false);
        _configured = true;
        try
        {
            foreach (var report in AllyProtocol.GameModeConfiguration)
            {
                await vendor.WriteConfigurationAsync(report, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            // Each table is one uncertain write; none is retried. Put the defaults back and carry on
            // without the rear buttons: the pad itself does not depend on these tables.
            host.Trace(DeviceTraceLevel.Error, "controller",
                AllyDiagnosticText.FromException("controller tables were not all written", ex));
            _ = await RestoreConfigurationAsync(context.Deadline).ConfigureAwait(false);
            return;
        }

        host.Trace(DeviceTraceLevel.Info, "controller", "controller tables written; M1/M2 now send F18/F17.");
        keyboard.SetRearEnabled(true);
    }

    /// <summary>Writes the factory M1/M2 tables back.</summary>
    /// <remarks>
    ///     The MCU tables cannot be read, so there is no captured original and no readback: the release
    ///     can be acknowledged but never verified, and is reported as unverified.
    /// </remarks>
    private async ValueTask<CapabilityReason> RestoreConfigurationAsync(DateTimeOffset deadline)
    {
        _configured = false;
        try
        {
            AllyWriteBudget.Require(deadline, "controller table restoration");
            foreach (var report in AllyProtocol.DefaultConfiguration)
            {
                await vendor.WriteConfigurationAsync(report, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException
                                       or OperationCanceledException)
        {
            await journal.CompleteAsync(ServiceId, AllyRecoveryStatus.RestoreFailed, CancellationToken.None)
                .ConfigureAwait(false);
            return new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                AllyDiagnosticText.FromException("The factory controller tables could not be written back", ex));
        }

        // Every report was acknowledged; nothing more can be done for an unreadable table, so the
        // journal entry is cleared rather than left to block the next cycle.
        await journal.CompleteAsync(ServiceId, AllyRecoveryStatus.RestoredVerified, CancellationToken.None)
            .ConfigureAwait(false);
        return new CapabilityReason(CapabilityReasonCode.TransportFaulted,
            "The factory controller tables were written back; the MCU cannot report them to verify.");
    }

    private async ValueTask PublishSampleAsync(CanonicalControllerSample sample, CancellationToken cancellationToken)
    {
        await host.PublishControllerSampleAsync(sample with { Motion = motion.Current(sample.Timestamp) },
            cancellationToken).ConfigureAwait(false);
    }

    private void OnReaderFault(Exception exception)
    {
        var detail = AllyDiagnosticText.FromException("The controller reader stopped", exception);
        Fault(new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
        host.ReportFault("controller", detail);
    }
}
