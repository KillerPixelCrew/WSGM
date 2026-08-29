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
/// wire frame (<see cref="SteamDeckNeptuneReport"/>) and submits it; VIIPER re-emits it to the host.
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

    private static readonly IReadOnlyList<VirtualTargetKind> Supported =
    [
        VirtualTargetKind.SteamDeckComposite,
    ];

    /// <summary>Rumble command identifier in the Deck's feedback report.</summary>
    private const byte RumbleCommandId = 0xEB;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private GCHandle _self;
    private bool _initialized;
    private uint _deviceId;
    private uint _fastHandle;
    private long _generation;
    private HidTargetHandle? _target;
    private bool _disposed;

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
                new HidBackendCapabilities(new Version(1, 0), Supported, SupportsOutput: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<HidTargetHandle> CreateTargetAsync(
        VirtualTargetKind kind,
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

            Check(NativeViiper.DeviceAdd(BusId, "steamdeck", out uint deviceId), "add the device");
            _deviceId = deviceId;
            try
            {
                // Neutral before attach: the host enumerates the device and starts polling
                // immediately, and the first frame it reads must not be uninitialised memory.
                Check(
                    NativeViiper.DeviceOpenFast(BusId, deviceId, out uint handle),
                    "open the submission handle");
                _fastHandle = handle;
                SubmitUnderGate(initialNeutralState);
                RegisterFeedbackUnderGate(deviceId);
                Check(NativeViiper.DeviceAttach(BusId, deviceId), "attach the device");
            }
            catch
            {
                RemoveDeviceUnderGate();
                throw;
            }

            _target = new HidTargetHandle(
                kind,
                Interlocked.Increment(ref _generation),
                $"viiper:{BusId}:{deviceId}");
            Log.Info(
                $"Virtual controller created: {kind} as VIIPER device {BusId}:{deviceId}, "
                + $"generation={_target.Generation}.");
            return _target;
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_target?.Generation != target.Generation)
            {
                return false;
            }

            return SubmitUnderGate(sample);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public Task NeutralizeAsync(
        HidTargetHandle target,
        CanonicalControllerSample neutralState,
        CancellationToken cancellationToken) =>
        PublishAsync(target, neutralState, cancellationToken).AsTask();

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

            RemoveDeviceUnderGate();
            _target = null;
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
        return Task.FromResult(_target?.Generation != target.Generation);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyDictionary<string, string> diagnostics = new Dictionary<string, string>
            {
                ["backend"] = "viiper",
                ["initialized"] = _initialized.ToString(),
                ["busId"] = BusId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["deviceId"] = _deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["target"] = _target?.InstanceId ?? "none",
                ["targetGeneration"] = (_target?.Generation ?? 0)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            return diagnostics;
        }
        finally
        {
            _gate.Release();
        }
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
                RemoveDeviceUnderGate();
                _target = null;
                TargetLost?.Invoke(this, generation);
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            if (_initialized)
            {
                // Shutdown releases the bus and the server together, so the bus is not removed
                // separately; doing both would report a missing bus on the second call.
                SafeNative(NativeViiper.Shutdown, "shut down the controller backend");
                _initialized = false;
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
    /// return. A pinned handle carries the instance across the native boundary because
    /// <c>UnmanagedCallersOnly</c> cannot capture one, and it is released on disposal.
    /// </remarks>
    private unsafe void RegisterFeedbackUnderGate(uint deviceId)
    {
        if (!_self.IsAllocated)
        {
            _self = GCHandle.Alloc(this, GCHandleType.Weak);
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
            if (userData is null || data is null || length < 9)
            {
                return;
            }

            if (GCHandle.FromIntPtr((IntPtr)userData).Target is not ViiperControllerBackend backend)
            {
                return;
            }

            ReadOnlySpan<byte> report = new(data, length);
            if (report[0] != RumbleCommandId)
            {
                return;
            }

            // Deck rumble: two 16-bit speeds behind the command header. The canonical model is a
            // 0..1 unit per motor, so the device's own scale never leaves this method.
            float left = BinaryPrimitives.ReadUInt16LittleEndian(report[5..7]) / (float)ushort.MaxValue;
            float right = BinaryPrimitives.ReadUInt16LittleEndian(report[7..9]) / (float)ushort.MaxValue;
            HidTargetHandle? target = backend._target;
            if (target is null)
            {
                return;
            }

            backend.OutputReceived?.Invoke(
                backend,
                new HidTargetOutput(
                    new HapticOutputFrame
                    {
                        TargetGeneration = target.Generation,
                        LowFrequency = left,
                        HighFrequency = right,
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

    private unsafe bool SubmitUnderGate(CanonicalControllerSample sample)
    {
        Span<byte> frame = stackalloc byte[SteamDeckNeptuneReport.Length];
        SteamDeckNeptuneReport.Write(sample, frame);
        fixed (byte* data = frame)
        {
            return NativeViiper.DeviceSetInputFast(_fastHandle, data, frame.Length)
                == NativeViiper.Ok;
        }
    }

    private void RemoveDeviceUnderGate()
    {
        if (_deviceId == 0)
        {
            return;
        }

        uint deviceId = _deviceId;
        _deviceId = 0;
        _fastHandle = 0;
        SafeNative(
            () => NativeViiper.DeviceRemove(BusId, deviceId),
            $"remove VIIPER device {BusId}:{deviceId}");
    }

    private static void Check(int result, string operation)
    {
        if (result != NativeViiper.Ok)
        {
            throw new InvalidOperationException(
                $"The controller backend failed to {operation}: {NativeViiper.TakeLastError()}");
        }
    }

    private static void SafeNative(Action action, string operation)
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

    private static void SafeNative(Func<int> action, string operation) =>
        SafeNative(() => _ = action(), operation);
}
