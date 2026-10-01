using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Msi.Claw;

internal sealed class OemEventService(
    IMsiOemEventSource source,
    IPluginHostAdapter host,
    OemButtonLatch oemButtons) : DeviceService<ClawIdentityState>(ServiceIds.OemEvents)
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

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var started = await _source.StartAsync(PublishAsync, cancellationToken).ConfigureAwait(false);
        return started
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The MSI_Event provider was unavailable."));
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
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
        // HC's keyMapping: 0x29 is OEM1 and 0x58 is OEM2. Every other code is ignored, as in HC.
        var controlId = code switch
        {
            0x29 => "oem1",
            0x58 => "oem2",
            _ => null
        };
        if (controlId is null)
        {
            return ValueTask.CompletedTask;
        }

        return PublishPressAsync(controlId, OemPressKind.Short, timestamp, $"msi-event-{code:X2}");
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
        // on OEM2 is still only that button; the duration belongs to whatever reads it.
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
    ClawRecoveryJournal journal) : ClawJournalledService(ServiceIds.Power)
{
    private readonly ClawPowerCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    public PowerPair? LastObserved { get; private set; }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(DeviceServiceState.Faulted, ReconciliationBlockReason);
        }

        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return Set(DeviceServiceState.Passive, FirmwareReason(identity));
        }

        // HC writes without reading, so a read the firmware refuses leaves the limits unknown until
        // the first write rather than keeping the service from starting.
        LastObserved = await ClawObservation.TryAsync(_capability.ReadAsync, "power", cancellationToken)
            .ConfigureAwait(false);
        return Set(DeviceServiceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        var read = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
        await _capability.ReassertAsync(read, cancellationToken).ConfigureAwait(false);
        LastObserved = _capability.Observe(read);
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
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
    ClawChargeLimitCapability capability) : DeviceService<ClawIdentityState>(ServiceIds.ChargeLimit)
{
    private readonly ClawChargeLimitCapability _capability = capability
                                                             ?? throw new ArgumentNullException(nameof(capability));

    public ChargeLimitState? LastObserved { get; private set; }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return Set(DeviceServiceState.Passive, FirmwareReason(identity));
        }

        LastObserved = await ClawObservation.TryAsync(_capability.ReadAsync, "charge-limit", cancellationToken)
            .ConfigureAwait(false);
        return Set(DeviceServiceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        LastObserved = _capability.Observe(await _capability.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastObserved = null;
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
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
    ClawRecoveryJournal journal) : ClawJournalledService(ServiceIds.Fans)
{
    private readonly ClawFanCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    public FanSnapshot? LastObserved { get; private set; }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(DeviceServiceState.Faulted, ReconciliationBlockReason);
        }

        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                identity.ExactMachineMatch
                    ? CapabilityReasonCode.FirmwareNotVerified
                    : CapabilityReasonCode.GenerationChanged,
                "The Claw model or its MSI_ACPI provider was not available."));
        }

        LastObserved = await ClawObservation.TryAsync(_capability.ReadSnapshotAsync, "fans", cancellationToken)
            .ConfigureAwait(false);
        return Set(DeviceServiceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        LastObserved = _capability.Observe(
            await _capability.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false));
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
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
    ClawFanCapability capability) : DeviceService<ClawIdentityState>(ServiceIds.Telemetry)
{
    private readonly ClawFanCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    public FanTelemetry? LastTelemetry { get; private set; }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        LastTelemetry = await _capability.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "MSI telemetry requires the MSI_ACPI provider."));
        }

        LastTelemetry = await _capability.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Owned);
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastTelemetry = null;
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
    }
}

internal sealed class LightingService(
    IClawMcuTransport transport,
    ClawLightingCapability capability) : DeviceService<ClawIdentityState>(ServiceIds.Lighting)
{
    private readonly ClawLightingCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    private readonly IClawMcuTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public LightingState? LastObserved { get; private set; }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch)
        {
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Exact device identity no longer matches the Claw implementation."));
        }

        if (!await _transport.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return Set(DeviceServiceState.Passive, new CapabilityReason(
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

        return Set(DeviceServiceState.Owned);
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // RGB profile writes are intentionally persistent user choices. Deactivation never rewrites
        // the captured profile and therefore cannot silently undo the requested setting.
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
    }
}

internal sealed class MotionService(IClawMotionSource source, ClawModel model)
    : DeviceService<ClawIdentityState>(ServiceIds.Motion)
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
    ///     two alternating lines about 1.3 times a second: 7,619 lines and 40% of one day's log. The
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

    public override bool Suspendable => true;

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
        // governs the resampler's quiet cap, and the held rest it produces is correct: the average
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

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
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
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The Intel ISS physical gyrometer or accelerometer was unavailable; no synthetic fallback exists."));
    }

    public override ValueTask<DeviceServiceResult> SuspendAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        return ReleaseAsync(context, cancellationToken);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_latestGate)
        {
            _latest = null;
        }

        _resampler.Reset();
        return Set(DeviceServiceState.Idle);
    }
}

internal sealed class ChordSuppressorService(
    IFirmwareChordSuppressor suppressor,
    OemEventService oemEvents,
    IPluginHostAdapter host) : DeviceService<ClawIdentityState>(ServiceIds.ChordSuppressor)
{
    private readonly IPluginHostAdapter _host = host ?? throw new ArgumentNullException(nameof(host));

    private readonly OemEventService _oemEvents = oemEvents
                                                  ?? throw new ArgumentNullException(nameof(oemEvents));

    private readonly IFirmwareChordSuppressor _suppressor = suppressor
                                                            ?? throw new ArgumentNullException(nameof(suppressor));

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        // HC's chords work without MSI_Event, and are the only path to QS where it is missing.
        var started = await _suppressor.StartAsync(ReportFault, _oemEvents.RaiseChord, cancellationToken)
            .ConfigureAwait(false);
        return started
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Degraded, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The bounded low-level keyboard hook could not be installed."));
    }

    /// <summary>The keyboard hook stopped: the chords are gone until the next start, nothing else is.</summary>
    private void ReportFault(Exception exception)
    {
        var detail = DiagnosticText.FromException("The firmware chord suppressor stopped", exception);
        _host.Trace(DeviceTraceLevel.Warn, "chords", detail);
        _ = Set(DeviceServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
    }

    public override ValueTask<DeviceServiceResult> SuspendAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        return ReleaseAsync(context, cancellationToken);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        await _suppressor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }
}

/// <summary>Owns the panel's variable-refresh state for one cycle.</summary>
/// <remarks>
///     A service of its own rather than a corner of the lighting or power services, because it is the
///     only capability driven by the GPU driver rather than by MSI's firmware: it has no firmware
///     identity to verify, and it must be restored on release even when every WMI and MCU path failed.
/// </remarks>
internal sealed class DisplayService : DeviceServiceStatus, IDisposable
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
        State = DeviceServiceState.Idle;
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
        State = available ? DeviceServiceState.Owned : DeviceServiceState.Passive;
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
