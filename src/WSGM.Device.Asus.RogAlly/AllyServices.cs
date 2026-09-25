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
    private readonly HashSet<byte> _unmapped = [];
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
        // A faulted reader keeps its task until stopped; stopping it lets this start open the collection again.
        await vendor.StopAsync(cancellationToken).ConfigureAwait(false);
        return await vendor.StartAsync(OnEventAsync, OnFault, cancellationToken).ConfigureAwait(false)
            ? Set(AllyServiceState.Owned)
            : Set(AllyServiceState.Passive,
                Missing("No ASUS vendor collection (FF31:0080, or one answering feature report 0x5A) was found."));
    }

    private void OnFault(Exception exception)
    {
        var detail = AllyDiagnosticText.FromException("The vendor event reader stopped", exception);
        Fault(new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
        host.ReportFault(ServiceId, detail);
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
        if (_model is null)
        {
            return ValueTask.CompletedTask;
        }

        if (AllyModels.VendorAction(_model, code) is not { } action)
        {
            // Once per code, so a tester's log names what a silent button sends.
            if (_unmapped.Add(code))
            {
                PluginTrace.Info("vendor-hid", $"unmapped vendor event 0x{code:X2}.");
            }

            return ValueTask.CompletedTask;
        }

        // Only M2 (0xA7/0xA8) reports a release; HC press-and-releases the others (ROGAlly.cs:485-505).
        var releases = code is 0xA7 or 0xA8;
        if (!buttons.Admit(action.ControlId, AllyOemSource.Vendor, action.Edge, releases, timestamp))
        {
            return ValueTask.CompletedTask;
        }

        if (releases)
        {
            buttons.Hold(AllyOemSource.Vendor, action.Button, action.Edge is OemControlEdge.Pressed);
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
    private bool _rearRemapped;

    public override bool Suspendable => true;

    /// <summary>Rear keys while HC's M1/M2 table is applied: the left button sends F17, the right F18.</summary>
    /// <remarks>
    ///     HHD reads the same table bytes that way (<c>base.py:396-403</c>), and an Xbox Ally X tester whose
    ///     tables were accepted found the native assignment below swapped (2026-09-26).
    /// </remarks>
    internal static IReadOnlyList<AllyKeyboardControl> Rear { get; } =
    [
        new(AllyModels.VkF17, OemControlIds.M1, CanonicalButtons.RearPaddle1),
        new(AllyModels.VkF18, OemControlIds.M2, CanonicalButtons.RearPaddle2)
    ];

    /// <summary>Rear keys the Xbox Ally X firmware sends with no table written: left F18, right F17.</summary>
    /// <remarks>Device Lab RC73XA run 2026-09-25, <c>back-left1</c> F18 and <c>back-right1</c> F17.</remarks>
    internal static IReadOnlyList<AllyKeyboardControl> NativeRear { get; } =
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
        if (!Watched().Any())
        {
            // No key to claim yet (a classic model without the controller tables): no system-wide hook.
            return Set(AllyServiceState.Owned);
        }

        return await keyboard.StartAsync(OnKeyAsync, OnFault, cancellationToken).ConfigureAwait(false)
            ? Set(AllyServiceState.Owned)
            : HookUnavailable();
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

    /// <summary>Claims M1/M2 while they send F-keys: with the controller tables applied, or natively.</summary>
    /// <param name="enabled">Whether the rear keys are watched.</param>
    /// <param name="remapped">Whether HC's M1/M2 table is applied, which swaps the keys' sides.</param>
    /// <param name="cancellationToken">Cancels hook installation.</param>
    /// <remarks>The hook is installed with the first watched key and removed when none is left.</remarks>
    public async ValueTask SetRearEnabledAsync(
        bool enabled,
        bool remapped,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _rearEnabled = enabled;
            _rearRemapped = remapped;
        }

        UpdateWatch();
        if (State is AllyServiceState.Owned)
        {
            if (enabled && !await keyboard.StartAsync(OnKeyAsync, OnFault, cancellationToken).ConfigureAwait(false))
            {
                _ = HookUnavailable();
            }
            else if (!enabled && _front.Count == 0)
            {
                await keyboard.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (!enabled)
        {
            lock (_gate)
            {
                _down.Remove(AllyModels.VkF17);
                _down.Remove(AllyModels.VkF18);
            }

            buttons.Hold(AllyOemSource.Keyboard, CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2, false);
            buttons.Forget(AllyOemSource.Keyboard, OemControlIds.M1, OemControlIds.M2);
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

        var edge = key.Down ? OemControlEdge.Pressed : OemControlEdge.Released;
        if (!buttons.Admit(mapped.ControlId, AllyOemSource.Keyboard, edge, true, key.Timestamp))
        {
            return;
        }

        buttons.Hold(AllyOemSource.Keyboard, mapped.Button, key.Down);
        await host.PublishOemEventAsync(
            new OemControlEvent(mapped.ControlId, OemPressKind.Short, _cycleGeneration, key.Timestamp,
                $"asus-key-{mapped.VirtualKey:X2}-{key.Timestamp.UtcTicks}", edge),
            CancellationToken.None).ConfigureAwait(false);
    }

    private AllyServiceResult HookUnavailable()
    {
        return Set(AllyServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
            "The low-level keyboard hook could not be installed."));
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
        return _rearEnabled ? _front.Concat(_rearRemapped ? Rear : NativeRear) : _front;
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

    /// <summary>Whether this cycle journalled an original that stop restores.</summary>
    public bool HasJournalledOriginal => journal.PendingOriginalFor(ServiceId) is not null;

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
        LastObserved = Capability?.Effective();
    }

    /// <summary>Journals the original state before the first write of this cycle, when it can be read.</summary>
    /// <remarks>
    ///     A firmware that cannot report its limits and mode is written anyway, as HC does
    ///     (<c>ROGAlly.cs:694-702</c>). Nothing is journalled then, so stop has nothing to restore.
    /// </remarks>
    public async ValueTask PrepareWriteAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        if (HasJournalledOriginal)
        {
            return;
        }

        var original = Capability!.Read();
        if (!original.LimitsReadable || original.Mode is null)
        {
            PluginTrace.Change("power", "journal",
                "The firmware does not report its power limits and mode; writing without a restore point.");
            return;
        }

        _ = await journal.BeginAsync(ServiceId, identity.FirmwareIdentity, AllyRecoveryState.Power(original),
            cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(AllyServiceState.Faulted, ReconciliationBlockReason);
        }

        // A service faulted by a failed command rollback still owns the ATKACPI handle and the journalled
        // original, and stop is the designed restore point for both.
        if (State is not (AllyServiceState.Owned or AllyServiceState.Faulted) || Capability is null)
        {
            return Set(AllyServiceState.Idle);
        }

        if (journal.PendingOriginalFor(ServiceId)?.ToPower() is { } original)
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

    /// <summary>Whether this cycle journalled an original that stop restores.</summary>
    public bool HasJournalledOriginal => journal.PendingOriginalFor(ServiceId) is not null;

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

    /// <summary>Journals the original curves before the first write of this cycle, when they can be read.</summary>
    /// <remarks>
    ///     A firmware that refuses the curve query is written anyway, as HC does; stop then returns the
    ///     fans to HC's factory tables rather than to a captured original (<c>ROGAlly.cs:466-478</c>).
    /// </remarks>
    public async ValueTask PrepareWriteAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        if (HasJournalledOriginal)
        {
            return;
        }

        var original = Capability!.Read();
        if (!original.Readable || original.Mode is null || (Capability.HasMidFan && original.Mid is null))
        {
            PluginTrace.Change("fans", "journal",
                "The firmware does not report its fan curves; stop will write HC's factory tables.");
            return;
        }

        _ = await journal.BeginAsync(ServiceId, identity.FirmwareIdentity, AllyRecoveryState.Fans(original),
            cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(AllyServiceState.Faulted, ReconciliationBlockReason);
        }

        if (State is not (AllyServiceState.Owned or AllyServiceState.Faulted) || Capability is null)
        {
            return Set(AllyServiceState.Idle);
        }

        if (journal.PendingOriginalFor(ServiceId)?.ToFans() is not { } original)
        {
            return await ReleaseToFactoryAsync(context, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Without a captured original, a custom curve is replaced by HC's factory tables.</summary>
    private async ValueTask<AllyServiceResult> ReleaseToFactoryAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        if (Capability!.WrittenCpu is not { } written
            || written.AsSpan().SequenceEqual(AllyFanCapability.DefaultCpuCurve))
        {
            return Set(AllyServiceState.Idle);
        }

        AllyWriteBudget.Require(context.Deadline, "fan restoration");
        try
        {
            await Capability.WriteFactoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            return Set(AllyServiceState.Faulted, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                AllyDiagnosticText.FromException("Fan restoration failed", ex)));
        }

        return Set(AllyServiceState.Idle);
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
///     HC's sequence: brightness as a feature report, then the colour messages as output reports
///     (<c>ROGAlly.cs:507-593</c>). Two different solid ring colours use HC's per-zone path
///     (<c>ApplyColorFast</c>); everything else is one all-zone message, with the right ring's colour as
///     the breathing effect's second colour.
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
    /// <remarks>
    ///     One colour, or any animated effect, is HC's <c>ApplyColor</c>: one all-zone message, then apply
    ///     and set (<c>ROGAlly.cs:555-572</c>). Two different solid colours are HC's <c>ApplyColorFast</c>:
    ///     four per-zone messages at the slow speed with no apply or set (<c>ROGAlly.cs:574-593</c>).
    /// </remarks>
    internal static IReadOnlyList<(byte[] Bytes, bool Feature)> Encode(AllyLightingState state)
    {
        List<(byte[], bool)> reports = [(AllyProtocol.Brightness(state.Brightness), true)];
        if (state.Effect is AuraEffect.Solid && state.LeftColor != state.RightColor)
        {
            const byte speed = AllyProtocol.SpeedSlow;
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
            return reports;
        }

        reports.Add((AllyProtocol.Color(state.Effect, AuraZone.All, state.LeftColor, state.RightColor,
            AllyProtocol.Speed(state.Speed)), false));
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
    private long _generation;
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

        if (State is AllyServiceState.Owned && _topology is { } owned)
        {
            if (_generation == context.CycleGeneration)
            {
                return Set(AllyServiceState.Owned);
            }

            // A new cycle generation: samples must carry it, so the reader restarts. The tables stay
            // applied and are not written again.
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
            await source.StartAsync(owned, context.CycleGeneration, PublishSampleAsync, OnReaderFault,
                cancellationToken).ConfigureAwait(false);
            _generation = context.CycleGeneration;
            await host.PublishPhysicalDevicesAsync(owned.PhysicalDevices, OutputCapabilities, cancellationToken)
                .ConfigureAwait(false);
            return Set(AllyServiceState.Owned);
        }

        if (_topology is not null || _configured)
        {
            // A reader that faulted left its topology and tables behind; release them before starting over.
            _ = await ReleaseControllerAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
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

        if (topology.Route is AllyControllerRoute.WindowsGamingInput)
        {
            // GamepadButtons has no guide flag, and HC reads the Xbox button only through XInput's guide bit.
            host.Trace(DeviceTraceLevel.Warn, "controller",
                "Windows.Gaming.Input reports no guide button; the Xbox button is unavailable on this route.");
        }

        if (topology.PhysicalDevices.Count == 0)
        {
            // Without an identity to hide, Steam would see the physical pad beside the virtual one.
            return Set(AllyServiceState.Passive, Missing(
                $"No hideable XUSB, GIP or XInput HID node was found for the pad ({topology.Observed})."));
        }

        _topology = topology;
        _generation = context.CycleGeneration;
        if (!await ConfigureAsync(context, cancellationToken).ConfigureAwait(false)
            && context.Identity.Model?.RearKeysNative == true)
        {
            // The rear keys work without the tables on this model, so a refused table set does not
            // cost M1 and M2; they keep the firmware's own sides.
            await keyboard.SetRearEnabledAsync(true, false, cancellationToken).ConfigureAwait(false);
        }

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
        if (_topology is null && !_configured)
        {
            // Never acquired, or already released: there is no reader, motor or table to put back.
            LastReleasedDevices = [];
            _ = Set(AllyServiceState.Idle);
            return ControllerHandoffResult.ReleasedVerified;
        }

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

        await keyboard.SetRearEnabledAsync(false, false, CancellationToken.None).ConfigureAwait(false);
        buttons.Release(CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2);
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
    /// <returns>Whether the M1/M2 table was written and the rear keys are watched with its sides.</returns>
    private async ValueTask<bool> ConfigureAsync(AllyCycleContext context, CancellationToken cancellationToken)
    {
        if (!await vendor.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            host.Trace(DeviceTraceLevel.Warn, "controller",
                "no vendor collection for the controller tables; M1 and M2 stay unavailable.");
            return false;
        }

        if (!AllyWriteBudget.IsAvailable(context.Deadline))
        {
            host.Trace(DeviceTraceLevel.Warn, "controller",
                "too little time to write the controller tables; M1 and M2 stay unavailable this cycle.");
            return false;
        }

        _ = await journal.BeginAsync(ServiceId, AllyServiceIds.McuFirmware, AllyRecoveryState.Controller(),
            cancellationToken).ConfigureAwait(false);
        _configured = true;
        // HC writes every table and ignores each result (ROGAlly.cs:646-668). Each is one uncertain
        // write that is never retried; a refused table does not stop the others, and only a refused
        // M1/M2 table keeps the rear buttons off.
        List<string> refused = [];
        var rearWritten = false;
        foreach (var report in AllyProtocol.GameModeConfiguration)
        {
            try
            {
                await vendor.WriteConfigurationAsync(report, cancellationToken).ConfigureAwait(false);
                rearWritten |= ReferenceEquals(report, AllyProtocol.RearKeyboardMapping);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                refused.Add($"{report[2]:X2}/{report[3]:X2}: "
                            + AllyDiagnosticText.FromException("refused", ex));
            }
        }

        if (refused.Count > 0)
        {
            host.Trace(DeviceTraceLevel.Warn, "controller",
                $"{refused.Count} of {AllyProtocol.GameModeConfiguration.Count} controller tables refused: "
                + string.Join("; ", refused));
        }

        if (!rearWritten)
        {
            host.Trace(DeviceTraceLevel.Error, "controller", "the M1/M2 table was not written.");
            return false;
        }

        host.Trace(DeviceTraceLevel.Info, "controller",
            "controller tables written; left rear sends F17, right rear F18.");
        await keyboard.SetRearEnabledAsync(true, true, cancellationToken).ConfigureAwait(false);
        return true;
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
        }
        catch (AllyBudgetException ex)
        {
            // Nothing was written, so the entry stays outstanding and the next cycle restores the tables.
            return new CapabilityReason(CapabilityReasonCode.Quiescing,
                AllyDiagnosticText.FromException("The factory controller tables were not written back", ex));
        }

        // Like the forward write, every table is sent even when one is refused (HC ROGAlly.cs:646-668).
        Exception? failure = null;
        var refused = 0;
        foreach (var report in AllyProtocol.DefaultConfiguration)
        {
            try
            {
                await vendor.WriteConfigurationAsync(report, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                failure ??= ex;
                refused++;
            }
        }

        if (failure is not null)
        {
            // A partly written release is still the best that can be done for an unreadable table.
            var status = refused == AllyProtocol.DefaultConfiguration.Count
                ? AllyRecoveryStatus.RestoreFailed
                : AllyRecoveryStatus.RestoredUnverified;
            await journal.CompleteAsync(ServiceId, status, CancellationToken.None).ConfigureAwait(false);
            return new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                AllyDiagnosticText.FromException(
                    $"{refused} factory controller tables could not be written back", failure));
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
