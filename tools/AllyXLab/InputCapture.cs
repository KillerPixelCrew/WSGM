using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.AllyXLab;

internal sealed class InputCapture : NativeWindow, IDisposable
{
    private readonly SessionLog _log;
    private readonly Dictionary<IntPtr, string?> _devices = [];
    private readonly Dictionary<string, byte[]> _baseline = [];
    private readonly Dictionary<string, byte[]> _lastReports = [];
    private readonly uint[] _packets = new uint[4];
    private readonly bool[] _seen = new bool[4];
    private int _rawCount, _duplicates;
    private readonly InputSources.HookCallback _callback;
    private IntPtr _hook;

    internal InputCapture(SessionLog log, IEnumerable<HidEndpoint> endpoints)
    {
        _log = log;
        _callback = ObserveSpecialKey;
        CreateHandle(new CreateParams { Caption = "AllyXLab capture", Parent = IntPtr.Zero });
        var usages = endpoints.Where(e => e.Ally && e.InputBytes > 0).Select(e => (e.Page, e.Usage)).Append(((ushort)1, (ushort)6)).Distinct();
        var registrations = usages.Select(x => new InputSources.Registration { Page = x.Item1, Usage = x.Item2, Flags = 0x100, Window = Handle }).ToArray();
        if (registrations.Length == 0 || !InputSources.RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<InputSources.Registration>()))
        {
            throw new InvalidOperationException("Raw Input registration failed.");
        }

        _hook = InputSources.SetWindowsHookEx(13, _callback, InputSources.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _log.Add("special-key-observer-unavailable", Marshal.GetLastWin32Error());
        }

        _log.Add("capture-scope", new { RawInput = "Only device paths containing ASUS 0B05:1B4C. Other keyboards are discarded.", XInput = "All four slots are observations, not physical attribution; disconnect other controllers.", Timestamp = "monotonic milliseconds", MaximumSeconds = 20 });
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x218)
        {
            _log.Add("power-broadcast", new { Event = message.WParam.ToInt64() });
        }

        if (message.Msg == 0xFF)
        {
            try { InputSources.ReadRawInput(message.LParam, Read); }
            catch (Exception e) { _log.Add("raw-input-error", e.Message); }
        }
        base.WndProc(ref message);
    }
    private void Read(InputSources.Header header, IntPtr body, int length)
    {
        if (!_devices.TryGetValue(header.Device, out string? endpoint))
        {
            uint nameLength = 0;
            InputSources.GetRawInputDeviceInfo(header.Device, 0x20000007, null, ref nameLength);
            if (nameLength is 0 or > 8192)
            {
                return;
            }

            var name = new StringBuilder((int)nameLength + 1);
            InputSources.GetRawInputDeviceInfo(header.Device, 0x20000007, name, ref nameLength);
            string path = name.ToString();
            endpoint = path.Contains("vid_0b05", StringComparison.OrdinalIgnoreCase) && path.Contains("pid_1b4c", StringComparison.OrdinalIgnoreCase) ? SessionLog.Token(path.ToUpperInvariant()) : null;
            _devices[header.Device] = endpoint;
        }
        if (endpoint is null)
        {
            return;
        }

        if (header.Type == 1 && length >= 16)
        {
            byte[] data = new byte[length];
            Marshal.Copy(body, data, 0, length);
            _log.Add("asus-keyboard", new { Endpoint = endpoint, ScanCode = BitConverter.ToUInt16(data), Flags = BitConverter.ToUInt16(data, 2), VirtualKey = BitConverter.ToUInt16(data, 6), Message = BitConverter.ToUInt32(data, 8) });
            _rawCount++;
        }
        else if (header.Type == 2 && length >= 8)
        {
            InputSources.ReadRawHid(body, length, report => ReadRawHid(endpoint, report));
        }
    }

    private void ReadRawHid(string endpoint, byte[] report)
    {
        string key = endpoint + "/" + report[0];
        _rawCount++;
        if (_lastReports.TryGetValue(key, out var last) && report.AsSpan().SequenceEqual(last))
        {
            _duplicates++;
            return;
        }
        _lastReports[key] = report;
        if (!_baseline.TryGetValue(key, out var first))
        {
            _baseline[key] = first = report;
        }

        int[] changed = Enumerable.Range(0, Math.Min(first.Length, report.Length)).Where(j => first[j] != report[j]).ToArray();
        _log.Add("asus-hid", new { Endpoint = endpoint, ReportId = report[0], Hex = Convert.ToHexString(report), ChangedFromFirstBytes = changed });
    }
    internal void PollXInput()
    {
        for (uint i = 0; i < 4; i++)
        {
            if (InputSources.XInputGetState(i, out var state) != 0)
            {
                continue;
            }

            if (_seen[i] && _packets[i] == state.Packet)
            {
                continue;
            }

            _packets[i] = state.Packet; _seen[i] = true;
            var g = state.Gamepad;
            _log.Add("xinput", new { Slot = i, state.Packet, g.Buttons, g.LeftTrigger, g.RightTrigger, g.LX, g.LY, g.RX, g.RY });
        }
    }
    internal void Summary() => _log.Add("input-summary", new { RawReports = _rawCount, DuplicateHidReports = _duplicates, DistinctReports = _lastReports.Count, Note = "Changed-byte correlations are candidates. No physical attribution is inferred for XInput slots." });
    private IntPtr ObserveSpecialKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int key = Marshal.ReadInt32(data);
            // Secondary evidence only: a hook cannot attribute a key to a physical device.
            // Never retain text keys and never suppress or modify delivery.
            if (key is 0x80 or 0x81 or 0xAD or 0xAE or 0xAF)
            {
                try { _log.Add("special-key-unattributed", new { VirtualKey = key, Message = message.ToInt64(), ScanCode = Marshal.ReadInt32(data, 4), Flags = Marshal.ReadInt32(data, 8) }); }
                catch (InvalidOperationException) { }
            }
        }
        return InputSources.CallNextHookEx(_hook, code, message, data);
    }
    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { InputSources.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        DestroyHandle();
    }
}
