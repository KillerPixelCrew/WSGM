using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Capture.Live;

// The message-only window, Raw Input, low-level hooks and power notifications. Everything here runs on
// the capture's own message thread.
internal sealed partial class LabInputCapture
{
    private const uint WmQuit = 0x0012;
    private const uint WmInput = 0x00FF;
    private const uint WmInputDeviceChange = 0x00FE;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmAppCommand = 0x0319;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevPageOnly = 0x00000020;
    private const uint RidevDevNotify = 0x00002000;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const int HeaderSize = 24;

    private static readonly (string Name, Guid Setting)[] PowerSettings =
    [
        ("power-source", new Guid("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548")),
        ("display-state", new Guid("6fe69556-704a-47a0-8f24-c28d936fda47")),
        ("lid-switch", new Guid("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3")),
        ("user-presence", new Guid("3c0f4548-c03f-4c4d-b9f2-237ede686376")),
        ("power-scheme", new Guid("245d8541-3afd-4b76-9239-3ba91b4b5a1f")),
        ("battery-saver", new Guid("e00958c0-c213-4ace-ac77-fecced2eeea5"))
    ];

    private readonly List<IntPtr> _notifications = [];
    private bool _altHeld;
    private IntPtr _keyboardHook;
    private HookProc? _keyboardProc;
    private IntPtr _mouseHook;
    private HookProc? _mouseProc;
    private uint _shellMessage;
    private IntPtr _window;
    private WndProc? _windowProc;
    private bool _windowsHeld;

    private void MessageLoop()
    {
        try
        {
            _messageThreadId = GetCurrentThreadId();
            _windowProc = WindowProc;
            var className = "WSGM.DeviceLab.Input." + Environment.ProcessId;
            WindowClass windowClass = new()
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
                Instance = GetModuleHandle(null),
                ClassName = className
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                MarkUnavailable("message window", $"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
                _ready.Set();
                return;
            }

            _window = CreateWindowEx(0, className, "Device Lab input", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero,
                windowClass.Instance, IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                MarkUnavailable("message window", $"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
                _ready.Set();
                return;
            }

            RegisterRawInput();
            _keyboardProc = KeyboardHook;
            _mouseProc = MouseHook;
            LabTrace.Write("capture hooks: keyboard and mouse");
            _keyboardHook = SetWindowsHookEx(13, _keyboardProc, GetModuleHandle(null), 0);
            _mouseHook = SetWindowsHookEx(14, _mouseProc, GetModuleHandle(null), 0);
            LabTrace.Write("capture power, suspend and shell notifications");
            if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            {
                MarkUnavailable("low-level hooks", $"SetWindowsHookEx failed ({Marshal.GetLastWin32Error()})");
            }

            foreach (var (name, setting) in PowerSettings)
            {
                var copy = setting;
                var handle = RegisterPowerSettingNotification(_window, ref copy, 0);
                if (handle == IntPtr.Zero)
                {
                    MarkUnavailable($"power setting {name}", $"error {Marshal.GetLastWin32Error()}");
                }
                else
                {
                    _notifications.Add(handle);
                }
            }

            var suspend = RegisterSuspendResumeNotification(_window, 0);
            if (suspend == IntPtr.Zero)
            {
                MarkUnavailable("suspend and resume", $"error {Marshal.GetLastWin32Error()}");
            }

            _shellMessage = RegisterWindowMessage("SHELLHOOK");
            if (!RegisterShellHookWindow(_window))
            {
                _shellMessage = 0;
                MarkUnavailable("shell app commands",
                    $"RegisterShellHookWindow failed ({Marshal.GetLastWin32Error()})");
            }

            LabTrace.Write("capture message thread: ready");
            _ready.Set();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
            }

            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
            }

            foreach (var handle in _notifications)
            {
                UnregisterPowerSettingNotification(handle);
            }

            if (suspend != IntPtr.Zero)
            {
                UnregisterSuspendResumeNotification(suspend);
            }

            if (_shellMessage != 0)
            {
                DeregisterShellHookWindow(_window);
            }

            DestroyWindow(_window);
            UnregisterClass(className, windowClass.Instance);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MarkUnavailable("message thread", ex.Message);
            _ready.Set();
        }
    }

    // Registers the standard desktop usages and consumer controls explicitly, then every other usage page
    // present on the machine page-wide, so no collection is left out.
    private void RegisterRawInput()
    {
        LabTrace.Write("capture raw input: list devices");
        List<RawInputDeviceRegistration> registrations =
        [
            .. new (ushort Page, ushort Usage)[]
            {
                (1, 1), (1, 2), (1, 4), (1, 5), (1, 6), (1, 7), (1, 8), (1, 0x80), (0x0C, 1)
            }.Select(item => new RawInputDeviceRegistration
            {
                UsagePage = item.Page,
                Usage = item.Usage,
                Flags = RidevInputSink | RidevDevNotify,
                Target = _window
            })
        ];
        HashSet<ushort> pages = [];
        foreach (var handle in RawInputDevices())
        {
            if (Describe(handle) is { Kind: "hid", UsagePage: { } page } && page is not 1 and not 0x0C)
            {
                pages.Add((ushort)page);
            }
        }

        registrations.AddRange(pages.Order().Select(page => new RawInputDeviceRegistration
        {
            UsagePage = page,
            Usage = 0,
            Flags = RidevInputSink | RidevDevNotify | RidevPageOnly,
            Target = _window
        }));

        // One refused page must not cost the others, so each is registered on its own after a combined
        // attempt fails.
        var array = registrations.ToArray();
        LabTrace.Write("capture raw input: register " + string.Join(", ",
            array.Select(registration => $"{registration.UsagePage:X4}:{registration.Usage:X4}")));
        if (!RegisterRawInputDevices(array, (uint)array.Length, (uint)Marshal.SizeOf<RawInputDeviceRegistration>()))
        {
            LabTrace.Write("capture raw input: combined registration refused, registering one by one");
            foreach (var registration in array)
            {
                RawInputDeviceRegistration[] one = [registration];
                LabTrace.Write($"capture raw input: register {registration.UsagePage:X4}:{registration.Usage:X4}");
                if (!RegisterRawInputDevices(one, 1, (uint)Marshal.SizeOf<RawInputDeviceRegistration>()))
                {
                    MarkUnavailable($"raw input {registration.UsagePage:X4}:{registration.Usage:X4}",
                        $"error {Marshal.GetLastWin32Error()}");
                }
            }
        }
    }

    private static IEnumerable<IntPtr> RawInputDevices()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<RawInputDeviceListEntry>();
        if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0)
        {
            return [];
        }

        var list = new RawInputDeviceListEntry[count];
        var read = GetRawInputDeviceList(list, ref count, size);
        return read == uint.MaxValue ? [] : list.Take((int)read).Select(entry => entry.Device).ToArray();
    }

    /// <summary>
    ///     Lists the Raw Input devices present right now as <c>kind VID:PID page:usage</c>, without caching,
    ///     so two calls can be compared across sleep.
    /// </summary>
    /// <returns>One key per device, sorted.</returns>
    public static IReadOnlyList<string> PresentDeviceKeys()
    {
        List<string> keys = [];
        foreach (var handle in RawInputDevices())
        {
            RawDeviceInfo info = new() { Size = (uint)Marshal.SizeOf<RawDeviceInfo>() };
            var size = info.Size;
            if (GetRawInputDeviceInfo(handle, RidiDeviceInfo, ref info, ref size) == uint.MaxValue)
            {
                continue;
            }

            keys.Add(info.Type switch
            {
                0 => "mouse",
                1 => "keyboard",
                _ => $"hid {info.VendorId:X4}:{info.ProductId:X4} {info.UsagePage:X4}:{info.Usage:X4}"
            });
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>Which XInput slots have a controller right now.</summary>
    /// <returns>Connected slot numbers.</returns>
    public static IReadOnlyList<int> ConnectedXInputSlots()
    {
        List<int> slots = [];
        try
        {
            for (uint slot = 0; slot < 4; slot++)
            {
                if (XInputGetState(slot, out _) == 0)
                {
                    slots.Add((int)slot);
                }
            }
        }
        catch (DllNotFoundException)
        {
            // No XInput on this machine; no slots.
        }

        return slots;
    }

    private LabInputDevice Describe(IntPtr handle)
    {
        lock (_gate)
        {
            if (_byHandle.TryGetValue(handle, out var known))
            {
                return known;
            }
        }

        var path = string.Empty;
        if (handle != IntPtr.Zero)
        {
            uint length = 0;
            GetRawInputDeviceInfo(handle, RidiDeviceName, IntPtr.Zero, ref length);
            if (length is > 0 and < 4096)
            {
                var buffer = Marshal.AllocHGlobal((int)length * 2 + 2);
                try
                {
                    if (GetRawInputDeviceInfo(handle, RidiDeviceName, buffer, ref length) != uint.MaxValue)
                    {
                        path = Marshal.PtrToStringUni(buffer) ?? string.Empty;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        RawDeviceInfo info = new() { Size = (uint)Marshal.SizeOf<RawDeviceInfo>() };
        var infoSize = info.Size;
        var haveInfo = handle != IntPtr.Zero
                       && GetRawInputDeviceInfo(handle, RidiDeviceInfo, ref info, ref infoSize) != uint.MaxValue;
        var kind = !haveInfo ? "injected" : info.Type switch { 0 => "mouse", 1 => "keyboard", _ => "hid" };
        var hid = haveInfo && info.Type == 2;
        var vendor = hid ? info.VendorId.ToString("X4") : Hex(path, "VID_");
        var product = hid ? info.ProductId.ToString("X4") : Hex(path, "PID_");
        var isVirtual = handle == IntPtr.Zero
                        || path.Contains(@"\ROOT#", StringComparison.OrdinalIgnoreCase)
                        || path.Contains(@"\SWD#", StringComparison.OrdinalIgnoreCase)
                        || vendor is null or "0000";
        LabInputDevice device;
        lock (_gate)
        {
            if (_byHandle.TryGetValue(handle, out var raced))
            {
                return raced;
            }

            var prefix = kind == "injected" ? "injected" : kind == "hid" ? "hid" : kind == "keyboard" ? "kbd" : "mouse";
            device = new LabInputDevice(
                NextId(prefix),
                kind, vendor, product,
                hid ? info.UsagePage : null, hid ? info.Usage : null,
                path.Length == 0 ? null : path, isVirtual);
            _byHandle[handle] = device;
            _devices.Add(device);
        }

        return device;
    }

    private static string? Hex(string path, string marker)
    {
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && at + marker.Length + 4 <= path.Length
            ? path.Substring(at + marker.Length, 4).ToUpperInvariant()
            : null;
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case WmInput:
                    ReadRawInput(lParam);
                    break;
                case WmInputDeviceChange:
                {
                    var arrived = wParam.ToInt64() == 1;
                    var device = Describe(lParam);
                    Record(new LabInputEvent(Math.Round(Now, 2), "device", device.Id,
                            $"{(arrived ? "arrived" : "removed")}: {device.Kind} {device.VendorId}:{device.ProductId} {device.UsagePage:X4}:{device.Usage:X4}"),
                        false);
                    DeviceChanged?.Invoke(device, arrived);
                    if (arrived)
                    {
                        QueueHidRescan();
                    }
                    break;
                }
                case WmPowerBroadcast:
                    OnPowerBroadcast(wParam.ToInt64(), lParam);
                    return new IntPtr(1);
                case WmAppCommand:
                    Record(new LabInputEvent(Math.Round(Now, 2), "app-command", null,
                        $"app command {(lParam.ToInt64() >> 16) & 0x0FFF}"), true);
                    break;
                default:
                    if (_shellMessage != 0 && message == _shellMessage && wParam.ToInt64() == 12)
                    {
                        Record(new LabInputEvent(Math.Round(Now, 2), "app-command", null,
                            $"app command {(lParam.ToInt64() >> 16) & 0x0FFF} (shell)"), true);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record(new LabInputEvent(Math.Round(Now, 2), "error", null, $"message {message:X4}: {ex.Message}"), false);
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void OnPowerBroadcast(long kind, IntPtr lParam)
    {
        switch (kind)
        {
            case 0x8013:
            {
                var setting = Marshal.PtrToStructure<Guid>(lParam);
                var length = Marshal.ReadInt32(lParam + 16);
                var value = length is > 0 and <= 4 ? Marshal.ReadInt32(lParam + 20) : -1;
                var name = PowerSettings.FirstOrDefault(item => item.Setting == setting).Name ?? setting.ToString();
                Record(new LabInputEvent(Math.Round(Now, 2), "power", null, $"{name} = {value}"), true);
                break;
            }
            case 4:
                Record(new LabInputEvent(Math.Round(Now, 2), "power", null, "suspending"), true);
                SuspendResume?.Invoke(true);
                break;
            case 7:
            case 0x12:
                Record(new LabInputEvent(Math.Round(Now, 2), "power", null,
                    kind == 7 ? "resumed by the user" : "resumed"), true);
                SuspendResume?.Invoke(false);
                break;
            default:
                Record(new LabInputEvent(Math.Round(Now, 2), "power", null, $"power broadcast {kind}"), true);
                break;
        }
    }

    private void ReadRawInput(IntPtr handle)
    {
        uint size = 0;
        if (GetRawInputData(handle, RidInput, IntPtr.Zero, ref size, HeaderSize) != 0 || size is 0 or > 65536)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle, RidInput, buffer, ref size, HeaderSize) == uint.MaxValue)
            {
                return;
            }

            var type = Marshal.ReadInt32(buffer);
            var device = Describe(Marshal.ReadIntPtr(buffer + 8));
            var body = buffer + HeaderSize;
            switch (type)
            {
                case 0:
                {
                    var buttons = (ushort)Marshal.ReadInt16(body + 4);
                    if (buttons != 0 || Detailed)
                    {
                        Record(new LabInputEvent(Math.Round(Now, 2), "raw-input", device.Id,
                                $"mouse buttons {buttons:X4} data {Marshal.ReadInt16(body + 6)} move {Marshal.ReadInt32(body + 12)},{Marshal.ReadInt32(body + 16)}"),
                            buttons != 0);
                    }
                    else
                    {
                        AddMotion(device.Id, Marshal.ReadInt32(body + 12), Marshal.ReadInt32(body + 16));
                    }

                    break;
                }
                case 1:
                {
                    var scan = (ushort)Marshal.ReadInt16(body);
                    var flags = (ushort)Marshal.ReadInt16(body + 2);
                    var key = (ushort)Marshal.ReadInt16(body + 6);
                    Record(new LabInputEvent(Math.Round(Now, 2), "raw-input", device.Id,
                            $"key {LabKeyNames.Name(key)} (VK {key:X2}, scan {scan:X2}{((flags & 2) != 0 ? " E0" : string.Empty)}) {((flags & 1) != 0 ? "up" : "down")}"),
                        true);
                    break;
                }
                default:
                {
                    var reportSize = Marshal.ReadInt32(body);
                    var count = Marshal.ReadInt32(body + 4);
                    if (reportSize <= 0 || count <= 0 || (long)reportSize * count > size - HeaderSize - 8)
                    {
                        return;
                    }

                    var data = new byte[reportSize];
                    for (var i = 0; i < count; i++)
                    {
                        Marshal.Copy(body + 8 + i * reportSize, data, 0, reportSize);
                        OnHidReport(device, data);
                    }

                    break;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = (uint)Marshal.ReadInt32(lParam);
            var scan = (uint)Marshal.ReadInt32(lParam + 4);
            var flags = (uint)Marshal.ReadInt32(lParam + 8);
            var up = (flags & 0x80) != 0;
            var swallow = SwallowShortcuts && Shortcut((ushort)key, up);
            Record(new LabInputEvent(Math.Round(Now, 2), "hook", (flags & 0x10) != 0 ? "injected" : null,
                    $"key {LabKeyNames.Name((ushort)key)} (VK {key:X2}, scan {scan:X2}{((flags & 0x01) != 0 ? " E0" : string.Empty)}, flags {flags:X2}) {(up ? "up" : "down")}{((flags & 0x10) != 0 ? " injected" : string.Empty)}{((flags & 0x02) != 0 ? " lower-integrity" : string.Empty)}{(swallow ? " swallowed" : string.Empty)}"),
                true);
            if (swallow)
            {
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // Tracks the Windows and Alt keys from the hook's own events. A Windows key is swallowed on both
    // edges, and any key while it is held, so the shell never sees half a chord.
    private bool Shortcut(ushort key, bool up)
    {
        switch (key)
        {
            case 0x5B or 0x5C:
                _windowsHeld = !up;
                return true;
            case 0xA4 or 0xA5 or 0x12:
                _altHeld = !up;
                return false;
            case 0x09 when _altHeld:
                return true;
            default:
                return _windowsHeld;
        }
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        // Movement is counted, not stored; buttons and wheels are stored.
        if (code >= 0)
        {
            var message = wParam.ToInt64();
            if (message == 0x0200 && !Detailed)
            {
                lock (_gate)
                {
                    _repeated["hook-mouse-move"] = _repeated.GetValueOrDefault("hook-mouse-move") + 1;
                }
            }
            else
            {
                var flags = (uint)Marshal.ReadInt32(lParam + 12);
                Record(new LabInputEvent(Math.Round(Now, 2), "hook", (flags & 1) != 0 ? "injected" : null,
                        $"mouse message {message:X4} data {Marshal.ReadInt32(lParam + 8) >> 16}{((flags & 1) != 0 ? " injected" : string.Empty)}"),
                    true);
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint slot, out XInputState state);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint slot, out XInputState state);

    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern uint XInputGetCapabilitiesEx(uint one, uint slot, uint flags, out XInputCapabilitiesEx caps);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDeviceRegistration[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(RawInputDeviceListEntry[]? list, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, ref RawDeviceInfo data,
        ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size,
        uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterShellHookWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool DeregisterShellHookWindow(IntPtr window);

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceRegistration
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceListEntry
    {
        public IntPtr Device;
        public uint Type;
    }

    // RID_DEVICE_INFO with the HID member of the union; the keyboard and mouse members are the same size
    // or smaller, and only the type is read for them.
    [StructLayout(LayoutKind.Sequential)]
    private struct RawDeviceInfo
    {
        public uint Size;
        public uint Type;
        public uint VendorId;
        public uint ProductId;
        public uint VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
        private readonly uint _padding1;
        private readonly uint _padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
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
    private struct XInputState
    {
        public uint Packet;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilitiesEx
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public ushort LeftMotor;
        public ushort RightMotor;
        public ushort VendorId;
        public ushort ProductId;
        public ushort ProductVersion;
        public ushort Unknown1;
        public uint Unknown2;
    }
}
