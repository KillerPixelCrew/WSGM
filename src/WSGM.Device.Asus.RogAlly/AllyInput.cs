// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Gaming.Input;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Asus.RogAlly;

/// <summary>The transport an OEM button edge arrived on.</summary>
internal enum AllyOemSource
{
    /// <summary>A 0x5A report on the ASUS vendor collection.</summary>
    Vendor,

    /// <summary>An F-key on the ASUS keyboard collection.</summary>
    Keyboard
}

/// <summary>Buttons that reach the controller sample from outside the gamepad report.</summary>
/// <remarks>
///     The front OEM buttons arrive as vendor events with no release (HC releases them after its
///     <c>KeyPressDelay</c>, <c>ROGAlly.cs:485-505</c>; HHD after 150 ms, <c>rog_ally/base.py:180-196</c>),
///     and the rear buttons and the Xbox models' front buttons as keyboard keys with real edges. An event
///     is latched for <see cref="HoldDuration" /> as the Claw plugin does; a key is held for as long as it
///     is down. The same physical button may report on both transports, so <see cref="Admit" /> passes
///     only the first report of each press.
/// </remarks>
internal sealed class AllyOemButtonState
{
    /// <summary>HHD's <c>MODE_DELAY</c>, the synthesized press length for a release-less event.</summary>
    internal static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(150);

    private readonly Dictionary<string, ControlEdges> _controls = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private DateTimeOffset _guideUntil;
    private CanonicalButtons _heldByKeyboard;
    private CanonicalButtons _heldByVendor;
    private DateTimeOffset _quickAccessUntil;

    /// <summary>Decides whether one edge is a new report of its control, not the other transport's echo.</summary>
    /// <param name="controlId">The OEM control the edge belongs to.</param>
    /// <param name="source">The transport it arrived on.</param>
    /// <param name="edge">Press or release.</param>
    /// <param name="releases">Whether this source sends a release for its presses.</param>
    /// <param name="now">When it arrived.</param>
    /// <returns>True when the edge should change button state and be published.</returns>
    public bool Admit(string controlId, AllyOemSource source, OemControlEdge edge, bool releases, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_controls.TryGetValue(controlId, out var control))
            {
                control = new ControlEdges();
                _controls.Add(controlId, control);
            }

            var index = (int)source;
            if (edge is OemControlEdge.Released)
            {
                var admitted = control.Admitted[index];
                control.Down[index] = false;
                control.Admitted[index] = false;
                return admitted;
            }

            var other = 1 - index;
            var echo = control.Down[other]
                       || (control.LastPressSource == other && now - control.LastPress < HoldDuration);
            control.Down[index] = releases;
            control.Admitted[index] = releases && !echo;
            if (echo)
            {
                return false;
            }

            control.LastPress = now;
            control.LastPressSource = index;
            return true;
        }
    }

    /// <summary>Forgets one source's outstanding presses of the given controls, when it stops reporting them.</summary>
    public void Forget(AllyOemSource source, params ReadOnlySpan<string> controlIds)
    {
        lock (_gate)
        {
            foreach (var controlId in controlIds)
            {
                if (_controls.TryGetValue(controlId, out var control))
                {
                    control.Down[(int)source] = false;
                    control.Admitted[(int)source] = false;
                }
            }
        }
    }

    public void Latch(CanonicalButtons button, DateTimeOffset now)
    {
        lock (_gate)
        {
            var until = now + HoldDuration;
            if ((button & CanonicalButtons.Guide) != 0)
            {
                _guideUntil = until;
            }

            if ((button & CanonicalButtons.QuickAccess) != 0)
            {
                _quickAccessUntil = until;
            }
        }
    }

    /// <summary>Holds or releases buttons for one source; a button is down while either source holds it.</summary>
    public void Hold(AllyOemSource source, CanonicalButtons button, bool down)
    {
        lock (_gate)
        {
            if (source is AllyOemSource.Vendor)
            {
                _heldByVendor = down ? _heldByVendor | button : _heldByVendor & ~button;
            }
            else
            {
                _heldByKeyboard = down ? _heldByKeyboard | button : _heldByKeyboard & ~button;
            }
        }
    }

    /// <summary>Releases buttons whichever source holds them.</summary>
    public void Release(CanonicalButtons button)
    {
        lock (_gate)
        {
            _heldByVendor &= ~button;
            _heldByKeyboard &= ~button;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _heldByVendor = CanonicalButtons.None;
            _heldByKeyboard = CanonicalButtons.None;
            _guideUntil = default;
            _quickAccessUntil = default;
            _controls.Clear();
        }
    }

    public CanonicalButtons Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            var buttons = _heldByVendor | _heldByKeyboard;
            buttons |= now < _guideUntil ? CanonicalButtons.Guide : CanonicalButtons.None;
            buttons |= now < _quickAccessUntil ? CanonicalButtons.QuickAccess : CanonicalButtons.None;
            return buttons;
        }
    }

    private sealed class ControlEdges
    {
        public readonly bool[] Admitted = new bool[2];
        public readonly bool[] Down = new bool[2];
        public DateTimeOffset LastPress;
        public int LastPressSource = -1;
    }
}

/// <summary>How the physical pad is read.</summary>
internal enum AllyControllerRoute
{
    /// <summary>An XInput slot, HC's route for every Ally (<c>XboxAdaptiveController : XInputController</c>).</summary>
    XInput,

    /// <summary>
    ///     Windows.Gaming.Input, only when no XInput slot is found. HC reads every Ally, the Xbox models
    ///     included, through XInput and has no such route. This route has no guide button, and WSGM owns
    ///     it only when a hideable XUSB, GIP or XInput HID node exists.
    /// </summary>
    WindowsGamingInput
}

/// <summary>The pad this cycle reads, and the device nodes WSGM must hide while it does.</summary>
internal sealed record AllyControllerTopology(
    AllyControllerRoute Route,
    int XInputSlot,
    IReadOnlyList<PhysicalDeviceIdentity> PhysicalDevices,
    string Observed);

internal interface IAllyControllerSource : IAsyncDisposable
{
    ValueTask<AllyControllerTopology?> DiscoverAsync(CancellationToken cancellationToken);

    ValueTask StartAsync(
        AllyControllerTopology topology,
        long cycleGeneration,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);

    ValueTask WriteRumbleAsync(float low, float high, CancellationToken cancellationToken);
}

/// <summary>XInput state to canonical sample. XInput's thumbs are already positive up.</summary>
internal static class AllyControllerCodec
{
    public const ushort DPadUp = 0x0001;
    public const ushort DPadDown = 0x0002;
    public const ushort DPadLeft = 0x0004;
    public const ushort DPadRight = 0x0008;
    public const ushort Start = 0x0010;
    public const ushort Back = 0x0020;
    public const ushort LeftThumb = 0x0040;
    public const ushort RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightShoulder = 0x0200;

    /// <summary>The guide bit only <c>XInputGetStateEx</c> (ordinal 100) reports; HC <c>XInputStateButtons.Xbox</c>.</summary>
    public const ushort Guide = 0x0400;

    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;

    public static CanonicalButtons Buttons(ushort buttons)
    {
        var result = CanonicalButtons.None;
        result |= (buttons & A) != 0 ? CanonicalButtons.A : 0;
        result |= (buttons & B) != 0 ? CanonicalButtons.B : 0;
        result |= (buttons & X) != 0 ? CanonicalButtons.X : 0;
        result |= (buttons & Y) != 0 ? CanonicalButtons.Y : 0;
        result |= (buttons & LeftShoulder) != 0 ? CanonicalButtons.LeftShoulder : 0;
        result |= (buttons & RightShoulder) != 0 ? CanonicalButtons.RightShoulder : 0;
        result |= (buttons & LeftThumb) != 0 ? CanonicalButtons.LeftStick : 0;
        result |= (buttons & RightThumb) != 0 ? CanonicalButtons.RightStick : 0;
        result |= (buttons & Back) != 0 ? CanonicalButtons.View : 0;
        result |= (buttons & Start) != 0 ? CanonicalButtons.Menu : 0;
        result |= (buttons & DPadUp) != 0 ? CanonicalButtons.DPadUp : 0;
        result |= (buttons & DPadDown) != 0 ? CanonicalButtons.DPadDown : 0;
        result |= (buttons & DPadLeft) != 0 ? CanonicalButtons.DPadLeft : 0;
        result |= (buttons & DPadRight) != 0 ? CanonicalButtons.DPadRight : 0;
        result |= (buttons & Guide) != 0 ? CanonicalButtons.Guide : 0;
        return result;
    }

    public static CanonicalControllerSample Decode(
        in XInputNative.State state,
        CanonicalButtons oem,
        long sequence,
        long cycleGeneration,
        DateTimeOffset timestamp,
        SampleQuality quality)
    {
        var gamepad = state.Gamepad;
        return new CanonicalControllerSample
        {
            Sequence = sequence,
            CycleGeneration = cycleGeneration,
            Timestamp = timestamp,
            Buttons = Buttons(gamepad.Buttons) | oem,
            LeftStickX = Axis(gamepad.ThumbLX),
            LeftStickY = Axis(gamepad.ThumbLY),
            RightStickX = Axis(gamepad.ThumbRX),
            RightStickY = Axis(gamepad.ThumbRY),
            LeftTrigger = gamepad.LeftTrigger / 255f,
            RightTrigger = gamepad.RightTrigger / 255f,
            Quality = quality
        };
    }

    public static CanonicalButtons Buttons(GamepadButtons buttons)
    {
        var result = CanonicalButtons.None;
        result |= buttons.HasFlag(GamepadButtons.A) ? CanonicalButtons.A : 0;
        result |= buttons.HasFlag(GamepadButtons.B) ? CanonicalButtons.B : 0;
        result |= buttons.HasFlag(GamepadButtons.X) ? CanonicalButtons.X : 0;
        result |= buttons.HasFlag(GamepadButtons.Y) ? CanonicalButtons.Y : 0;
        result |= buttons.HasFlag(GamepadButtons.LeftShoulder) ? CanonicalButtons.LeftShoulder : 0;
        result |= buttons.HasFlag(GamepadButtons.RightShoulder) ? CanonicalButtons.RightShoulder : 0;
        result |= buttons.HasFlag(GamepadButtons.LeftThumbstick) ? CanonicalButtons.LeftStick : 0;
        result |= buttons.HasFlag(GamepadButtons.RightThumbstick) ? CanonicalButtons.RightStick : 0;
        result |= buttons.HasFlag(GamepadButtons.View) ? CanonicalButtons.View : 0;
        result |= buttons.HasFlag(GamepadButtons.Menu) ? CanonicalButtons.Menu : 0;
        result |= buttons.HasFlag(GamepadButtons.DPadUp) ? CanonicalButtons.DPadUp : 0;
        result |= buttons.HasFlag(GamepadButtons.DPadDown) ? CanonicalButtons.DPadDown : 0;
        result |= buttons.HasFlag(GamepadButtons.DPadLeft) ? CanonicalButtons.DPadLeft : 0;
        result |= buttons.HasFlag(GamepadButtons.DPadRight) ? CanonicalButtons.DPadRight : 0;
        return result;
    }

    public static float Axis(short value)
    {
        return Math.Clamp(value / 32767f, -1f, 1f);
    }
}

/// <summary>Reads the Ally pad through XInput, or Windows.Gaming.Input when it has no XInput slot.</summary>
internal sealed class WindowsAllyControllerSource(AllyModel model, AllyOemButtonState oem) : IAllyControllerSource
{
    /// <summary>About 125 Hz, the Claw's pad cadence, which the motion resampler is tuned against.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(8);

    private readonly Lock _gate = new();
    private readonly AllyModel _model = model ?? throw new ArgumentNullException(nameof(model));
    private readonly AllyOemButtonState _oem = oem ?? throw new ArgumentNullException(nameof(oem));
    private CancellationTokenSource? _cancellation;
    private Gamepad? _gamepad;
    private int _slot = -1;
    private Thread? _worker;

    public async ValueTask<AllyControllerTopology?> DiscoverAsync(CancellationToken cancellationToken)
    {
        var nodes = AllyHidEnumerator.ControllerNodes(_model.ControllerProductIds);
        IReadOnlyList<PhysicalDeviceIdentity> devices =
        [
            .. nodes.Select(node => new PhysicalDeviceIdentity
            {
                InstancePath = node.InstancePath,
                LocationPath = node.PhysicalLocation,
                VendorId = AllyModels.AsusVendorId.ToString("X4", CultureInfo.InvariantCulture),
                ProductId = ProductOf(node.InstancePath),
                RequiresHiding = true
            })
        ];
        var observed = string.Join(", ",
            nodes.Select(node => $"{node.InstancePath} {ClassName(node.ClassGuid)}").Take(8));
        var slot = FindXInputSlot();
        if (slot >= 0)
        {
            return new AllyControllerTopology(AllyControllerRoute.XInput, slot, devices, $"xinput {slot}; {observed}");
        }

        var gamepad = await FindGamepadAsync(cancellationToken).ConfigureAwait(false);
        return gamepad is null
            ? null
            : new AllyControllerTopology(AllyControllerRoute.WindowsGamingInput, -1, devices, $"wgi; {observed}");
    }

    public async ValueTask StartAsync(
        AllyControllerTopology topology,
        long cycleGeneration,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(fault);
        var gamepad = topology.Route is AllyControllerRoute.WindowsGamingInput
            ? await FindGamepadAsync(cancellationToken).ConfigureAwait(false)
              ?? throw new InvalidOperationException("The Windows.Gaming.Input pad disappeared.")
            : null;
        lock (_gate)
        {
            if (_worker is not null)
            {
                throw new InvalidOperationException("The Ally controller reader is already active.");
            }

            _slot = topology.XInputSlot;
            _gamepad = gamepad;
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _worker = new Thread(() =>
                Run(topology.Route, topology.XInputSlot, gamepad, cycleGeneration, publish, fault, cancellation.Token))
            {
                IsBackground = true,
                Name = "WSGM Ally controller reader"
            };
            _worker.Start();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Thread? worker;
        lock (_gate)
        {
            worker = _worker;
            _cancellation?.Cancel();
        }

        var stopped = worker is null || await Task.Run(() => worker.Join(TimeSpan.FromSeconds(1)),
            CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_worker == worker)
            {
                _worker = null;
                _slot = -1;
                _gamepad = null;
                // A reader that has not exited still waits on this token's handle; it is left to the
                // collector rather than disposed under it.
                if (stopped)
                {
                    _cancellation?.Dispose();
                }

                _cancellation = null;
            }
        }

        if (!stopped)
        {
            throw new TimeoutException("The Ally controller reader did not stop within one second.");
        }
    }

    public ValueTask WriteRumbleAsync(float low, float high, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int slot;
        Gamepad? gamepad;
        lock (_gate)
        {
            slot = _slot;
            gamepad = _gamepad;
        }

        if (gamepad is not null)
        {
            gamepad.Vibration = new GamepadVibration { LeftMotor = low, RightMotor = high };
            return ValueTask.CompletedTask;
        }

        if (slot < 0)
        {
            throw new InvalidOperationException("The Ally controller route is no longer available for rumble.");
        }

        // HC's XInputController.SetVibration: the large (low-frequency) motor is left, the small right
        // (XInputController.cs:279-296). HHD writes an HID output report instead (base.py:226-254),
        // which the RC73XA lab run showed has no output collection to go to; HC is followed.
        var vibration = new XInputNative.Vibration
        {
            LeftMotorSpeed = (ushort)Math.Round(Math.Clamp(low, 0f, 1f) * ushort.MaxValue),
            RightMotorSpeed = (ushort)Math.Round(Math.Clamp(high, 0f, 1f) * ushort.MaxValue)
        };
        var result = XInputNative.XInputSetState((uint)slot, ref vibration);
        if (result != 0)
        {
            throw new Win32Exception((int)result, "XInputSetState did not accept the Ally rumble frame.");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private int FindXInputSlot()
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            try
            {
                if (XInputNative.XInputGetCapabilitiesEx(1, slot, 0, out var caps) == 0
                    && caps.VendorId == AllyModels.AsusVendorId
                    && _model.ControllerProductIds.Contains(caps.ProductId))
                {
                    return (int)slot;
                }
            }
            catch (EntryPointNotFoundException)
            {
                return -1;
            }
        }

        return -1;
    }

    private async ValueTask<Gamepad?> FindGamepadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The list fills asynchronously after first access in a desktop process.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var match = Gamepad.Gamepads.FirstOrDefault(pad =>
                {
                    var raw = RawGameController.FromGameController(pad);
                    return raw?.HardwareVendorId == AllyModels.AsusVendorId
                           && _model.ControllerProductIds.Contains(raw.HardwareProductId);
                });
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidOperationException)
        {
            PluginTrace.Failure("controller", "Windows.Gaming.Input discovery failed", ex);
        }

        return null;
    }

    private void Run(
        AllyControllerRoute route,
        int slot,
        Gamepad? gamepad,
        long cycleGeneration,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        long sequence = 0;
        var first = true;
        try
        {
            while (!cancellationToken.WaitHandle.WaitOne(PollInterval))
            {
                var now = DateTimeOffset.UtcNow;
                CanonicalControllerSample sample;
                if (route is AllyControllerRoute.XInput)
                {
                    var result = XInputNative.XInputGetStateEx((uint)slot, out var state);
                    if (result != 0)
                    {
                        throw new InvalidOperationException($"XInput slot {slot} stopped answering ({result}).");
                    }

                    sample = AllyControllerCodec.Decode(state, _oem.Current(now), ++sequence,
                        cycleGeneration, now, first ? SampleQuality.Discontinuity : SampleQuality.Good);
                }
                else
                {
                    var reading = gamepad!.GetCurrentReading();
                    sample = new CanonicalControllerSample
                    {
                        Sequence = ++sequence,
                        CycleGeneration = cycleGeneration,
                        Timestamp = now,
                        Buttons = AllyControllerCodec.Buttons(reading.Buttons) | _oem.Current(now),
                        LeftStickX = (float)Math.Clamp(reading.LeftThumbstickX, -1, 1),
                        LeftStickY = (float)Math.Clamp(reading.LeftThumbstickY, -1, 1),
                        RightStickX = (float)Math.Clamp(reading.RightThumbstickX, -1, 1),
                        RightStickY = (float)Math.Clamp(reading.RightThumbstickY, -1, 1),
                        LeftTrigger = (float)Math.Clamp(reading.LeftTrigger, 0, 1),
                        RightTrigger = (float)Math.Clamp(reading.RightTrigger, 0, 1),
                        Quality = first ? SampleQuality.Discontinuity : SampleQuality.Good
                    };
                }

                first = false;
                publish(sample, cancellationToken).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                fault(ex);
            }
            catch (Exception callback) when (callback is not OutOfMemoryException)
            {
                PluginTrace.Failure("controller", "Controller fault reporting failed", callback);
            }
        }
    }

    private static string? ProductOf(string instancePath)
    {
        var index = instancePath.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        return index >= 0 && index + 8 <= instancePath.Length
            ? instancePath.Substring(index + 4, 4).ToUpperInvariant()
            : null;
    }

    private static string ClassName(Guid classGuid)
    {
        return classGuid == AllyHidEnumerator.XnaCompositeClass ? "xusb"
            : classGuid == AllyHidEnumerator.XboxCompositeClass ? "gip"
            : "hid";
    }
}

internal static class XInputNative
{
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    public static extern uint XInputGetStateEx(uint userIndex, out State state);

    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    public static extern uint XInputGetCapabilitiesEx(uint reserved, uint userIndex, uint flags,
        out CapabilitiesEx capabilities);

    [DllImport("xinput1_4.dll")]
    public static extern uint XInputSetState(uint userIndex, ref Vibration vibration);

    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    /// <summary>HC's <c>XInputCapabilitiesEx</c> (<c>XInputController.cs:53-89</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CapabilitiesEx
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public Gamepad Gamepad;
        public Vibration Vibration;
        public ushort VendorId;
        public ushort ProductId;
        public ushort Revision;
        public uint Xid;
    }
}

/// <summary>Raw keyboard events the hook claimed.</summary>
internal readonly record struct AllyKeyEvent(uint VirtualKey, bool Down, DateTimeOffset Timestamp);

internal interface IAllyKeyboardSource : IAsyncDisposable
{
    /// <summary>Installs the hook. Only keys in the watched set are consumed and reported.</summary>
    ValueTask<bool> StartAsync(Func<AllyKeyEvent, ValueTask> callback, Action<Exception> fault,
        CancellationToken cancellationToken);

    /// <summary>Replaces the set of consumed keys. Keys outside it pass through untouched.</summary>
    void Watch(IReadOnlyCollection<uint> virtualKeys);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>A low-level keyboard hook that claims the F-keys the Ally firmware sends for OEM buttons.</summary>
/// <remarks>
///     HHD grabs the ASUS keyboard evdev for F17/F18 (<c>rog_ally/base.py:396-403</c>); Windows opens
///     keyboards exclusively, so a hook is the Windows equivalent, as HC's keyboard chords are. The hook
///     cannot tell the ASUS keyboard from another one; F17, F18, F21 and F22 are claimed because no
///     ordinary keyboard sends them. Injected input always passes. The callback is allocation-light and
///     does no I/O: it only posts to a bounded channel.
/// </remarks>
internal sealed class WindowsAllyKeyboardHook : IAllyKeyboardSource
{
    private readonly Channel<AllyKeyEvent> _events = Channel.CreateBounded<AllyKeyEvent>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

    private readonly Lock _gate = new();
    private readonly KeyboardNative.HookProcedure _procedure;

    private CancellationTokenSource? _cancellation;
    private nint _hook;
    private Task? _pump;
    private Thread? _thread;
    private uint _threadId;
    private bool[] _watched = new bool[256];

    public WindowsAllyKeyboardHook()
    {
        _procedure = Callback;
    }

    public void Watch(IReadOnlyCollection<uint> virtualKeys)
    {
        var watched = new bool[256];
        foreach (var key in virtualKeys.Where(key => key < 256))
        {
            watched[key] = true;
        }

        Volatile.Write(ref _watched, watched);
    }

    public async ValueTask<bool> StartAsync(
        Func<AllyKeyEvent, ValueTask> callback,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(fault);
        TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_thread is not null)
            {
                return _hook != 0;
            }

            _cancellation = new CancellationTokenSource();
            _pump = PumpAsync(callback, fault, _cancellation.Token);
            _thread = new Thread(() => Run(started, fault))
            {
                IsBackground = true,
                Name = "WSGM Ally OEM keyboard hook"
            };
            _thread.Start();
        }

        return await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Thread? thread;
        uint threadId;
        Task? pump;
        lock (_gate)
        {
            thread = _thread;
            threadId = _threadId;
            pump = _pump;
            _cancellation?.Cancel();
        }

        if (thread is not null)
        {
            if (threadId != 0)
            {
                _ = KeyboardNative.PostThreadMessage(threadId, KeyboardNative.WM_QUIT, 0, 0);
            }

            _ = await Task.Run(() => thread.Join(TimeSpan.FromSeconds(1)), CancellationToken.None)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (pump is not null)
        {
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }
        }

        lock (_gate)
        {
            _thread = null;
            _threadId = 0;
            _pump = null;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void Run(TaskCompletionSource<bool> started, Action<Exception> fault)
    {
        _threadId = KeyboardNative.GetCurrentThreadId();
        _hook = KeyboardNative.SetWindowsHookEx(KeyboardNative.WH_KEYBOARD_LL, _procedure, 0, 0);
        if (_hook == 0)
        {
            started.TrySetResult(false);
            return;
        }

        started.TrySetResult(true);
        try
        {
            while (KeyboardNative.GetMessage(out var message, 0, 0, 0) > 0)
            {
                _ = KeyboardNative.TranslateMessage(in message);
                _ = KeyboardNative.DispatchMessage(in message);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fault(ex);
        }
        finally
        {
            _ = KeyboardNative.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    private unsafe nint Callback(int code, nuint message, nint data)
    {
        if (code >= 0)
        {
            var keyboard = *(KeyboardNative.KeyboardHookData*)data;
            var down = message is KeyboardNative.WM_KEYDOWN or KeyboardNative.WM_SYSKEYDOWN;
            var up = message is KeyboardNative.WM_KEYUP or KeyboardNative.WM_SYSKEYUP;
            var watched = Volatile.Read(ref _watched);
            if ((down || up) && keyboard.VirtualKey < 256 && watched[keyboard.VirtualKey]
                && (keyboard.Flags & KeyboardNative.LLKHF_INJECTED) == 0)
            {
                _events.Writer.TryWrite(new AllyKeyEvent(keyboard.VirtualKey, down, DateTimeOffset.UtcNow));
                return 1;
            }
        }

        return KeyboardNative.CallNextHookEx(_hook, code, message, data);
    }

    private async Task PumpAsync(Func<AllyKeyEvent, ValueTask> callback, Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var keyEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await callback(keyEvent).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fault(ex);
        }
    }
}

internal static partial class KeyboardNative
{
    public delegate nint HookProcedure(int code, nuint message, nint data);

    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_QUIT = 0x0012;
    public const nuint WM_KEYDOWN = 0x0100;
    public const nuint WM_KEYUP = 0x0101;
    public const nuint WM_SYSKEYDOWN = 0x0104;
    public const nuint WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_INJECTED = 0x10;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int hookId, HookProcedure procedure, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }
}
