using System.Management;
using System.Runtime.InteropServices;

namespace WSGM.AllyXLab;

/// <summary>The sources that are not controllers: firmware and ACPI events published through WMI,
/// shell app commands (where volume keys surface even without focus), power settings and device
/// arrival.</summary>
internal sealed partial class InputSources
{
    private readonly List<ManagementEventWatcher> _watchers = [];
    private readonly List<IntPtr> _powerNotifications = [];
    private uint _shellMessage;

    private void StartSystemSources()
    {
        _shellMessage = RegisterWindowMessage("SHELLHOOK");
        if (!RegisterShellHookWindow(Handle))
        {
            _log.Add("source-unavailable", new { Source = "shell-hook", Error = Marshal.GetLastWin32Error() });
        }

        foreach ((string name, string guid) in new[]
        {
            ("power-source", "5d3e9a59-e9d5-4b00-a6bd-ff34ff516548"),
            ("display-state", "6fe69556-704a-47a0-8f24-c28d936fda47"),
            ("lid-switch", "ba3e0f4d-b817-4094-a2d1-d56379e6a0f3"),
            ("user-presence", "3c0f4548-c03f-4c4d-b9f2-237ede686376"),
            ("power-scheme", "245d8541-3afd-4b76-9239-3ba91b4b5a1f"),
        })
        {
            Guid setting = new(guid);
            IntPtr handle = RegisterPowerSettingNotification(Handle, ref setting, 0);
            if (handle == IntPtr.Zero)
            {
                _log.Add("source-unavailable", new { Source = "power-setting", Setting = name, Error = Marshal.GetLastWin32Error() });
                continue;
            }

            _powerNotifications.Add(handle);
        }

        // Firmware and ACPI providers publish through root\wmi; the device and power classes live in
        // root\cimv2. Each subscription is optional: a refused one is recorded, never retried blindly.
        Watch(@"root\wmi", "SELECT * FROM __ExtrinsicEvent");
        Watch(@"root\cimv2", "SELECT * FROM Win32_DeviceChangeEvent");
        Watch(@"root\cimv2", "SELECT * FROM Win32_PowerManagementEvent");
    }

    private void Watch(string scope, string query)
    {
        try
        {
            ManagementEventWatcher watcher = new(new ManagementScope(scope), new WqlEventQuery(query));
            watcher.EventArrived += (_, e) => OnWmiEvent(scope, e);
            watcher.Start();
            _watchers.Add(watcher);
            _log.Add("wmi-subscribed", new { Scope = scope, Query = query });
        }
        catch (Exception e)
        {
            _log.Add("source-unavailable", new { Source = "wmi", Scope = scope, Query = query, e.Message });
        }
    }

    private void OnWmiEvent(string scope, EventArrivedEventArgs e)
    {
        try
        {
            ManagementBaseObject instance = e.NewEvent;
            string className = Convert.ToString(instance.ClassPath?.ClassName) ?? "unknown";
            Dictionary<string, string> properties = [];
            foreach (PropertyData property in instance.Properties)
            {
                if (properties.Count >= 24 || property.Value is null)
                {
                    continue;
                }

                properties[property.Name] = property.Value switch
                {
                    byte[] raw => Convert.ToHexString(raw.AsSpan(0, Math.Min(raw.Length, 128))),
                    Array array => string.Join(',', array.Cast<object?>().Take(32)),
                    object value => (Convert.ToString(value) ?? "")[..Math.Min((Convert.ToString(value) ?? "").Length, 256)],
                };
            }

            _log.Add("wmi-event", new { Scope = scope, Class = className, Properties = properties });
            _ui.Post(_ => Report("wmi", $"{className} event"), null);
        }
        catch (Exception error) { _log.Add("source-error", new { Source = "wmi", error.Message }); }
    }

    private void HandleSystemMessage(ref Message message)
    {
        if (_shellMessage != 0 && message.Msg == _shellMessage)
        {
            // HSHELL_APPCOMMAND reaches every shell hook window, so media and volume keys are seen
            // even while another application has focus.
            if (message.WParam.ToInt32() == 12)
            {
                int command = (int)((message.LParam.ToInt64() >> 16) & 0x0FFF);
                _log.Add("app-command", new { Command = command, Source = "shell-hook" });
                Report("app-command", $"app command {command}");
            }
            return;
        }

        switch (message.Msg)
        {
            case 0x0218 when message.WParam.ToInt32() == 0x8013:
                {
                    Guid setting = Marshal.PtrToStructure<Guid>(message.LParam);
                    int length = Marshal.ReadInt32(message.LParam + 16);
                    int value = length is > 0 and <= 4 ? Marshal.ReadInt32(message.LParam + 20) : -1;
                    _log.Add("power-setting", new { Setting = setting, Value = value });
                    Report("power", $"power setting {setting} = {value}");
                    break;
                }
            case 0x0218:
                _log.Add("power-broadcast", new { Event = message.WParam.ToInt64() });
                Report("power", $"power broadcast {message.WParam.ToInt64()}");
                break;
            case 0x0219:
                _log.Add("device-broadcast", new { Event = message.WParam.ToInt64() });
                break;
            case 0x0319:
                {
                    int command = (int)((message.LParam.ToInt64() >> 16) & 0x0FFF);
                    _log.Add("app-command", new { Command = command, Source = "window" });
                    Report("app-command", $"app command {command}");
                    break;
                }
            default:
                break;
        }
    }

    private void StopSystemSources()
    {
        foreach (ManagementEventWatcher watcher in _watchers)
        {
            try { watcher.Stop(); watcher.Dispose(); } catch (ManagementException) { }
        }

        _watchers.Clear();
        foreach (IntPtr handle in _powerNotifications)
        {
            UnregisterPowerSettingNotification(handle);
        }

        _powerNotifications.Clear();
        if (_shellMessage != 0)
        {
            DeregisterShellHookWindow(Handle);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterShellHookWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeregisterShellHookWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
