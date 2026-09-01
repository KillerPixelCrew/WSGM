using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Interop;

namespace WSGM.Input;

/// <summary>
/// The production virtual-controller backend, over VIIPER's in-process USBIP server.
/// </summary>
/// <remarks>
/// VIIPER presents a virtual USB device through <c>usbip-win2</c>'s generic signed kernel driver, so
/// WSGM ships no driver of its own and needs no per-device kernel code. WSGM packs the device's own
/// target-specific wire state and submits it; VIIPER re-emits it to the host.
/// <para>
/// Everything here fails closed and fails quiet. A missing library, a missing USBIP driver, or a
/// refused attach leaves controller management unavailable with a reason, and never takes down the
/// shell, SDL input, or the Steam Input lease.
/// </para>
/// </remarks>
internal sealed class ViiperControllerBackend : IHidBackend
{
    /// <summary>Loopback endpoint the in-process USBIP server binds.</summary>
    /// <remarks>
    /// Loopback only. The virtual controller is local to this machine, and VIIPER's optional network
    /// mode would expose input devices to it.
    /// </remarks>
    internal const string ListenAddress = "127.0.0.1:0";

    /// <summary>The one bus WSGM owns.</summary>
    internal const uint BusId = 1;

    private static readonly IReadOnlyList<ManagedControllerTarget> Supported =
    [
        ManagedControllerTarget.SteamDeckComposite,
        ManagedControllerTarget.Xbox360,
        ManagedControllerTarget.DualShock4,
    ];

    /// <summary>Rumble command identifier in the Deck's feedback report.</summary>
    private const byte RumbleCommandId = 0xEB;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private GCHandle _self;
    private bool _initialized;
    private uint _deviceId;
    private uint _fastHandle;
    private ManagedControllerTarget? _deviceKind;
    private long _generation;
    private HidTargetHandle? _target;
    private long? _removalUnverifiedGeneration;
    private bool _disposed;

    /// <summary>The targets for which this build carries complete VIIPER wire encoders.</summary>
    internal static IReadOnlyList<ManagedControllerTarget> SupportedTargets => Supported;

    /// <inheritdoc/>
    public event EventHandler<HidTargetOutput>? OutputReceived;

    /// <inheritdoc/>
    public event EventHandler<long>? TargetLost;

    /// <inheritdoc/>
    public async Task<HidBackendHealth> DiscoverAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryInitializeUnderGate(out string detail))
            {
                return new HidBackendHealth(HidBackendHealthState.Unavailable, detail);
            }

            return new HidBackendHealth(
                HidBackendHealthState.Ready,
                "The VIIPER controller backend is ready.",
                new HidBackendCapabilities(Supported));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<HidTargetHandle> CreateTargetAsync(
        ManagedControllerTarget kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialNeutralState);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Supported.Contains(kind))
        {
            throw new InvalidOperationException($"The backend cannot create a {kind} target.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_target is not null)
            {
                throw new InvalidOperationException("A virtual target already exists.");
            }

            if (!TryInitializeUnderGate(out string detail))
            {
                throw new InvalidOperationException(detail);
            }

            string deviceType = kind switch
            {
                ManagedControllerTarget.SteamDeckComposite => "steamdeck",
                ManagedControllerTarget.Xbox360 => "xbox360",
                ManagedControllerTarget.DualShock4 => "dualshock4",
                _ => throw new InvalidOperationException($"The backend cannot create a {kind} target."),
            };
            Check(NativeViiper.DeviceAdd(BusId, deviceType, out uint deviceId), "add the device");
            Volatile.Write(ref _deviceId, deviceId);
            _deviceKind = kind;
            try
            {
                // Neutral before attach: the host enumerates the device and starts polling
                // immediately, and the first frame it reads must not be uninitialised memory.
                Check(
                    NativeViiper.DeviceOpenFast(BusId, deviceId, out uint handle),
                    "open the submission handle");
                _fastHandle = handle;
                if (!SubmitUnderGate(initialNeutralState))
                {
                    throw new InvalidOperationException(
                        "The controller backend rejected the initial neutral report.");
                }

                RegisterFeedbackUnderGate(deviceId);
                Check(NativeViiper.DeviceAttach(BusId, deviceId), "attach the device");
            }
            catch
            {
                RemoveDeviceUnderGate();
                throw;
            }

            HidTargetHandle target = new(kind, Interlocked.Increment(ref _generation));
            Volatile.Write(ref _target, target);
            Log.Info(
                $"Virtual controller created: {kind} as VIIPER device {BusId}:{deviceId}, "
                + $"generation={target.Generation}.");
            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// VIIPER attaches synchronously, so a returned handle already means the host accepted the
    /// device. There is nothing further to wait for.
    /// </remarks>
    public Task<bool> WaitForEnumerationAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Task.FromResult(_target?.Generation == target.Generation);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> PublishAsync(
        HidTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sample);
        if (_disposed || _target?.Generation != target.Generation)
        {
            return false;
        }

        bool lost;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_target?.Generation != target.Generation)
            {
                return false;
            }

            if (SubmitUnderGate(sample))
            {
                return true;
            }

            // A rejected submission means the device is gone whatever WSGM still believes; dropping
            // the handle routes this into the fault path. DeviceRemove must still run before the
            // handle is forgotten: VIIPER owns a device object and feedback callback beyond WSGM's
            // bookkeeping, and both leak for the rest of the process otherwise.
            lost = true;
            Volatile.Write(ref _target, null);
            bool removed = RemoveDeviceUnderGate();
            _removalUnverifiedGeneration = removed ? null : target.Generation;
        }
        finally
        {
            _gate.Release();
        }

        // Raised outside the gate: the handler stops the output sink, which comes back through
        // this backend.
        if (lost)
        {
            TargetLost?.Invoke(this, target.Generation);
        }

        return false;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A neutral packet that was not written is a failure, not a dropped sample: the caller is
    /// asking for the target to be left quiet before a handoff, and reporting success for a report
    /// the device never took is how a held control survives make-safe.
    /// </remarks>
    public async Task NeutralizeAsync(
        HidTargetHandle target,
        CanonicalControllerSample neutralState,
        CancellationToken cancellationToken)
    {
        if (!await PublishAsync(target, neutralState, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The controller backend could not write a neutral report to the virtual target.");
        }
    }

    /// <inheritdoc/>
    public async Task RemoveTargetAsync(HidTargetHandle target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_target?.Generation != target.Generation)
            {
                return;
            }

            // Make the native callback inert before plugout. VIIPER can have one host feedback
            // callback already in flight while it detaches the usbip-win2 port.
            Volatile.Write(ref _target, null);
            bool removed = RemoveDeviceUnderGate();
            // Removal is reported from what the library actually did, not from WSGM's bookkeeping;
            // the handle is dropped either way because an unaddressable target must not keep being
            // written to.
            _removalUnverifiedGeneration = removed ? null : target.Generation;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public Task<bool> WaitForRemovalAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_removalUnverifiedGeneration == target.Generation)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_target?.Generation != target.Generation);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_target is not null)
            {
                long generation = _target.Generation;
                Volatile.Write(ref _target, null);
                RemoveDeviceUnderGate();
                TargetLost?.Invoke(this, generation);
            }

            if (_initialized)
            {
                // Shutdown releases the bus and the server together, so the bus is not removed
                // separately; doing both would report a missing bus on the second call.
                SafeNative(NativeViiper.Shutdown, "shut down the controller backend");
                _initialized = false;
            }

            // The callback's user-data handle outlives VIIPER itself. Shutdown joins the native
            // server lifetime, so no callback can start after this point with a released handle.
            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private bool TryInitializeUnderGate(out string detail)
    {
        if (_initialized)
        {
            detail = string.Empty;
            return true;
        }

        try
        {
            if (NativeViiper.Init(ListenAddress) != NativeViiper.Ok)
            {
                detail = $"The controller backend could not start: {NativeViiper.TakeLastError()}";
                return false;
            }

            if (NativeViiper.BusCreate(BusId) != NativeViiper.Ok)
            {
                NativeViiper.Shutdown();
                detail = $"The controller backend could not create its bus: "
                    + NativeViiper.TakeLastError();
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            detail = "The controller backend library is not installed.";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            detail = "The installed controller backend library is the wrong version.";
            return false;
        }

        _initialized = true;
        detail = string.Empty;
        return true;
    }

    /// <summary>
    /// Subscribes to the host's feedback reports so rumble reaches the physical device.
    /// </summary>
    /// <remarks>
    /// The callback runs on a library thread, so it does the least possible work: decode, raise, and
    /// return. A strong handle carries the instance across the native boundary because
    /// <c>UnmanagedCallersOnly</c> cannot capture one, and it is released on disposal.
    /// </remarks>
    private unsafe void RegisterFeedbackUnderGate(uint deviceId)
    {
        if (!_self.IsAllocated)
        {
            _self = GCHandle.Alloc(this);
        }

        int result = NativeViiper.DeviceSetFeedbackCallback(
            BusId,
            deviceId,
            &OnFeedback,
            (void*)GCHandle.ToIntPtr(_self));
        if (result != NativeViiper.Ok)
        {
            // Output is not worth failing target creation over: the controller still works, it
            // simply does not rumble, and that is reported rather than hidden.
            Log.Warn(
                "Virtual controller output is unavailable; input continues: "
                + NativeViiper.TakeLastError());
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnFeedback(
        uint busId,
        uint deviceId,
        byte* data,
        int length,
        void* userData)
    {
        try
        {
            if (userData is null || data is null || length <= 0)
            {
                return;
            }

            if (GCHandle.FromIntPtr((IntPtr)userData).Target is not ViiperControllerBackend backend)
            {
                return;
            }

            if (busId != BusId || deviceId != Volatile.Read(ref backend._deviceId))
            {
                return;
            }

            HidTargetHandle? target = Volatile.Read(ref backend._target);
            if (target is null || DecodeFeedback(target.Kind, new ReadOnlySpan<byte>(data, length))
                is not { } motors)
            {
                return;
            }

            backend.OutputReceived?.Invoke(
                backend,
                new HidTargetOutput(
                    new HapticOutputFrame
                    {
                        TargetGeneration = target.Generation,
                        LowFrequency = motors.LowFrequency,
                        HighFrequency = motors.HighFrequency,
                        Timestamp = DateTimeOffset.UtcNow,
                    },
                    target.Kind));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never let an exception cross back into native code.
            Log.Warn($"Virtual controller feedback was dropped: {ex.Message}");
        }
    }

    /// <summary>Decodes one VIIPER target feedback frame into canonical rumble motors.</summary>
    internal static (float LowFrequency, float HighFrequency)? DecodeFeedback(
        ManagedControllerTarget kind,
        ReadOnlySpan<byte> report) => kind switch
        {
            ManagedControllerTarget.SteamDeckComposite
                when report.Length >= 9 && report[0] == RumbleCommandId =>
                (BinaryPrimitives.ReadUInt16LittleEndian(report[5..7]) / (float)ushort.MaxValue,
                    BinaryPrimitives.ReadUInt16LittleEndian(report[7..9]) / (float)ushort.MaxValue),
            ManagedControllerTarget.Xbox360 when report.Length >= 2 =>
                (report[0] / (float)byte.MaxValue, report[1] / (float)byte.MaxValue),
            // DS4 orders the small/high-frequency motor first and the large/low-frequency motor
            // second, followed by LED and flash state that WSGM deliberately does not own.
            ManagedControllerTarget.DualShock4 when report.Length >= 7 =>
                (report[1] / (float)byte.MaxValue, report[0] / (float)byte.MaxValue),
            _ => null,
        };

    private unsafe bool SubmitUnderGate(CanonicalControllerSample sample)
    {
        Span<byte> frame = stackalloc byte[SteamDeckNeptuneReport.Length];
        int length = _deviceKind switch
        {
            ManagedControllerTarget.SteamDeckComposite => SteamDeckNeptuneReport.Length,
            ManagedControllerTarget.Xbox360 => Xbox360Report.Length,
            ManagedControllerTarget.DualShock4 => DualShock4Report.Length,
            _ => 0,
        };
        switch (_deviceKind)
        {
            case ManagedControllerTarget.SteamDeckComposite:
                SteamDeckNeptuneReport.Write(sample, frame[..length]);
                break;
            case ManagedControllerTarget.Xbox360:
                Xbox360Report.Write(sample, frame[..length]);
                break;
            case ManagedControllerTarget.DualShock4:
                DualShock4Report.Write(sample, frame[..length]);
                break;
            default:
                return false;
        }

        int status;
        fixed (byte* data = frame)
        {
            status = NativeViiper.DeviceSetInputFast(_fastHandle, data, length);
        }

        if (status == NativeViiper.Ok)
        {
            return true;
        }

        // The host keeps whatever report it last accepted — a held button included — so a rejected
        // submission must be loud enough to diagnose from a pasted log.
        Log.Change(
            "controller.viiper.submit",
            $"VIIPER rejected an input frame on device {BusId}:{_deviceId}: status={status}, "
                + $"{NativeViiper.TakeLastError()}.",
            "warn ");
        return false;
    }

    /// <summary>Removes the VIIPER device and reports whether the library confirmed it.</summary>
    /// <returns><see langword="true"/> when removal was accepted.</returns>
    /// <remarks>
    /// The status must be read: a refused detach leaves the old virtual controller enumerated, and
    /// <see cref="WaitForRemovalAsync"/> has to report that rather than WSGM's own bookkeeping.
    /// </remarks>
    private bool RemoveDeviceUnderGate()
    {
        if (_deviceId == 0)
        {
            return true;
        }

        uint deviceId = _deviceId;
        ManagedControllerTarget? kind = _deviceKind;
        Volatile.Write(ref _deviceId, 0);
        _fastHandle = 0;
        _deviceKind = null;
        bool removed = false;
        Log.Info($"Virtual controller removal started: {kind} as VIIPER device {BusId}:{deviceId}.");
        SafeNative(
            () =>
            {
                int status = NativeViiper.DeviceRemove(BusId, deviceId);
                removed = status == NativeViiper.Ok;
                if (!removed)
                {
                    Log.Warn(
                        $"VIIPER refused to remove device {BusId}:{deviceId}: status={status}, "
                        + $"{NativeViiper.TakeLastError()}.");
                }
                else
                {
                    Log.Info($"Virtual controller removed: {kind} as VIIPER device {BusId}:{deviceId}.");
                }
            },
            $"remove VIIPER device {BusId}:{deviceId}");
        return removed;
    }

    private static void Check(int result, string operation)
    {
        if (result != NativeViiper.Ok)
        {
            throw new InvalidOperationException(
                $"The controller backend failed to {operation}: {NativeViiper.TakeLastError()}");
        }
    }

    /// <summary>Runs one native call; a status the caller needs is captured inside the action.</summary>
    /// <remarks>
    /// Deliberately the only overload. A former <c>Func&lt;int&gt;</c> twin forwarded here through
    /// <c>() => _ = action()</c>, and that lambda's int-valued body binds to <c>Func&lt;int&gt;</c>
    /// — itself — rather than <c>Action</c>, so every removal and shutdown recursed until the
    /// thread's stack was gone (device-observed 2026-09-01: Windows reported that it could not
    /// create a new stack guard page). One delegate shape leaves nothing to resolve.
    /// </remarks>
    internal static void SafeNative(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or SEHException)
        {
            Log.Warn($"Controller backend could not {operation}: {ex.Message}");
        }
    }
}
