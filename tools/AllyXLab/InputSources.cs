using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.AllyXLab;

/// <summary>One thing that happened on an input source, reported to the step runner.</summary>
internal readonly record struct InputActivity(string Source, string Detail);

/// <summary>
/// Listens to every input channel Windows offers at once and records all of them: Raw Input from
/// every HID, keyboard and mouse device (physical and virtual), low-level keyboard and mouse hooks
/// (which see injected input), XInput including the guide button, Windows.Gaming.Input, shell app
/// commands, WMI firmware and ACPI events, power settings and device arrival. Nothing is suppressed.
/// </summary>
/// <remarks>Runs on the wizard's UI thread, which owns the message loop the hooks and Raw Input need.</remarks>
internal sealed partial class InputSources : NativeWindow, IDisposable
{
    private readonly SessionLog _log;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<IntPtr, DeviceInfo> _devices = [];
    private readonly Dictionary<string, byte[]> _lastReports = [];
    private readonly Dictionary<string, HashSet<int>> _noise = [];
    private readonly Dictionary<string, int> _reportCounts = [];
    private readonly HookCallback _keyboardCallback, _mouseCallback;
    private IntPtr _keyboardHook, _mouseHook;
    private bool _baseline;
    private const int ReportLogCap = 6000;

    /// <summary>Raised on the UI thread for anything a person could have caused.</summary>
    internal event Action<InputActivity>? Activity;

    private sealed record DeviceInfo(string Token, string Kind, int Vid, int Pid, int Page, int Usage, bool Virtual);

    internal InputSources(SessionLog log, IEnumerable<HidEndpoint> hidCollections)
    {
        _log = log;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Input capture needs the UI thread.");
        _keyboardCallback = OnKeyboardHook;
        _mouseCallback = OnMouseHook;
        CreateHandle(new CreateParams { Caption = "AllyXLab input", Parent = IntPtr.Zero });

        // Generic desktop usages plus consumer controls, and every vendor page present on the machine.
        var usages = new List<(ushort Page, ushort Usage, uint Flags)>
        {
            (1, 2, 0), (1, 4, 0), (1, 5, 0), (1, 6, 0), (1, 7, 0), (1, 8, 0), (1, 0x80, 0), (0x0C, 1, 0),
        };
        foreach (ushort page in hidCollections.Select(e => e.Page).Where(page => page >= 0xFF00).Distinct())
        {
            usages.Add((page, 0, PageOnly));
        }

        var registrations = usages.Select(u => new Registration { Page = u.Page, Usage = u.Usage, Flags = InputSink | DeviceNotify | u.Flags, Window = Handle }).ToArray();
        if (!RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<Registration>()))
        {
            _log.Add("source-unavailable", new { Source = "raw-input", Error = Marshal.GetLastWin32Error() });
        }

        _keyboardHook = SetWindowsHookEx(13, _keyboardCallback, GetModuleHandle(null), 0);
        _mouseHook = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            _log.Add("source-unavailable", new { Source = "low-level-hooks", Error = Marshal.GetLastWin32Error() });
        }

        StartSystemSources();
        StartPolledSources();
        _log.Add("capture-scope", new
        {
            RawInput = registrations.Select(r => $"{r.Page:X4}:{r.Usage:X4}").ToArray(),
            Hooks = "Low-level keyboard and mouse, including injected flags. Delivery is never modified.",
            Polled = "XInput slots 0-3 with guide button, Windows.Gaming.Input raw controllers and gamepads",
            System = "WMI extrinsic events (root\\wmi, device and power classes), shell app commands, power settings, device arrival",
            Privacy = "Key codes only while the wizard asks for a press; device paths are hashed.",
        });
    }

    /// <summary>While the baseline runs, bytes that change on their own are learned as noise.</summary>
    internal void Baseline(bool active) => _baseline = active;

    private void Report(string source, string detail)
    {
        if (!_baseline)
        {
            Activity?.Invoke(new(source, detail));
        }
    }

    protected override void WndProc(ref Message message)
    {
        try
        {
            switch (message.Msg)
            {
                case 0x00FF: ReadRawInput(message.LParam, ReadRawInputBody); break;
                case 0x00FE: LogDeviceChange(message.WParam.ToInt32(), message.LParam); break;
                default: HandleSystemMessage(ref message); break;
            }
        }
        catch (Exception e) { _log.Add("source-error", new { WindowMessage = message.Msg, Error = e.Message }); }
        base.WndProc(ref message);
    }

    private DeviceInfo Describe(IntPtr device)
    {
        if (_devices.TryGetValue(device, out var known))
        {
            return known;
        }

        string path = "";
        uint length = 0;
        if (device != IntPtr.Zero && GetRawInputDeviceInfo(device, 0x20000007, null, ref length) == 0 && length is > 0 and < 8192)
        {
            var name = new StringBuilder((int)length + 1);
            GetRawInputDeviceInfo(device, 0x20000007, name, ref length);
            path = name.ToString();
        }

        var info = new RawDeviceInfo { Size = (uint)Marshal.SizeOf<RawDeviceInfo>() };
        uint size = info.Size;
        bool haveInfo = device != IntPtr.Zero && GetRawInputDeviceInfo(device, 0x2000000B, ref info, ref size) != uint.MaxValue;
        int vid = haveInfo && info.Type == 2 ? (int)info.Vendor : ParseHex(path, "VID_");
        int pid = haveInfo && info.Type == 2 ? (int)info.Product : ParseHex(path, "PID_");
        string kind = info.Type switch { 0 => "mouse", 1 => "keyboard", _ => "hid" };
        // No device handle means SendInput; ROOT and SWD enumerators are software devices.
        bool virtualDevice = device == IntPtr.Zero || path.Contains(@"\ROOT#", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\SWD#", StringComparison.OrdinalIgnoreCase) || (vid == 0 && pid == 0);
        var described = new DeviceInfo(device == IntPtr.Zero ? "injected" : SessionLog.Token(path.ToUpperInvariant()), kind, vid, pid,
            haveInfo && info.Type == 2 ? info.UsagePage : 0, haveInfo && info.Type == 2 ? info.Usage : 0, virtualDevice);
        _devices[device] = described;
        _log.Add("input-device", described);
        return described;
    }

    private static int ParseHex(string path, string marker)
    {
        int at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && at + marker.Length + 4 <= path.Length
            && int.TryParse(path.AsSpan(at + marker.Length, 4), System.Globalization.NumberStyles.HexNumber, null, out int value) ? value : 0;
    }

    private void LogDeviceChange(int change, IntPtr device)
    {
        _devices.Remove(device);
        DeviceInfo info = Describe(device);
        _log.Add("input-device-change", new { Change = change == 1 ? "arrived" : "removed", info.Token, info.Kind, info.Vid, info.Pid });
        Report("device-change", $"{info.Kind} {info.Vid:X4}:{info.Pid:X4} {(change == 1 ? "arrived" : "removed")}");
    }

    internal static void ReadRawInput(IntPtr raw, Action<Header, IntPtr, int> readBody)
    {
        uint size = 0, headerSize = (uint)Marshal.SizeOf<Header>();
        if (GetRawInputData(raw, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize || size > 65536)
        {
            return;
        }

        IntPtr memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(raw, 0x10000003, memory, ref size, headerSize) != size)
            {
                return;
            }

            Header header = Marshal.PtrToStructure<Header>(memory);
            IntPtr body = memory + (int)headerSize;
            int length = (int)(size - headerSize);
            readBody(header, body, length);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private void ReadRawInputBody(Header header, IntPtr body, int length)
    {
        DeviceInfo device = Describe(header.Device);
        if (header.Type == 1 && length >= 16)
        {
            ushort scan = (ushort)Marshal.ReadInt16(body), flags = (ushort)Marshal.ReadInt16(body, 2), key = (ushort)Marshal.ReadInt16(body, 6);
            uint msg = (uint)Marshal.ReadInt32(body, 8);
            _log.Add("raw-keyboard", new { Device = device.Token, device.Vid, device.Pid, device.Virtual, ScanCode = scan, Flags = flags, VirtualKey = key, Message = msg });
            Report("raw-keyboard", $"key 0x{key:X2} {(msg == 0x100 || msg == 0x104 ? "down" : "up")} from {Name(device)}");
        }
        else if (header.Type == 0 && length >= 24)
        {
            ReadRawMouse(device, body);
        }
        else if (header.Type == 2 && length >= 8)
        {
            ReadRawHid(body, length, report => ProcessRawHid(device, report));
        }
    }

    private static string Name(DeviceInfo device) => device.Virtual ? $"virtual {device.Kind}" : $"{device.Kind} {device.Vid:X4}:{device.Pid:X4}";

    private readonly Dictionary<string, (int X, int Y, double Since)> _mouseMotion = [];
    private void ReadRawMouse(DeviceInfo device, IntPtr body)
    {
        ushort buttons = (ushort)Marshal.ReadInt16(body, 4), data = (ushort)Marshal.ReadInt16(body, 6);
        int x = Marshal.ReadInt32(body, 12), y = Marshal.ReadInt32(body, 16);
        if (buttons != 0)
        {
            _log.Add("raw-mouse-button", new { Device = device.Token, device.Vid, device.Pid, device.Virtual, ButtonFlags = buttons, ButtonData = (short)data });
            Report("raw-mouse", $"mouse buttons 0x{buttons:X4} from {Name(device)}");
        }

        if (x == 0 && y == 0)
        {
            return;
        }

        // Motion is summarized per quarter second instead of logged at device rate.
        (int X, int Y, double Since) motion = _mouseMotion.TryGetValue(device.Token, out var pending) ? pending : (0, 0, _log.Now);
        motion = (motion.X + x, motion.Y + y, motion.Since);
        if (_log.Now - motion.Since >= 250)
        {
            _log.Add("raw-mouse-motion", new { Device = device.Token, device.Vid, device.Pid, device.Virtual, DeltaX = motion.X, DeltaY = motion.Y });
            if (Math.Abs(motion.X) + Math.Abs(motion.Y) > 40)
            {
                Report("raw-mouse", $"pointer moved by {Name(device)}");
            }

            motion = (0, 0, _log.Now);
        }
        _mouseMotion[device.Token] = motion;
    }

    internal static void ReadRawHid(IntPtr body, int length, Action<byte[]> readReport)
    {
        int bytes = Marshal.ReadInt32(body), count = Marshal.ReadInt32(body, 4);
        if (bytes <= 0 || bytes > 4096 || count < 0 || count > (length - 8) / bytes)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            byte[] report = new byte[bytes];
            Marshal.Copy(body + 8 + i * bytes, report, 0, bytes);
            readReport(report);
        }
    }

    private void ProcessRawHid(DeviceInfo device, byte[] report)
    {
        string key = $"{device.Token}/{device.Page:X4}:{device.Usage:X4}/{report[0]}";
        byte[]? last = _lastReports.GetValueOrDefault(key);
        if (last is not null && report.AsSpan().SequenceEqual(last))
        {
            return;
        }

        _lastReports[key] = report;
        int[] changed = last is null ? [] : Enumerable.Range(0, Math.Min(last.Length, report.Length)).Where(j => last[j] != report[j]).ToArray();
        HashSet<int> noise = _noise.TryGetValue(key, out var known) ? known : _noise[key] = [];
        if (_baseline)
        {
            noise.UnionWith(changed);
        }

        int logged = _reportCounts.GetValueOrDefault(key);
        if (logged < ReportLogCap)
        {
            _reportCounts[key] = logged + 1;
            _log.Add("raw-hid", new { Device = device.Token, device.Vid, device.Pid, device.Page, device.Usage, device.Virtual, ReportId = report[0], Hex = Convert.ToHexString(report), ChangedBytes = changed, Baseline = _baseline });
        }
        else if (logged == ReportLogCap)
        {
            _reportCounts[key] = logged + 1;
            _log.Add("raw-hid-capped", new { Device = device.Token, device.Page, device.Usage, ReportId = report[0], Cap = ReportLogCap });
        }

        if (last is null || changed.Any(j => !noise.Contains(j)))
        {
            Report("raw-hid", $"HID {device.Vid:X4}:{device.Pid:X4} {device.Page:X4}:{device.Usage:X4} report 0x{report[0]:X2}");
        }
    }

    private IntPtr OnKeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            try
            {
                int key = Marshal.ReadInt32(data), scan = Marshal.ReadInt32(data, 4), flags = Marshal.ReadInt32(data, 8);
                _log.Add("hook-keyboard", new { VirtualKey = key, ScanCode = scan, Flags = flags, Injected = (flags & 0x10) != 0, LowerIntegrityInjected = (flags & 0x02) != 0, Message = message.ToInt32() });
                Report("hook-keyboard", $"key 0x{key:X2}{((flags & 0x10) != 0 ? " (injected)" : "")}");
            }
            catch (InvalidOperationException) { }
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private IntPtr OnMouseHook(int code, IntPtr message, IntPtr data)
    {
        // Pointer motion is left to Raw Input's summary; buttons and wheels are logged here with their injected flags.
        if (code >= 0 && message.ToInt32() is not 0x0200)
        {
            try
            {
                int mouseData = Marshal.ReadInt32(data, 8), flags = Marshal.ReadInt32(data, 12);
                _log.Add("hook-mouse", new { Message = message.ToInt32(), MouseData = mouseData, Flags = flags, Injected = (flags & 0x01) != 0, LowerIntegrityInjected = (flags & 0x02) != 0 });
                Report("hook-mouse", $"mouse message 0x{message.ToInt32():X3}{((flags & 0x01) != 0 ? " (injected)" : "")}");
            }
            catch (InvalidOperationException) { }
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    public void Dispose()
    {
        StopPolledSources();
        StopSystemSources();
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        DestroyHandle();
    }

    private const uint InputSink = 0x100, PageOnly = 0x20, DeviceNotify = 0x2000;
    internal delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] internal struct Registration { internal ushort Page, Usage; internal uint Flags; internal IntPtr Window; }
    [StructLayout(LayoutKind.Sequential)] internal struct Header { internal uint Type, Size; internal IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RawDeviceInfo { internal uint Size, Type, Vendor, Product, Version; internal ushort UsagePage, Usage; internal uint Padding; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterRawInputDevices(Registration[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, ref RawDeviceInfo data, ref uint size);
}
