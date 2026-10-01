using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Windows;

namespace WSGM.Device.Msi.Claw;

internal static class ClawDiagnosticText
{
    public static string FromException(string context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = $"{context} ({exception.GetType().Name}): {exception.Message}";
        var length = Math.Min(message.Length, PluginTrace.MaxMessageLength);
        var bounded = new char[length];
        for (var index = 0; index < bounded.Length; index++)
        {
            var character = message[index];
            bounded[index] = PlainText.IsUnsafe(character) ? ' ' : character;
        }

        return new string(bounded);
    }
}

internal enum ClawServiceState
{
    Idle,
    Acquiring,
    Owned,
    Passive,
    Degraded,
    Releasing,
    ReleasedUnverified,
    Faulted
}

internal readonly record struct ClawCycleContext(
    long CycleGeneration,
    Deadline Deadline,
    ClawIdentityState Identity);

internal sealed record ClawServiceResult(
    ClawServiceState State,
    CapabilityReason? Reason = null);

internal abstract class ClawServiceStatus(string serviceId)
{
    public string ServiceId { get; } = serviceId;

    public ClawServiceState State { get; protected set; } = ClawServiceState.Idle;

    public CapabilityReason? Reason { get; private set; }

    public CapabilityReason? ReconciliationBlockReason { get; set; }

    internal void ApplyResult(ClawServiceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        State = result.State;
        Reason = result.Reason;
    }

    public void Fault(CapabilityReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ReconciliationBlockReason = reason;
        _ = Set(ClawServiceState.Faulted, reason);
    }

    protected ClawServiceResult Set(
        ClawServiceState state,
        CapabilityReason? reason = null)
    {
        State = state;
        Reason = reason;
        return new ClawServiceResult(state, reason);
    }
}

/// <summary>A service the plugin acquires for a device cycle and releases when the cycle ends.</summary>
internal abstract class ClawCycleService(string serviceId) : ClawServiceStatus(serviceId)
{
    public abstract ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken);

    public abstract ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken);

    protected async ValueTask<ClawServiceResult> RestoreJournalledAsync<TSnapshot>(
        ClawCycleContext context,
        ClawRecoveryJournal journal,
        Func<ClawRecoveryState?, TSnapshot?> readSnapshot,
        Func<TSnapshot, CancellationToken, ValueTask<bool>> restoreAsync,
        string recoveryNoun,
        string budgetLabel,
        string unverifiedMessage,
        CancellationToken cancellationToken)
        where TSnapshot : class
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ClawServiceState.Faulted, ReconciliationBlockReason);
        }

        if (State is not ClawServiceState.Owned || !journal.HasUnrestoredMutation(ServiceId))
        {
            return Set(ClawServiceState.Idle);
        }

        var restoreSnapshot = readSnapshot(journal.OriginalStateFor(ServiceId));
        if (restoreSnapshot is null)
        {
            return Set(ClawServiceState.Faulted, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                $"The {recoveryNoun} recovery record did not contain its pre-mutation snapshot."));
        }

        ClawWriteBudget.Require(context.Deadline, budgetLabel);
        bool restored;
        try
        {
            restored = await restoreAsync(restoreSnapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await journal.CompleteServiceRestorationAsync(
                ServiceId,
                ClawRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await journal.CompleteServiceRestorationAsync(
            ServiceId,
            restored ? ClawRecoveryStatus.RestoredVerified : ClawRecoveryStatus.RestoredUnverified,
            cancellationToken).ConfigureAwait(false);
        return restored
            ? Set(ClawServiceState.Idle)
            : Set(ClawServiceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                unverifiedMessage));
    }
}

/// <summary>A cycle service with a live source that stops for a suspend and is reacquired on resume.</summary>
internal abstract class ClawSuspendableService(string serviceId) : ClawCycleService(serviceId)
{
    public abstract ValueTask<ClawServiceResult> SuspendAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken);
}

internal sealed class OemEventService(
    IMsiOemEventSource source,
    IPluginHostAdapter host,
    OemButtonLatch oemButtons) : ClawSuspendableService(ServiceIds.OemEvents)
{
    /// <summary>
    ///     How close a WMI QS event and a firmware QS chord must be to count as one press. Where
    ///     MSI_Event is installed the firmware may send both for the same press.
    /// </summary>
    private static readonly TimeSpan SamePressWindow = TimeSpan.FromMilliseconds(500);

    private readonly IPluginHostAdapter _host = host ?? throw new ArgumentNullException(nameof(host));

    private readonly OemButtonLatch _oemButtons =
        oemButtons ?? throw new ArgumentNullException(nameof(oemButtons));

    private readonly IMsiOemEventSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private long _lastQuickSettingsTicks;

    public override async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        var started = await _source.StartAsync(PublishAsync, cancellationToken).ConfigureAwait(false);
        return started
            ? Set(ClawServiceState.Owned)
            : Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The MSI_Event provider was unavailable."));
    }

    public override ValueTask<ClawServiceResult> SuspendAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        return ReleaseAsync(context, cancellationToken);
    }

    public override async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Idle);
    }

    /// <summary>
    ///     A QS press from the firmware's keyboard chord, called from the keyboard hook. Queued so the
    ///     hook returns at once.
    /// </summary>
    public void RaiseChord(FirmwareChord chord)
    {
        var press = chord is FirmwareChord.QuickSettingsLong ? OemPressKind.Long : OemPressKind.Short;
        var timestamp = DateTimeOffset.UtcNow;
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => _ = state.Service.PublishPressAsync("oem2", state.Press, state.Timestamp, "chord"),
            (Service: this, Press: press, Timestamp: timestamp),
            false);
    }

    private ValueTask PublishAsync(byte code, DateTimeOffset timestamp)
    {
        // HC maps 0x29 and 0x58. 0x2A, a long QS press, predates the provenance record; HC ignores
        // the code, so a firmware that never sends it loses nothing.
        (string controlId, OemPressKind press)? mapped = code switch
        {
            0x29 => ("oem1", OemPressKind.Short),
            0x58 => ("oem2", OemPressKind.Short),
            0x2A => ("oem2", OemPressKind.Long),
            _ => null
        };
        if (mapped is null)
        {
            return ValueTask.CompletedTask;
        }

        return PublishPressAsync(mapped.Value.controlId, mapped.Value.press, timestamp, $"msi-event-{code:X2}");
    }

    private ValueTask PublishPressAsync(string controlId, OemPressKind press, DateTimeOffset timestamp, string origin)
    {
        if (controlId == "oem2")
        {
            var previous = Interlocked.Exchange(ref _lastQuickSettingsTicks, timestamp.UtcTicks);
            if (timestamp.UtcTicks - previous < SamePressWindow.Ticks)
            {
                return ValueTask.CompletedTask;
            }
        }

        (string controlId, OemPressKind press)? mapped = (controlId, press);

        // The buttons the device is printed for: the left one is the virtual target's Steam button,
        // the right one its Quick Access button. Latched into the controller sample so Steam sees
        // its own controller press them, rather than WSGM acting on the user's behalf. A long press
        // on OEM2 is still only that button — the duration belongs to whatever reads it.
        var button = mapped.Value.controlId switch
        {
            "oem1" => CanonicalButtons.Guide,
            "oem2" => CanonicalButtons.QuickAccess,
            _ => CanonicalButtons.None
        };
        if (button != CanonicalButtons.None)
        {
            _oemButtons.Press(button, timestamp);
        }

        return _host.PublishOemEventAsync(
            new OemControlEvent(
                mapped.Value.controlId,
                mapped.Value.press,
                timestamp,
                $"{origin}-{timestamp.UtcTicks}"),
            CancellationToken.None);
    }
}

internal sealed class PowerService(
    ClawPowerCapability capability,
    ClawRecoveryJournal journal) : ClawCycleService(ServiceIds.Power)
{
    private readonly ClawPowerCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    public PowerPair? LastObserved { get; private set; }

    public override async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ClawServiceState.Faulted, ReconciliationBlockReason);
        }

        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return Set(ClawServiceState.Passive, FirmwareReason(identity));
        }

        // HC writes without reading, so a read the firmware refuses leaves the limits unknown until
        // the first write rather than keeping the service from starting.
        LastObserved = await ClawObservation.TryAsync(_capability.ReadAsync, "power", cancellationToken)
            .ConfigureAwait(false);
        return Set(ClawServiceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        var read = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
        await _capability.ReassertAsync(read, cancellationToken).ConfigureAwait(false);
        LastObserved = _capability.Observe(read);
    }

    public override ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        return RestoreJournalledAsync(
            context,
            _journal,
            state => ClawRecoveryValues.TryPower(state, out var snapshot) ? snapshot : null,
            _capability.RestoreAsync,
            "power",
            "journalled power restoration",
            "The captured power pair or scenario could not be verified after restoration.",
            cancellationToken);
    }

    private static CapabilityReason FirmwareReason(ClawIdentityState identity)
    {
        return identity.ExactMachineMatch
            ? new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "The MSI_ACPI provider did not answer.")
            : new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "The Claw model identity no longer matches.");
    }
}

internal sealed class ChargeLimitService(
    ClawChargeLimitCapability capability) : ClawCycleService(ServiceIds.ChargeLimit)
{
    private readonly ClawChargeLimitCapability _capability = capability
                                                             ?? throw new ArgumentNullException(nameof(capability));

    public ChargeLimitState? LastObserved { get; private set; }

    public override async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return Set(ClawServiceState.Passive, FirmwareReason(identity));
        }

        LastObserved = await ClawObservation.TryAsync(_capability.ReadAsync, "charge-limit", cancellationToken)
            .ConfigureAwait(false);
        return Set(ClawServiceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        LastObserved = _capability.Observe(await _capability.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    public override ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastObserved = null;
        return ValueTask.FromResult(Set(ClawServiceState.Idle));
    }

    private static CapabilityReason FirmwareReason(ClawIdentityState identity)
    {
        return identity.ExactMachineMatch
            ? new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "The MSI_ACPI provider did not answer.")
            : new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "The Claw model identity no longer matches.");
    }
}

internal sealed class FanService(
    ClawFanCapability capability,
    ClawRecoveryJournal journal) : ClawCycleService(ServiceIds.Fans)
{
    private readonly ClawFanCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    public FanSnapshot? LastObserved { get; private set; }

    public override async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ClawServiceState.Faulted, ReconciliationBlockReason);
        }

        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                identity.ExactMachineMatch
                    ? CapabilityReasonCode.FirmwareNotVerified
                    : CapabilityReasonCode.GenerationChanged,
                "The Claw model or its MSI_ACPI provider was not available."));
        }

        LastObserved = await ClawObservation.TryAsync(_capability.ReadSnapshotAsync, "fans", cancellationToken)
            .ConfigureAwait(false);
        return Set(ClawServiceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        LastObserved = _capability.Observe(
            await _capability.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false));
    }

    public override ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        return RestoreJournalledAsync(
            context,
            _journal,
            state => ClawRecoveryValues.TryFans(state, out var snapshot) ? snapshot : null,
            _capability.RestoreAsync,
            "fan",
            "journalled fan restoration",
            "The captured left/right fan tables or flags could not be verified after restoration.",
            cancellationToken);
    }
}

internal sealed class TelemetryService(
    ClawFanCapability capability) : ClawCycleService(ServiceIds.Telemetry)
{
    private readonly ClawFanCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    public FanTelemetry? LastTelemetry { get; private set; }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        LastTelemetry = await _capability.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "MSI telemetry requires the MSI_ACPI provider."));
        }

        LastTelemetry = await _capability.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Owned);
    }

    public override ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastTelemetry = null;
        return ValueTask.FromResult(Set(ClawServiceState.Idle));
    }
}

internal sealed class LightingService(
    IClawMcuTransport transport,
    ClawLightingCapability capability) : ClawCycleService(ServiceIds.Lighting)
{
    private readonly ClawLightingCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    private readonly IClawMcuTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public LightingState? LastObserved { get; private set; }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch)
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Exact device identity no longer matches the Claw implementation."));
        }

        if (!await _transport.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The reviewed MCU HID collection was unavailable."));
        }

        // HC writes the RGB profile without reading it. The read here only seeds what WSGM shows and
        // which unknown bytes a write keeps; a profile the MCU does not return in the known shape
        // leaves the state unknown until the first write, and lighting is offered either way.
        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (LastObserved is null)
        {
            PluginTrace.Info(
                "lighting",
                $"The profile at 0x{_capability.ProfileAddress:X4} did not read back in the known shape on "
                + $"MCU firmware {identity.Snapshot.McuFirmwareVersion ?? "<unknown>"}; writing as HC does.");
        }

        return Set(ClawServiceState.Owned);
    }

    public override ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // RGB profile writes are intentionally persistent user choices. Deactivation never rewrites
        // the captured profile and therefore cannot silently undo the requested setting.
        return ValueTask.FromResult(Set(ClawServiceState.Idle));
    }
}

internal sealed class MotionService(IClawMotionSource source, ClawModel model)
    : ClawSuspendableService(ServiceIds.Motion)
{
    /// <summary>How long a sensor reading may still be attached to a controller sample.</summary>
    /// <remarks>
    ///     The physical gyrometer is configured for a 10 ms report interval while the controller reader
    ///     runs at about 125 Hz, so the same reading legitimately rides an adjacent frame. Five sensor
    ///     periods cover ordinary scheduler jitter without replaying a non-zero value after the device
    ///     or its Intel transport stops producing fresh hardware reports.
    /// </remarks>
    internal static readonly TimeSpan MaximumMotionAge = TimeSpan.FromMilliseconds(50);

    /// <summary>How long staleness must persist before it is worth a line.</summary>
    /// <remarks>
    ///     Crossing <see cref="MaximumMotionAge" /> is not news: measured Intel transport jitter puts a
    ///     dense cluster of readings at 51-59 ms, just past the cap, so reporting each crossing produced
    ///     two alternating lines about 1.3 times a second — 7,619 lines and 40% of one day's log. The
    ///     decay those crossings cause is a couple of milliseconds inside a 52 ms interval and is not
    ///     what anyone is being told about. A pause worth reading about outlasts the jitter by an order
    ///     of magnitude.
    /// </remarks>
    internal static readonly TimeSpan StaleReportDelay = TimeSpan.FromMilliseconds(500);

    private readonly Lock _latestGate = new();
    private readonly GyroFrameResampler _resampler = new(MaximumMotionAge);
    private readonly IClawMotionSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private MotionSample? _latest;
    private int _staleReported;

    /// <summary>The motion to attach to the controller sample being published now.</summary>
    /// <param name="now">Current time, from the caller's clock.</param>
    /// <returns>
    ///     The last reading with its angular velocity replaced by the frame-average since the previous
    ///     call, or null before the first reading arrives.
    /// </returns>
    /// <remarks>
    ///     The sensor updates at 100 Hz under a ~125 Hz controller reader, so raw values ride frames
    ///     unevenly in a repeating beat that Steam integrates as jagged angular steps. The resampled
    ///     average preserves the exact integrated angle per frame and decays to zero when the sensor
    ///     goes quiet, which is also what keeps a still device from ever reading as freefall.
    /// </remarks>
    public MotionSample? Current(DateTimeOffset now)
    {
        MotionSample sample;
        lock (_latestGate)
        {
            if (_latest is not { } latest)
            {
                return null;
            }

            sample = latest;
        }

        // A source that supplies no timestamp cannot be aged; it is passed through as before.
        if (sample.SensorTimestamp is not { } stamp)
        {
            return sample;
        }

        // Crossing MaximumMotionAge is not itself news, and is not tested for here: that threshold
        // governs the resampler's quiet cap, and the held rest it produces is correct — the average
        // decays to zero while the last measured acceleration keeps Steam's fusion anchored, so an
        // Intel transport pause becomes neither continuous rotation nor freefall. Only a pause that
        // outlasts ordinary jitter tells a reader something. This runs on the ~125 Hz controller
        // reader, so it reports once per stretch at most.
        if (now - stamp >= StaleReportDelay && Interlocked.Exchange(ref _staleReported, 1) == 0)
        {
            PluginTrace.Change(
                "motion",
                "freshness",
                $"Gyroscope reports stopped for over {StaleReportDelay.TotalMilliseconds:F0} ms; "
                + "holding rest (decayed angular velocity, last measured acceleration) until they resume.");
        }

        var average = _resampler.FrameAverage(now);
        return sample with
        {
            GyroX = average.X,
            GyroY = average.Y,
            GyroZ = average.Z
        };
    }

    public override async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        lock (_latestGate)
        {
            _latest = null;
        }

        Interlocked.Exchange(ref _staleReported, 0);
        _resampler.Reset();
        var started = await _source.StartAsync(
            model,
            sample =>
            {
                lock (_latestGate)
                {
                    _latest = sample;
                }

                if (sample.SensorTimestamp is { } stamp)
                {
                    _resampler.OnReading(
                        new Vector3(sample.GyroX, sample.GyroY, sample.GyroZ),
                        stamp);
                }

                // A fresh reading ends the quiet stretch and re-arms its one-shot report. The
                // resume line is owed only where the pause was actually reported, so a crossing
                // too brief to mention stays unmentioned at both ends.
                if (Interlocked.Exchange(ref _staleReported, 0) != 0)
                {
                    PluginTrace.Change("motion", "freshness", "Gyroscope reports resumed.");
                }
            },
            cancellationToken).ConfigureAwait(false);
        return started
            ? Set(ClawServiceState.Owned)
            : Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The Intel ISS physical gyrometer or accelerometer was unavailable; no synthetic fallback exists."));
    }

    public override ValueTask<ClawServiceResult> SuspendAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        return ReleaseAsync(context, cancellationToken);
    }

    public override async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_latestGate)
        {
            _latest = null;
        }

        _resampler.Reset();
        return Set(ClawServiceState.Idle);
    }
}

internal sealed class ControllerService(
    IClawMcuTransport mcu,
    IClawControllerSource source,
    MotionService motion,
    IPluginHostAdapter host,
    ClawRecoveryJournal journal,
    ClawModel model) : ClawSuspendableService(ServiceIds.Controller)
{
    /// <summary>
    ///     The mode the controller goes back to on release: XInput, as HC's <c>Close</c> and app exit
    ///     both switch it, whatever mode it was found in.
    /// </summary>
    private const ClawControllerMode ReleaseMode = ClawControllerMode.XInput;

    /// <summary>
    ///     What the Claw's MCU can actually do with output.
    /// </summary>
    /// <remarks>
    ///     Two motors and no trigger haptics: the rumble report
    ///     (<see cref="ClawControllerCodec.EncodeRumble" />) carries one weak and one strong byte and
    ///     nothing else. WSGM's output router paces frames to the declared rate and never holds a stop
    ///     back, so this service writes every frame it is given.
    /// </remarks>
    private static readonly HapticCapabilities OutputCapabilities = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        LeftTrigger = OutputChannelSupport.Unsupported,
        RightTrigger = OutputChannelSupport.Unsupported,
        MaxFramesPerSecond = 250,
        // ERM motors: LRA-grade haptic ticks must be floored and stretched by the host to be
        // perceptible at all. Device-measured with the attended A-button sweep (2026-09-02):
        // 30 ms ticks are felt down to 56/255 and vanish at 48; full-strength pulses stay
        // reliable to about 10 ms (below that the sleep granularity dominates); continuous
        // rumble is felt down to 24/255, which is why the floor applies to bounded events only.
        MinimumStartIntensity = 56f / 255f,
        MinimumPulse = TimeSpan.FromMilliseconds(10)
    };

    /// <summary>
    ///     The Claw A1M's output as HC drives it: each motor on at one level or off, sampled every
    ///     100 ms by <c>DClawController</c>'s rumble thread. Any nonzero intensity is full, so there is
    ///     no start floor, and a pulse shorter than one sample would be lost.
    /// </summary>
    private static readonly HapticCapabilities BinaryOutputCapabilities = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        LeftTrigger = OutputChannelSupport.Unsupported,
        RightTrigger = OutputChannelSupport.Unsupported,
        MaxFramesPerSecond = 10,
        MinimumPulse = ClawModels.BinaryRumbleInterval
    };

    /// <summary>HC's <c>GetM12</c> DirectInput mapping payload after the address: map the paddle as a button.</summary>
    private static readonly byte[] PaddleDirectInputMapping = [0x01, 0x00];

    private readonly Lock _hapticGate = new();
    private readonly IPluginHostAdapter _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly IClawMcuTransport _mcu = mcu ?? throw new ArgumentNullException(nameof(mcu));
    private readonly MotionService _motion = motion ?? throw new ArgumentNullException(nameof(motion));
    private readonly SemaphoreSlim _outputSerializer = new(1, 1);
    private readonly DeviceReconnect _reconnect = new();
    private readonly IClawControllerSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private ClawCycleContext? _context;
    private byte _lastStrong;
    private byte _lastWeak;
    private ControllerTopology? _original;
    private CanonicalButtons _rearButtons;

    /// <summary>Scales HC's <c>Thread.Sleep</c> spacing around the paddle writes; tests set it to zero.</summary>
    internal static double McuDelayScale { get; set; } = 1;

    public bool Enabled { get; set; }

    private ControllerTopology? CurrentTopology { get; set; }

    public override async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.ResourceReleased,
                "Controller management is disabled."));
        }

        _context = context;
        if (ReconciliationBlockReason is not null)
        {
            return Set(ClawServiceState.Faulted, ReconciliationBlockReason);
        }

        // Exact machine identity is the only gate. The MCU revision is deliberately not one: the
        // mode switch and the hide list are not addressed writes, and gating them on the revision
        // took the controller away from every unit MSI updated to 0230.
        var identity = context.Identity;
        if (!identity.ExactMachineMatch)
        {
            _host.Trace(
                DeviceTraceLevel.Warn,
                "controller",
                "acquire refused at the identity gate: the Claw model identity no longer matches.");
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Exact device identity no longer matches the Claw implementation."));
        }

        var observed = await _source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (observed is null)
        {
            // After a wake the pad comes back a few seconds late. Nothing is final here: it is taken as
            // soon as it is there, as HC takes it on Device_Inserted. Giving up on the first look left
            // the controller dead after a wake.
            _host.Trace(DeviceTraceLevel.Warn, "controller", "no Claw controller yet; waiting for it.");
            _reconnect.Start(ReacquireAsync, ex => _host.Trace(DeviceTraceLevel.Debug, "controller",
                ClawDiagnosticText.FromException("taking the pad again failed", ex)));
            return Set(ClawServiceState.Degraded, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The Claw controller is not present yet."));
        }

        // Discovery is where the answer to "why didn't it switch to DirectInput?" lives, and it was
        // invisible: the mode, the product id and the endpoint list are all decided here and none
        // of them survived into any reason string. Traced unconditionally, before the gates below
        // get a chance to turn all of it into one sentence about a prerequisite.
        _host.Trace(
            observed is null ? DeviceTraceLevel.Warn : DeviceTraceLevel.Info,
            "controller",
            observed is null
                ? "discovery found no Claw controller topology."
                : $"discovered mode={observed.Mode}, product=0x{observed.ProductId}, "
                  + $"location='{observed.PhysicalLocation}', "
                  + $"physicalDevices={observed.PhysicalDevices.Count}, "
                  + $"endpoints=[{observed.ObservedEndpoints}]");
        _original ??= observed;
        if (_original is null || observed is null
                              || string.IsNullOrWhiteSpace(_original.PhysicalLocation)
                              || !HidDevices.SamePhysicalLocation(
                                  observed.PhysicalLocation,
                                  _original.PhysicalLocation))
        {
            _host.Trace(
                DeviceTraceLevel.Warn,
                "controller",
                "acquire refused: composite USB location did not match the one first observed. "
                + $"original='{_original?.PhysicalLocation}', "
                + $"observed='{observed?.PhysicalLocation}'.");
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The physical controller or its composite USB location was unavailable."));
        }

        CurrentTopology = observed;

        // HC's Open and every mode change write the M1/M2 DirectInput mapping and commit it to ROM
        // before switching, so the paddles report as DirectInput buttons 15 and 16 whatever MSI
        // Center stored for them.
        await ConfigurePaddlesAsync(context, cancellationToken).ConfigureAwait(false);
        if (CurrentTopology.Mode is not ClawControllerMode.DirectInput)
        {
            ClawWriteBudget.Require(context.Deadline, "controller mode acquisition");
            _ = await _journal.BeginAsync(
                ServiceId,
                CapabilityIds.Controller,
                ClawFirmwareIdentities.Mcu,
                ClawRecoveryValues.ControllerMode(ReleaseMode),
                cancellationToken).ConfigureAwait(false);
            _host.Trace(
                DeviceTraceLevel.Info,
                "controller",
                $"switching MCU mode {CurrentTopology.Mode} -> DirectInput at '{_original.PhysicalLocation}'.");
            try
            {
                CurrentTopology = await _mcu.SwitchModeAsync(
                    ClawControllerMode.DirectInput,
                    _original.PhysicalLocation,
                    context.Deadline,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _host.Trace(
                    DeviceTraceLevel.Error,
                    "controller",
                    $"mode switch to DirectInput failed: {ex.GetType().Name}: {ex.Message}");
                await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
                throw;
            }

            // The switch reports success by returning a topology, but the topology is what the
            // hardware actually settled into. Those differed on the reference unit, and nothing
            // said so.
            _host.Trace(
                CurrentTopology.Mode is ClawControllerMode.DirectInput
                    ? DeviceTraceLevel.Info
                    : DeviceTraceLevel.Warn,
                "controller",
                $"mode switch settled at {CurrentTopology.Mode}, product=0x{CurrentTopology.ProductId}, "
                + $"physicalDevices={CurrentTopology.PhysicalDevices.Count}, "
                + $"endpoints=[{CurrentTopology.ObservedEndpoints}]");
        }
        else
        {
            _host.Trace(
                DeviceTraceLevel.Info,
                "controller",
                "controller already in DirectInput; no mode switch needed.");
        }

        if (CurrentTopology.PhysicalDevices.Count == 0)
        {
            // Captured before the restore, which clears _current: the whole point of this reason is
            // to say what was observed, and reading it afterwards is reading nothing.
            var detail = "No exact DirectInput physical interface identity was available for "
                         + $"handoff. Mode={CurrentTopology.Mode}, product={CurrentTopology.ProductId}, "
                         + $"endpoints=[{CurrentTopology.ObservedEndpoints}]";
            _host.Trace(DeviceTraceLevel.Warn, "controller", detail);
            await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                detail));
        }

        try
        {
            await _source.StartAsync(
                model,
                PublishControllerSampleAsync,
                ReportControllerReaderFault,
                cancellationToken).ConfigureAwait(false);

            await _host.PublishPhysicalDevicesAsync(
                CurrentTopology.PhysicalDevices,
                model.BinaryRumble ? BinaryOutputCapabilities : OutputCapabilities,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Trace(
                DeviceTraceLevel.Error,
                "controller",
                $"controller acquisition failed before physical-device handoff completed: "
                + $"{ex.GetType().Name}: {ex.Message}");
            await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
            throw;
        }

        _host.Trace(
            DeviceTraceLevel.Info,
            "controller",
            $"owned: published {CurrentTopology.PhysicalDevices.Count} physical identities for hiding, "
            + "haptics=True.");
        return Set(ClawServiceState.Owned);
    }

    /// <summary>The DirectInput collection stopped reading: the pad went away, which is never a device fault.</summary>
    /// <remarks>
    ///     HC's read loop treats any failure as <c>Device_Removed</c> and takes the pad again on
    ///     <c>Device_Inserted</c>. Faulting here took fans, TDP and the OEM buttons down with the pad, for
    ///     a drop that every sleep produces.
    /// </remarks>
    private void ReportControllerReaderFault(Exception exception)
    {
        if (State is not ClawServiceState.Owned)
        {
            return;
        }

        var detail = ClawDiagnosticText.FromException("The controller reader stopped", exception);
        _host.Trace(DeviceTraceLevel.Warn, "controller", detail + "; waiting for the pad to come back.");
        _ = Set(ClawServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
        if (_context is { } context)
        {
            _ = NeutralizeReaderAndReconnectAsync(context);
        }
    }

    private async Task NeutralizeReaderAndReconnectAsync(ClawCycleContext context)
    {
        try
        {
            if (_context == context && State is ClawServiceState.Degraded)
            {
                _rearButtons = CanonicalButtons.None;
                await _host.PublishControllerSampleAsync(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Failure("controller", "Reader-loss neutral publication failed", ex);
        }
        finally
        {
            if (_context == context && State is ClawServiceState.Degraded)
            {
                _reconnect.Start(ReacquireAsync, ex => _host.Trace(DeviceTraceLevel.Debug, "controller",
                    ClawDiagnosticText.FromException("taking the pad again failed", ex)));
            }
        }
    }

    /// <summary>Takes the pad again once it answers, the way HC's <c>Device_Inserted</c> reopens it.</summary>
    private async ValueTask<bool> ReacquireAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || _context is not { } context)
        {
            return true;
        }

        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        var result = await AcquireAsync(context with { Deadline = Deadline.After(TimeSpan.FromSeconds(12)) },
            cancellationToken).ConfigureAwait(false);
        if (result.State is not ClawServiceState.Owned)
        {
            return false;
        }

        _host.Trace(DeviceTraceLevel.Info, "controller", "the pad is back.");
        return true;
    }

    public override async ValueTask<ClawServiceResult> SuspendAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await StopOutputAndAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Idle);
    }

    public override async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await ReleaseControllerAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Idle);
    }

    /// <summary>Stops reading and puts the controller back in XInput mode, as HC's <c>Close</c> does.</summary>
    /// <param name="deadline">Bounds the mode switch.</param>
    /// <param name="cancellationToken">Cancels waiting for the reader and the switch.</param>
    /// <remarks>
    ///     Best effort: every step runs, a failed one is traced and recorded in the recovery journal for
    ///     the next start, and the service ends idle. Nothing here is a reason to keep the device down.
    /// </remarks>
    public async ValueTask ReleaseControllerAsync(
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        _ = Set(ClawServiceState.Releasing);
        await StopOutputAndAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        if (_original is null || CurrentTopology is null)
        {
            CurrentTopology = null;
            if (_journal.HasUnrestoredMutation(ServiceId))
            {
                _host.Trace(DeviceTraceLevel.Warn, "controller",
                    "the controller disappeared before its original mode could be put back.");
                await _journal.CompleteServiceRestorationAsync(
                    ServiceId,
                    ClawRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
            }

            _ = Set(ClawServiceState.Idle);
            return;
        }

        var status = ClawRecoveryStatus.RestoredVerified;
        if (CurrentTopology.Mode != ReleaseMode)
        {
            try
            {
                ClawWriteBudget.Require(deadline, "controller mode restoration");
                var restored = await _mcu.SwitchModeAsync(
                    ReleaseMode,
                    _original.PhysicalLocation,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                if (restored.Mode != ReleaseMode
                    || !string.Equals(restored.PhysicalLocation, _original.PhysicalLocation,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _host.Trace(DeviceTraceLevel.Warn, "controller",
                        $"mode switch back settled at {restored.Mode} at '{restored.PhysicalLocation}'.");
                    status = ClawRecoveryStatus.RestoredUnverified;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _host.Trace(DeviceTraceLevel.Warn, "controller",
                    $"the original controller mode could not be put back: {ex.GetType().Name}: {ex.Message}");
                status = ClawRecoveryStatus.RestoreFailed;
            }
        }

        CurrentTopology = null;
        _rearButtons = CanonicalButtons.None;
        await _journal.CompleteServiceRestorationAsync(ServiceId, status, CancellationToken.None)
            .ConfigureAwait(false);
        _ = Set(ClawServiceState.Idle);
    }

    public async ValueTask ApplyHapticsAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
    {
        await _outputSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not ClawServiceState.Owned)
            {
                return;
            }

            var weak = ToByte(frame.HighFrequency);
            var strong = ToByte(frame.LowFrequency);
            if (model.BinaryRumble)
            {
                // HC's DClawController drives the Claw A1M's motors on or off at one level.
                weak = weak == 0 ? (byte)0 : ClawModels.BinaryRumbleLevel;
                strong = strong == 0 ? (byte)0 : ClawModels.BinaryRumbleLevel;
            }

            lock (_hapticGate)
            {
                if (weak == _lastWeak && strong == _lastStrong)
                {
                    return;
                }
            }

            await _source.WriteRumbleAsync(weak, strong, cancellationToken).ConfigureAwait(false);
            lock (_hapticGate)
            {
                _lastWeak = weak;
                _lastStrong = strong;
            }
        }
        finally
        {
            _outputSerializer.Release();
        }
    }

    /// <summary>HC's <c>ApplyM12Configuration</c>: M1, M2, then <c>SyncToROM</c>, with its sleeps.</summary>
    private async ValueTask ConfigurePaddlesAsync(ClawCycleContext context, CancellationToken cancellationToken)
    {
        var layout = ClawModels.McuLayout(context.Identity.Snapshot.McuFirmwareVersion);

        // HC's sleeps add almost two seconds; the mode switch after them needs its own budget.
        if (context.Deadline.Remaining < TimeSpan.FromSeconds(6))
        {
            _host.Trace(DeviceTraceLevel.Info, "controller",
                "paddle mapping skipped: the acquisition deadline leaves no room for HC's write spacing.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300 * McuDelayScale), cancellationToken).ConfigureAwait(false);
            await _mcu.WriteProfileAsync(layout.M1DirectInput, PaddleDirectInputMapping, cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(500 * McuDelayScale), cancellationToken).ConfigureAwait(false);
            await _mcu.WriteProfileAsync(layout.M2DirectInput, PaddleDirectInputMapping, cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(500 * McuDelayScale), cancellationToken).ConfigureAwait(false);
            await _mcu.SyncToRomAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(500 * McuDelayScale), cancellationToken).ConfigureAwait(false);
            _host.Trace(DeviceTraceLevel.Info, "controller",
                $"paddle mapping written at 0x{layout.M1DirectInput:X4}/0x{layout.M2DirectInput:X4} and synced to ROM.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // HC does not check these writes either; the controller is still taken.
            _host.Trace(DeviceTraceLevel.Warn, "controller",
                $"paddle mapping write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async ValueTask StopOutputAndAcquisitionAsync(
        CancellationToken cancellationToken)
    {
        await _reconnect.StopAsync().ConfigureAwait(false);
        var ownsOutput = false;
        try
        {
            await _outputSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
            ownsOutput = true;
            try
            {
                await _source.WriteRumbleAsync(0, 0, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The device may already be gone; acquisition cleanup and mode restoration continue.
                _host.Trace(
                    DeviceTraceLevel.Warn,
                    "controller",
                    $"zero-rumble write during release failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // StopAsync below closes the stream to abort the in-flight output at the lifecycle deadline.
        }
        finally
        {
            if (ownsOutput)
            {
                _outputSerializer.Release();
            }
        }

        try
        {
            await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The physical source may have vanished. Continue to the mode restoration, which is the
            // cleanup step that keeps external input usable.
            _host.Trace(DeviceTraceLevel.Warn, "controller",
                $"the controller source did not stop cleanly: {ex.GetType().Name}: {ex.Message}");
        }

        lock (_hapticGate)
        {
            _lastWeak = 0;
            _lastStrong = 0;
        }
    }

    private async ValueTask PublishControllerSampleAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        var current = sample.Buttons
                      & (CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2);
        var changed = current ^ _rearButtons;
        if ((changed & CanonicalButtons.RearPaddle1) != 0)
        {
            await PublishRearEventAsync(
                "oem3",
                (current & CanonicalButtons.RearPaddle1) != 0,
                sample,
                cancellationToken).ConfigureAwait(false);
        }

        if ((changed & CanonicalButtons.RearPaddle2) != 0)
        {
            await PublishRearEventAsync(
                "oem4",
                (current & CanonicalButtons.RearPaddle2) != 0,
                sample,
                cancellationToken).ConfigureAwait(false);
        }

        _rearButtons = current;
        // Aged rather than taken blindly. If the physical sensor counter stops advancing while the
        // controller keeps reporting, the last reading must not replay a non-zero angular velocity
        // through the virtual Deck indefinitely.
        await _host.PublishControllerSampleAsync(
            sample with { Motion = _motion.Current(sample.Timestamp) },
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask PublishRearEventAsync(
        string controlId,
        bool pressed,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        return _host.PublishOemEventAsync(
            new OemControlEvent(
                controlId,
                OemPressKind.Short,
                sample.Timestamp,
                $"claw-hid-{controlId}-{sample.Timestamp.UtcTicks}",
                pressed ? OemControlEdge.Pressed : OemControlEdge.Released),
            cancellationToken);
    }

    private async ValueTask RestoreAfterFailedAcquireAsync(Deadline deadline)
    {
        try
        {
            var observed = await _source.DiscoverAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (observed is null)
            {
                CurrentTopology = null;
                CapabilityReason reason = new(
                    CapabilityReasonCode.TransportFaulted,
                    "Controller topology vanished during acquisition rollback.");
                Fault(reason);
                await _journal.CompleteServiceRestorationAsync(
                    ServiceId,
                    ClawRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            CurrentTopology = observed;
            await ReleaseControllerAsync(deadline, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Fault(new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "Controller acquisition rollback failed."));
            await _journal.CompleteServiceRestorationAsync(
                ServiceId,
                ClawRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static byte ToByte(float value)
    {
        return checked((byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue));
    }
}

internal sealed class ChordSuppressorService(
    IFirmwareChordSuppressor suppressor,
    OemEventService oemEvents,
    IPluginHostAdapter host) : ClawSuspendableService(ServiceIds.ChordSuppressor)
{
    private readonly IPluginHostAdapter _host = host ?? throw new ArgumentNullException(nameof(host));

    private readonly OemEventService _oemEvents = oemEvents
                                                  ?? throw new ArgumentNullException(nameof(oemEvents));

    private readonly IFirmwareChordSuppressor _suppressor = suppressor
                                                            ?? throw new ArgumentNullException(nameof(suppressor));

    public override async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        // HC's chords work without MSI_Event, and are the only path to QS where it is missing.
        var started = await _suppressor.StartAsync(ReportFault, _oemEvents.RaiseChord, cancellationToken)
            .ConfigureAwait(false);
        return started
            ? Set(ClawServiceState.Owned)
            : Set(ClawServiceState.Degraded, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The bounded low-level keyboard hook could not be installed."));
    }

    /// <summary>The keyboard hook stopped: the chords are gone until the next start, nothing else is.</summary>
    private void ReportFault(Exception exception)
    {
        var detail = ClawDiagnosticText.FromException("The firmware chord suppressor stopped", exception);
        _host.Trace(DeviceTraceLevel.Warn, "chords", detail);
        _ = Set(ClawServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
    }

    public override ValueTask<ClawServiceResult> SuspendAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        return ReleaseAsync(context, cancellationToken);
    }

    public override async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await _suppressor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Idle);
    }
}

/// <summary>Owns the panel's variable-refresh state for one cycle.</summary>
/// <remarks>
///     A service of its own rather than a corner of the lighting or power services, because it is the
///     only capability driven by the GPU driver rather than by MSI's firmware: it has no firmware
///     identity to verify, and it must be restored on release even when every WMI and MCU path failed.
/// </remarks>
internal sealed class DisplayService : ClawServiceStatus, IDisposable
{
    private readonly ArcSyncTransport _arcSync = new();

    // The second thing this device's GPU driver owns, reached through the same library. It lives
    // beside variable refresh for the reason the service exists at all: neither has a firmware
    // identity to verify, and both have to be restorable when every WMI and MCU path failed.
    private readonly Intel3dFeatureTransport _endurance = new();

    // The driver's shared-memory split, which is a driver setting rather than a library call and so
    // needs no handle and no open. It belongs to this service anyway: it is the same GPU driver, it
    // has no firmware identity to verify, and grouping it anywhere else would put a graphics
    // setting behind an MSI WMI or MCU gate that has nothing to do with it.
    private readonly IntelGraphicsMemoryTransport _sharedMemory = new();
    private bool _disposed;
    private EnduranceGamingState? _enduranceOnAcquire;
    private bool? _shaderOnAcquire;

    /// <summary>Creates the service without touching the driver.</summary>
    public DisplayService()
        : base(ServiceIds.Display)
    {
    }

    /// <summary>Whether a variable-refresh capable panel answered.</summary>
    public bool IsAvailable => _arcSync.IsAvailable;

    /// <summary>Whether the driver answers for Endurance Gaming on this machine.</summary>
    public bool IsEnduranceGamingAvailable => _enduranceOnAcquire is not null;

    /// <summary>Whether the driver answers for prebuilt shader download on this machine.</summary>
    public bool IsShaderDownloadAvailable => _shaderOnAcquire is not null;

    /// <summary>Whether this machine's graphics driver stores a shared-memory split.</summary>
    public bool IsSharedGpuMemoryAvailable => _sharedMemory.IsAvailable;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _arcSync.Dispose();
        _endurance.Dispose();
        State = ClawServiceState.Idle;
    }

    /// <summary>Opens the driver and selects the panel, capturing the profile to restore later.</summary>
    /// <returns><see langword="true" /> when variable refresh can be driven.</returns>
    public bool TryAcquire()
    {
        var available = _arcSync.TryOpen();

        // Independent of variable refresh: a driver can answer for one and not the other, and a
        // panel without variable refresh must not cost the device its Endurance Gaming row.
        if (_endurance.TryOpen())
        {
            // Each feature is probed on its own: a driver can answer for one and not the other, and
            // one missing feature must not cost the device the rows for the rest.
            _enduranceOnAcquire = _endurance.Read();
            _shaderOnAcquire = _endurance.ReadShaderDownload();
            available |= _enduranceOnAcquire is not null || _shaderOnAcquire is not null;
        }

        available |= _sharedMemory.IsAvailable;

        // Passive rather than Faulted when no capable panel answered: nothing went wrong, the
        // device simply does not have the feature, and Faulted would report a defect that is not one.
        State = available ? ClawServiceState.Owned : ClawServiceState.Passive;
        return available;
    }

    /// <summary>Reads the current Endurance Gaming state, or null when it cannot be read.</summary>
    /// <returns>The driver's current control and mode.</returns>
    public EnduranceGamingState? ReadEnduranceGaming()
    {
        return _endurance.Read();
    }

    /// <summary>Applies an Endurance Gaming control and mode, verifying the result.</summary>
    /// <param name="control">Whether it should be off, on, or left to the driver.</param>
    /// <param name="mode">The frame target it should hold to.</param>
    /// <returns><see langword="true" /> when the driver reports both back.</returns>
    public bool TryWriteEnduranceGaming(EnduranceGamingControl control, EnduranceGamingMode mode)
    {
        return _endurance.TryWrite(control, mode);
    }

    /// <summary>Reads whether the driver downloads prebuilt shaders.</summary>
    /// <returns>The setting, or null when the driver does not offer it.</returns>
    public bool? ReadShaderDownload()
    {
        return _endurance.ReadShaderDownload();
    }

    /// <summary>Turns prebuilt shader download on or off, verifying the result.</summary>
    /// <param name="enabled">Whether the driver should download prebuilt shaders.</param>
    /// <returns><see langword="true" /> when the driver reports the value back.</returns>
    public bool TryWriteShaderDownload(bool enabled)
    {
        return _endurance.TryWriteShaderDownload(enabled);
    }

    /// <summary>Reads the share of system memory the integrated GPU may use.</summary>
    /// <returns>The stored percentage and the size the driver reports, or null when unreadable.</returns>
    public IntelGraphicsMemoryState? ReadSharedGpuMemory()
    {
        return _sharedMemory.Read();
    }

    /// <summary>The frame-presentation modes this driver offers, or null when it says nothing.</summary>
    /// <remarks>
    ///     Asked of IGCL, which answers this honestly even though it will not report or change the
    ///     current one. The value itself lives in the driver's own settings store.
    /// </remarks>
    public uint? ReadSupportedFlipModes()
    {
        return _endurance.ReadSupportedFlipModes();
    }

    /// <summary>Reads the driver's stored frame-presentation mode.</summary>
    /// <returns>Intel's gaming-flip flag value, or null when none is stored.</returns>
    public uint? ReadFlipMode()
    {
        return _sharedMemory.ReadFlipMode();
    }

    /// <summary>Stores a frame-presentation mode, verifying the stored value.</summary>
    /// <param name="mode">Intel's gaming-flip flag value.</param>
    /// <returns><see langword="true" /> when the driver stores the requested value.</returns>
    public bool TryWriteFlipMode(uint mode)
    {
        return _sharedMemory.TryWriteFlipMode(mode);
    }

    /// <summary>Sets the share of system memory the integrated GPU may use.</summary>
    /// <param name="percent">The requested percentage, within the offered range.</param>
    /// <returns><see langword="true" /> when the driver stores the requested value.</returns>
    /// <remarks>The split itself changes at the next restart; only the setting is verified here.</remarks>
    public bool TryWriteSharedGpuMemory(int percent)
    {
        return _sharedMemory.TryWrite(percent);
    }

    /// <summary>Reads the current state, or null when it cannot be read.</summary>
    /// <returns>The panel's variable-refresh state.</returns>
    public ArcSyncState? Read()
    {
        return _arcSync.Read();
    }

    /// <summary>Turns variable refresh on or off, verifying the result.</summary>
    /// <param name="enabled">Whether variable refresh should be active.</param>
    /// <returns><see langword="true" /> when the panel reports the requested state afterwards.</returns>
    public bool TryWrite(bool enabled)
    {
        return _arcSync.TryWrite(enabled);
    }

    /// <summary>Restores the profile captured when the cycle started.</summary>
    /// <returns><see langword="true" /> when nothing was left changed.</returns>
    /// <remarks>
    ///     The shared-memory split is deliberately not restored. It is a persistent user choice like the
    ///     charge limit rather than a resource this service borrowed, it only takes effect at the next
    ///     restart, and putting it back on a normal stop would silently undo what the user asked for.
    /// </remarks>
    public bool Restore()
    {
        var restored = _arcSync.TryRestore();

        // Only what was captured at acquire, and only when it actually moved. Writing the driver's
        // state back over itself on every release would be a device write nobody asked for.
        if (_enduranceOnAcquire is { } captured
            && ReadEnduranceGaming() is { } current
            && current != captured
            && !TryWriteEnduranceGaming(captured.Control, captured.Mode))
        {
            restored = false;
        }

        if (_shaderOnAcquire is { } shader
            && ReadShaderDownload() is { } currentShader
            && currentShader != shader
            && !TryWriteShaderDownload(shader))
        {
            restored = false;
        }

        return restored;
    }
}

/// <summary>A first read that may be refused without keeping a service from starting.</summary>
internal static class ClawObservation
{
    public static async ValueTask<T?> TryAsync<T>(
        Func<CancellationToken, ValueTask<T>> read,
        string scope,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            PluginTrace.Failure(scope, "The initial read failed; the value stays unknown until a write", ex);
            return null;
        }
    }
}

internal static class ServiceIds
{
    public const string OemEvents = "msi-oem-events";
    public const string Power = "msi-power";
    public const string ChargeLimit = "msi-charge-limit";
    public const string Fans = "msi-fans";
    public const string Telemetry = "msi-telemetry";
    public const string Lighting = "claw-lighting";
    public const string Motion = "claw-motion";
    public const string Controller = "physical-controller";
    public const string ChordSuppressor = "firmware-chord-suppressor";
    public const string Display = "claw-display";
}

internal static class ClawFirmwareIdentities
{
    /// <summary>
    ///     Deliberately carries no revision. A controller journal entry only records the mode to put
    ///     back, and that write is valid on any MCU firmware the exact machine ships with; a
    ///     revision here would strand the entry, and with it the controller, after every firmware
    ///     update. Journals written before 2026-09-18 carry <see cref="LegacyMcu" /> instead.
    /// </summary>
    public const string Mcu = "mcu";

    /// <summary>The revision-bound identity older journals recorded; read as <see cref="Mcu" />.</summary>
    public const string LegacyMcu = "mcu:0229";

    /// <summary>True when neither the EC nor the BIOS version was known, so the binding cannot tell two apart.</summary>
    public static bool IsUnknownEc(string identity)
    {
        return identity.StartsWith("ec:unknown;", StringComparison.Ordinal);
    }

    /// <summary>
    ///     True for a power or fan binding, <c>ec:&lt;version&gt;</c> or <c>bios:&lt;version&gt;</c> then
    ///     <c>;msi-acpi:&lt;major.minor&gt;</c>, as <see cref="WindowsClawIdentityReader" /> builds it. The
    ///     reference unit's reads <c>ec:1T52EMS1.109;msi-acpi:8.0</c>, the value every earlier journal carries.
    /// </summary>
    public static bool IsWmi(string identity)
    {
        return (identity.StartsWith("ec:", StringComparison.Ordinal)
                || identity.StartsWith("bios:", StringComparison.Ordinal))
               && identity.Contains(";msi-acpi:", StringComparison.Ordinal);
    }
}
