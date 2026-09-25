using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Gaming.Input;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Capture.Live;

/// <summary>One input device the capture has seen.</summary>
/// <param name="Id">Short ID used by events, for example <c>hid3</c>.</param>
/// <param name="Kind"><c>keyboard</c>, <c>mouse</c>, <c>hid</c>, <c>xinput</c> or <c>wgi</c>.</param>
/// <param name="VendorId">USB vendor ID as four hex digits, when known.</param>
/// <param name="ProductId">USB product ID as four hex digits, when known.</param>
/// <param name="UsagePage">Top-level collection usage page, for HID.</param>
/// <param name="Usage">Top-level collection usage, for HID.</param>
/// <param name="Path">Device interface path; redacted on export.</param>
/// <param name="Virtual">Whether the device is a software device or injected input.</param>
/// <param name="Name">Display name, for controllers.</param>
internal sealed record LabInputDevice(
    string Id,
    string Kind,
    string? VendorId,
    string? ProductId,
    int? UsagePage,
    int? Usage,
    string? Path,
    bool Virtual,
    string? Name = null);

/// <summary>One recorded input event.</summary>
/// <param name="Ms">Milliseconds since the capture started.</param>
/// <param name="Source">
///     <c>raw-input</c>, <c>hid-read</c>, <c>directinput</c>, <c>hook</c>, <c>xinput</c>, <c>wgi</c>, <c>wmi</c>, <c>power</c>, <c>device</c> or
///     <c>app-command</c>.
/// </param>
/// <param name="Device">Device ID from <see cref="LabInputDevice.Id" />, when the source names one.</param>
/// <param name="Detail">Readable description.</param>
/// <param name="Data">Raw report bytes as hex, for HID reports.</param>
/// <param name="Changed">Byte offsets that changed against the previous report and are not baseline noise.</param>
/// <param name="Noise">Whether only baseline-noise bytes changed.</param>
internal sealed record LabInputEvent(
    double Ms,
    string Source,
    string? Device,
    string Detail,
    string? Data = null,
    IReadOnlyList<int>? Changed = null,
    bool Noise = false);

/// <summary>Something a person could have caused, raised while a step runs.</summary>
/// <param name="Source">Source name.</param>
/// <param name="Device">Device ID, when known.</param>
/// <param name="Detail">Readable description.</param>
internal readonly record struct LabInputActivity(string Source, string? Device, string Detail);

/// <summary>Everything recorded during one step.</summary>
internal sealed record LabInputStepRecord
{
    /// <summary>Step ID.</summary>
    public required string Step { get; init; }

    /// <summary>When the step started, in capture milliseconds.</summary>
    public required double StartedMs { get; init; }

    /// <summary>When the step ended, in capture milliseconds.</summary>
    public required double EndedMs { get; init; }

    /// <summary>Events in receipt order.</summary>
    public required IReadOnlyList<LabInputEvent> Events { get; init; }

    /// <summary>Events not stored because a bound was reached, by source.</summary>
    public IReadOnlyDictionary<string, int> Dropped { get; init; } = new Dictionary<string, int>();

    /// <summary>Unchanged or noise-only reports counted but not stored, by device.</summary>
    public IReadOnlyDictionary<string, int> Repeated { get; init; } = new Dictionary<string, int>();

    /// <summary>Pointer movement not stored event by event: reports and summed X and Y, by device.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<long>> PointerMotion { get; init; } =
        new Dictionary<string, IReadOnlyList<long>>();
}

/// <summary>
///     Listens to every input channel Windows offers at once and records all of them.
/// </summary>
/// <remarks>
///     Raw Input from every HID top-level collection on the machine (every usage page present, keyboards
///     and mice included, background delivery), low-level keyboard and mouse hooks (which see injected
///     input), XInput slots 0-3 including the guide button, Windows.Gaming.Input raw controllers, WMI
///     firmware and ACPI events, shell app commands, power settings, suspend and resume, and device
///     arrival. Nothing is filtered by device and nothing is suppressed: attribution happens afterwards
///     in <see cref="LabInputAnalysis" />. A dedicated thread owns the message-only window and hooks; a
///     second thread polls the controller APIs. Storage per step is bounded; what does not fit is
///     counted.
/// </remarks>
internal sealed partial class LabInputCapture : IDisposable
{
    /// <summary>Most events stored per step.</summary>
    public const int MaximumEventsPerStep = 6000;

    /// <summary>Most noise-only reports stored per device per step (one in every <see cref="NoiseSampleEvery" />).</summary>
    public const int MaximumNoisePerDevice = 200;

    /// <summary>Sampling interval for noise-only reports.</summary>
    public const int NoiseSampleEvery = 50;

    private const int MaximumReportBytes = 512;

    private readonly Dictionary<IntPtr, LabInputDevice> _byHandle = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<LabInputDevice> _devices = [];
    private readonly Dictionary<string, int> _deviceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _dropped = new(StringComparer.Ordinal);
    private readonly List<LabInputEvent> _events = [];
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _lastReports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long[]> _motion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _noise = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _noiseStored = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<string, int> _repeated = new(StringComparer.Ordinal);
    private readonly List<string> _unavailable = [];
    private readonly List<ManagementEventWatcher> _watchers = [];
    private bool _baseline;
    private volatile bool _disposed;
    private Thread? _messageThread;
    private uint _messageThreadId;
    private Thread? _pollThread;
    private string _step = "idle";
    private double _stepStarted;

    internal LabInputCapture(nint windowHandle)
    {
        _directInputWindow = windowHandle;
    }

    /// <summary>Devices seen so far.</summary>
    public IReadOnlyList<LabInputDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _devices];
            }
        }
    }

    /// <summary>Sources that could not be started, with the reason.</summary>
    public IReadOnlyList<string> Unavailable
    {
        get
        {
            lock (_gate)
            {
                return [.. _unavailable];
            }
        }
    }

    /// <summary>Milliseconds since the capture started.</summary>
    public double Now => _clock.Elapsed.TotalMilliseconds;

    /// <summary>
    ///     While set, Windows-key and Alt+Tab shortcuts are swallowed after they are recorded, so a
    ///     firmware chord such as Win+D or Win+G cannot minimize the wizard or open an overlay mid-step.
    ///     Nothing else is ever suppressed.
    /// </summary>
    public bool SwallowShortcuts { get; set; }

    /// <summary>
    ///     While set, every controller state change and every pointer movement is stored instead of only
    ///     significant ones, for stick, trigger and touchpad steps.
    /// </summary>
    public bool Detailed { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        HidCollectionReader[] readers;
        lock (_gate)
        {
            readers = [.. _hidReaders];
            _hidReaders.Clear();
        }

        foreach (var reader in readers)
        {
            reader.Dispose();
        }
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.Stop();
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is ManagementException or COMException or InvalidOperationException)
            {
                // Stopping a watcher whose provider went away is not an error worth reporting.
            }
        }

        _watchers.Clear();
        if (_messageThreadId != 0)
        {
            PostThreadMessage(_messageThreadId, WmQuit, 0, 0);
        }

        _messageThread?.Join(TimeSpan.FromSeconds(5));
        _pollThread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    /// <summary>Raised on a capture thread for anything a person could have caused.</summary>
    public event Action<LabInputActivity>? Activity;

    /// <summary>Raised on the capture thread when Windows suspends (<c>true</c>) or resumes (<c>false</c>).</summary>
    public event Action<bool>? SuspendResume;

    /// <summary>Raised on the capture thread when an input device arrives or leaves.</summary>
    public event Action<LabInputDevice, bool>? DeviceChanged;

    /// <summary>Starts every source and returns once the message thread is running.</summary>
    /// <returns>The running capture.</returns>
    public static LabInputCapture Start(nint windowHandle)
    {
        LabInputCapture capture = new(windowHandle);
        LabTrace.Write("capture: message thread start");
        capture._messageThread = new Thread(capture.MessageLoop) { IsBackground = true, Name = "Device Lab input" };
        capture._messageThread.SetApartmentState(ApartmentState.STA);
        capture._messageThread.Start();
        if (!capture._ready.Wait(TimeSpan.FromSeconds(10)))
        {
            LabTrace.Write("capture: message thread did not become ready");
            capture.Dispose();
            throw new InvalidOperationException("The input capture thread did not start.");
        }

        LabTrace.Write("capture: poll thread start (XInput, Windows.Gaming.Input, DirectInput)");
        capture._pollThread = new Thread(capture.PollLoop) { IsBackground = true, Name = "Device Lab controller poll" };
        capture._pollThread.Start();
        LabTrace.Write("capture: direct HID readers start");
        capture.StartHidCollections();
        LabTrace.Write("capture: WMI event watchers start");
        capture.StartWmi();
        LabTrace.Write("capture: all readers started");
        return capture;
    }

    /// <summary>Starts a new step; events from here on belong to it.</summary>
    /// <param name="step">Step ID.</param>
    /// <param name="baseline">
    ///     Whether this step is the idle baseline, during which changing bytes are learned as noise and no
    ///     activity is raised.
    /// </param>
    public void BeginStep(string step, bool baseline = false)
    {
        lock (_gate)
        {
            _step = step;
            _baseline = baseline;
            _stepStarted = Now;
            _events.Clear();
            _dropped.Clear();
            _repeated.Clear();
            _noiseStored.Clear();
            _motion.Clear();
            if (baseline)
            {
                _noise.Clear();
            }
        }
    }

    /// <summary>Ends the current step and returns what it recorded.</summary>
    /// <returns>The step record.</returns>
    public LabInputStepRecord EndStep()
    {
        lock (_gate)
        {
            var record = new LabInputStepRecord
            {
                Step = _step,
                StartedMs = Math.Round(_stepStarted, 1),
                EndedMs = Math.Round(Now, 1),
                Events = [.. _events],
                Dropped = new Dictionary<string, int>(_dropped),
                Repeated = new Dictionary<string, int>(_repeated),
                PointerMotion = _motion.ToDictionary(pair => pair.Key, IReadOnlyList<long> (pair) => [.. pair.Value])
            };
            _motion.Clear();
            _step = "idle";
            _baseline = false;
            _events.Clear();
            _dropped.Clear();
            _repeated.Clear();
            return record;
        }
    }

    /// <summary>The bytes learned as noise per device during the last baseline.</summary>
    /// <returns>Offsets by device ID.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> NoiseMap()
    {
        lock (_gate)
        {
            return _noise.ToDictionary(pair => pair.Key, IReadOnlyList<int> (pair) => [.. pair.Value.Order()]);
        }
    }

    private void Record(LabInputEvent value, bool activity)
    {
        bool raise;
        lock (_gate)
        {
            if (_events.Count >= MaximumEventsPerStep)
            {
                _dropped[value.Source] = _dropped.GetValueOrDefault(value.Source) + 1;
            }
            else
            {
                _events.Add(value);
            }

            raise = activity && !_baseline;
        }

        if (raise)
        {
            Activity?.Invoke(new LabInputActivity(value.Source, value.Device, value.Detail));
        }
    }

    private void AddMotion(string device, int x, int y)
    {
        lock (_gate)
        {
            if (!_motion.TryGetValue(device, out var totals))
            {
                totals = _motion[device] = new long[3];
            }

            totals[0]++;
            totals[1] += x;
            totals[2] += y;
        }
    }

    private void MarkUnavailable(string source, string reason)
    {
        lock (_gate)
        {
            var entry = $"{source}: {reason}";
            if (!_unavailable.Contains(entry))
            {
                _unavailable.Add(entry);
            }
        }
    }

    private LabInputDevice AddDevice(LabInputDevice device)
    {
        lock (_gate)
        {
            _devices.Add(device);
        }

        return device;
    }

    internal string NextId(string prefix)
    {
        lock (_gate)
        {
            _deviceIds.TryGetValue(prefix, out var next);
            _deviceIds[prefix] = next + 1;
            return prefix + next;
        }
    }

    // A HID report: stored whole when a byte outside the baseline noise changed, sampled when only noise
    // changed, and counted when nothing changed.
    private void OnHidReport(LabInputDevice device, ReadOnlySpan<byte> report, string source = "raw-input")
    {
        var bytes = report.Length > MaximumReportBytes ? report[..MaximumReportBytes] : report;
        var key = device.Id + ":" + (bytes.Length > 0 ? bytes[0] : 0);
        List<int> changed = [];
        var noiseOnly = false;
        lock (_gate)
        {
            _lastReports.TryGetValue(key, out var previous);
            _noise.TryGetValue(device.Id, out var noise);
            if (previous is not null && previous.Length == bytes.Length)
            {
                var any = false;
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] == previous[i])
                    {
                        continue;
                    }

                    any = true;
                    if (_baseline)
                    {
                        (noise ??= _noise[device.Id] = []).Add(i);
                    }
                    else if (noise is null || !noise.Contains(i))
                    {
                        changed.Add(i);
                    }
                }

                if (!any)
                {
                    _repeated[device.Id] = _repeated.GetValueOrDefault(device.Id) + 1;
                    return;
                }

                noiseOnly = changed.Count == 0;
            }

            _lastReports[key] = bytes.ToArray();
            if (noiseOnly || _baseline)
            {
                var seen = _repeated[device.Id] = _repeated.GetValueOrDefault(device.Id) + 1;
                var stored = _noiseStored.GetValueOrDefault(device.Id);
                if (seen % NoiseSampleEvery != 1 || stored >= MaximumNoisePerDevice)
                {
                    return;
                }

                _noiseStored[device.Id] = stored + 1;
            }
        }

        var hex = Convert.ToHexString(bytes);
        Record(
            new LabInputEvent(Math.Round(Now, 2), source, device.Id,
                $"report {(bytes.Length > 0 ? bytes[0] : 0):X2}, {bytes.Length} bytes", hex,
                changed.Count > 0 ? changed : null, noiseOnly),
            !noiseOnly && changed.Count > 0);
    }

    // Vendor WMI event classes a shipping manager already subscribes to on its own devices, so enabling
    // them runs firmware paths that are exercised every day. Handheld Companion 1.3.1.6 subscribes to
    // MSI_Event on the Claw (ClawA1M.cs) and to nothing on ASUS, whose buttons arrive over HID.
    private static readonly string[] VendorWmiEvents = ["MSI_Event"];

    private void StartWmi()
    {
        // Never subscribe to every root\wmi event class. Each subscription makes Windows send the owning
        // driver an enable request, and on an ROG Xbox Ally X one of them completed that request twice:
        // bugcheck 0x44 in nt!WmipSendEnableDisableRequest from WmiPrvSE, three times on 2026-09-25.
        foreach (var name in VendorWmiEvents)
        {
            if (WmiClassExists(@"root\wmi", name))
            {
                Watch(@"root\wmi", $"SELECT * FROM {name}");
            }
        }

        Watch(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessEvent");
        Watch(@"root\cimv2", "SELECT * FROM Win32_PowerManagementEvent");
        Watch(@"root\cimv2", "SELECT * FROM Win32_DeviceChangeEvent");
    }

    private bool WmiClassExists(string scope, string name)
    {
        try
        {
            using ManagementObjectSearcher search = new(scope, $"SELECT * FROM meta_class WHERE __CLASS = '{name}'");
            using var classes = search.Get();
            return classes.Count > 0;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            MarkUnavailable($"wmi {scope} {name}", ex.Message);
            return false;
        }
    }

    private void Watch(string scope, string query)
    {
        ManagementEventWatcher? watcher = null;
        try
        {
            LabTrace.Write($"capture wmi watch {scope}: {query}");
            watcher = new ManagementEventWatcher(new ManagementScope(scope), new WqlEventQuery(query));
            watcher.EventArrived += (_, args) => OnWmiEvent(args.NewEvent);
            watcher.Start();
            _watchers.Add(watcher);
            LabTrace.Write($"capture wmi watch {scope}: started");
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            watcher?.Dispose();
            MarkUnavailable($"wmi {scope} {query}", ex.Message);
        }
    }

    private void OnWmiEvent(ManagementBaseObject instance)
    {
        try
        {
            var className = Convert.ToString(instance.ClassPath?.ClassName) ?? "unknown";
            List<string> properties = [];
            foreach (var property in instance.Properties)
            {
                if (properties.Count >= 24 || property.Value is null)
                {
                    continue;
                }

                var text = property.Value switch
                {
                    byte[] raw => Convert.ToHexString(raw.AsSpan(0, Math.Min(raw.Length, 128))),
                    Array array => string.Join(',', array.Cast<object?>().Take(32)),
                    _ => Convert.ToString(property.Value) ?? string.Empty
                };
                properties.Add($"{property.Name}={(text.Length > 256 ? text[..256] : text)}");
            }

            Record(new LabInputEvent(Math.Round(Now, 2), "wmi", null, $"{className}: {string.Join("; ", properties)}"),
                true);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException)
        {
            Record(new LabInputEvent(Math.Round(Now, 2), "wmi", null, $"unreadable event: {ex.Message}"), false);
        }
    }

    // Polls XInput and Windows.Gaming.Input every 4 ms. Only changes are stored.
    private void PollLoop()
    {
        DirectInputReader? directInput = null;
        var first = true;
        try
        {
            LabTrace.Write("capture directinput: create");
            directInput = new DirectInputReader(this, _directInputWindow);
            LabTrace.Write("capture directinput: created");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MarkUnavailable("directinput", ex.Message);
        }

        var packets = new uint[4];
        var results = new[] { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue };
        var pads = new XInputGamepad[4];
        var padIds = new string?[4];
        var guide = true;
        var xinputAvailable = true;
        Dictionary<RawGameController, (LabInputDevice Device, bool[] Buttons, int[] Switches, double[] Axes)>
            controllers = [];
        while (!_disposed)
        {
            if (first)
            {
                LabTrace.Write("capture xinput: first poll");
            }

            for (uint slot = 0; slot < 4; slot++)
            {
                if (!xinputAvailable)
                {
                    break;
                }

                uint result;
                XInputState state;
                try
                {
                    if (guide)
                    {
                        try
                        {
                            result = XInputGetStateEx(slot, out state);
                        }
                        catch (EntryPointNotFoundException)
                        {
                            guide = false;
                            MarkUnavailable("xinput guide", "ordinal 100 is absent; the guide button cannot be read");
                            result = XInputGetState(slot, out state);
                        }
                    }
                    else
                    {
                        result = XInputGetState(slot, out state);
                    }
                }
                catch (DllNotFoundException)
                {
                    MarkUnavailable("xinput", "xinput1_4.dll is missing");
                    xinputAvailable = false;
                    break;
                }

                if (result != results[slot])
                {
                    results[slot] = result;
                    if (result == 0)
                    {
                        padIds[slot] ??= AddDevice(XInputDevice(slot)).Id;
                    }

                    Record(new LabInputEvent(Math.Round(Now, 2), "xinput", padIds[slot],
                        result == 0 ? $"slot {slot} connected" : $"slot {slot} disconnected"), result == 0);
                }

                if (result != 0 || packets[slot] == state.Packet)
                {
                    continue;
                }

                packets[slot] = state.Packet;
                var pad = state.Gamepad;
                var before = pads[slot];
                var significant = Detailed
                                  || pad.Buttons != before.Buttons
                                  || Math.Abs(pad.LeftTrigger - before.LeftTrigger) > 24
                                  || Math.Abs(pad.RightTrigger - before.RightTrigger) > 24
                                  || Far(pad.ThumbLX, before.ThumbLX) || Far(pad.ThumbLY, before.ThumbLY)
                                  || Far(pad.ThumbRX, before.ThumbRX) || Far(pad.ThumbRY, before.ThumbRY);
                if (!significant)
                {
                    continue;
                }

                pads[slot] = pad;
                Record(new LabInputEvent(Math.Round(Now, 2), "xinput", padIds[slot],
                    $"buttons {pad.Buttons:X4} [{XInputButtonNames(pad.Buttons)}] LT {pad.LeftTrigger} RT {pad.RightTrigger} " +
                    $"L {pad.ThumbLX},{pad.ThumbLY} R {pad.ThumbRX},{pad.ThumbRY}"), true);
            }

            if (first)
            {
                LabTrace.Write("capture windows-gaming-input: first poll");
            }

            try
            {
                PollGameControllers(controllers);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TypeLoadException)
            {
                MarkUnavailable("windows-gaming-input", ex.Message);
                controllers.Clear();
                Thread.Sleep(1000);
            }

            if (first)
            {
                LabTrace.Write("capture directinput: first poll");
            }

            try
            {
                directInput?.Poll();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MarkUnavailable("directinput", ex.Message);
                directInput?.Dispose();
                directInput = null;
            }

            if (first)
            {
                LabTrace.Write("capture poll thread: first round done");
                first = false;
            }

            Thread.Sleep(4);
        }

        directInput?.Dispose();
    }

    private void PollGameControllers(
        Dictionary<RawGameController, (LabInputDevice Device, bool[] Buttons, int[] Switches, double[] Axes)>
            controllers)
    {
        var present = RawGameController.RawGameControllers;
        foreach (var controller in present)
        {
            var buttons = new bool[controller.ButtonCount];
            var switches = new GameControllerSwitchPosition[controller.SwitchCount];
            var axes = new double[controller.AxisCount];
            controller.GetCurrentReading(buttons, switches, axes);
            var switchValues = switches.Select(position => (int)position).ToArray();
            if (!controllers.TryGetValue(controller, out var previous))
            {
                var gamepad = Gamepad.FromGameController(controller) is not null;
                var device = AddDevice(new LabInputDevice(NextId("wgi"), "wgi",
                    controller.HardwareVendorId.ToString("X4"), controller.HardwareProductId.ToString("X4"),
                    null, null, null, !controller.IsWireless && controller.HardwareVendorId == 0,
                    $"{controller.DisplayName} ({controller.ButtonCount} buttons, {controller.AxisCount} axes, {controller.SwitchCount} switches{(gamepad ? ", gamepad" : string.Empty)})"));
                controllers[controller] = (device, buttons, switchValues, axes);
                Record(new LabInputEvent(Math.Round(Now, 2), "wgi", device.Id, $"controller present: {device.Name}"),
                    false);
                continue;
            }

            var buttonChanged = !buttons.SequenceEqual(previous.Buttons);
            var switchChanged = !switchValues.SequenceEqual(previous.Switches);
            var threshold = Detailed ? 0.005 : 0.15;
            var axisMoved = axes.Length == previous.Axes.Length
                            && axes.Where((value, i) => Math.Abs(value - previous.Axes[i]) > threshold).Any();
            if (!buttonChanged && !switchChanged && !axisMoved)
            {
                continue;
            }

            controllers[controller] = (previous.Device, buttons, switchValues, axes);
            var pressed = string.Join(",", buttons.Select((down, i) => (down, i)).Where(item => item.down)
                .Select(item => item.i));
            Record(new LabInputEvent(Math.Round(Now, 2), "wgi", previous.Device.Id,
                    $"buttons [{pressed}] switches [{string.Join(",", switchValues)}] axes [{string.Join(",", axes.Select(axis => axis.ToString("0.00")))}]"),
                true);
        }
    }

    private LabInputDevice XInputDevice(uint slot)
    {
        string? vendor = null;
        string? product = null;
        var detail = "no extended capabilities";
        try
        {
            if (XInputGetCapabilitiesEx(1, slot, 0, out var caps) == 0)
            {
                vendor = caps.VendorId.ToString("X4");
                product = caps.ProductId.ToString("X4");
                detail =
                    $"type {caps.Type}, subtype {caps.SubType}, flags {caps.Flags:X4}, version {caps.ProductVersion:X4}";
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Older XInput has no extended capabilities; the slot is still recorded.
        }

        return new LabInputDevice(NextId("xinput"), "xinput", vendor, product, null, null, null, false,
            $"XInput slot {slot} ({detail})");
    }

    private static bool Far(short now, short before)
    {
        return Math.Abs(now - before) > 6000;
    }

    /// <summary>Names the XInput buttons set in a mask.</summary>
    /// <param name="buttons">XInput button mask.</param>
    /// <returns>Comma-separated names.</returns>
    public static string XInputButtonNames(ushort buttons)
    {
        (ushort Bit, string Name)[] names =
        [
            (0x0001, "Up"), (0x0002, "Down"), (0x0004, "Left"), (0x0008, "Right"), (0x0010, "Menu"),
            (0x0020, "View"), (0x0040, "LS"), (0x0080, "RS"), (0x0100, "LB"), (0x0200, "RB"),
            (0x0400, "Guide"), (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")
        ];
        return string.Join(",", names.Where(item => (buttons & item.Bit) != 0).Select(item => item.Name));
    }
}
