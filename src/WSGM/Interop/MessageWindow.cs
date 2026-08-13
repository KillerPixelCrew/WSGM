using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Interop;

/// <summary>A raw message-only (HWND_MESSAGE) window whose queue is pumped by the
/// Avalonia UI thread. Hosts RegisterHotKey registrations.</summary>
public sealed unsafe class MessageWindow : IDisposable
{
    private static MessageWindow? _instance;
    private nint _hwnd;
    private uint _shellHookMessage;
    private bool _shellHookRegistered;
    private nint _displayNotify;

    /// <summary>Create() is the only entry point: a directly constructed instance
    /// would carry Handle == 0, and RegisterHotKey on hwnd 0 registers a thread
    /// hotkey the WndProc never sees.</summary>
    private MessageWindow()
    {
    }

    /// <summary>Gets the native handle of the message-only window.</summary>
    public nint Handle => _hwnd;

    /// <summary>Raised on the Avalonia UI thread with the hotkey id.</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>Raised on the Avalonia UI thread when the session's display turns on or
    /// off, with the MONITOR_DISPLAY_STATE value (0 = off, 1 = on, 2 = dimmed).</summary>
    public event Action<int>? DisplayStateChanged;

    /// <summary>Raised on the Avalonia UI thread for a shell-hook notification.
    /// Its delegate receives the HSHELL_* event code followed by the event-specific
    /// lParam supplied by the shell.</summary>
    public event Action<nint, nint>? ShellHookReceived;

    /// <summary>Gets or creates the process-wide message-only window.</summary>
    /// <returns>The singleton message window.</returns>
    public static MessageWindow Create()
    {
        if (_instance is not null)
        {
            return _instance;
        }

        var hwnd = CreateMessageOnlyWindow(
            "WSGM.MessageWindow", &WndProc, "Failed to create message window");
        _instance = new MessageWindow { _hwnd = hwnd };
        return _instance;
    }

    /// <summary>Registers this window to receive shell-hook notifications.
    /// The caller must later call <see cref="DeregisterShellHook"/> before a
    /// different shell takes ownership of the desktop.</summary>
    /// <returns>True when the registration is active.</returns>
    public bool RegisterShellHook()
    {
        if (_shellHookRegistered)
        {
            return true;
        }

        _shellHookMessage = NativeMethods.RegisterWindowMessageW("SHELLHOOK");
        if (_shellHookMessage == 0)
        {
            Log.Warn($"RegisterWindowMessage(SHELLHOOK) failed (error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        if (!NativeMethods.RegisterShellHookWindow(_hwnd))
        {
            Log.Warn($"RegisterShellHookWindow failed (error {Marshal.GetLastWin32Error()}).");
            _shellHookMessage = 0;
            return false;
        }

        _shellHookRegistered = true;
        Log.Info("Shell-hook window registered.");
        return true;
    }

    /// <summary>Stops this window receiving shell-hook notifications.</summary>
    public void DeregisterShellHook()
    {
        if (!_shellHookRegistered)
        {
            return;
        }

        if (!NativeMethods.DeregisterShellHookWindow(_hwnd))
        {
            Log.Warn($"DeregisterShellHookWindow failed (error {Marshal.GetLastWin32Error()}).");
        }
        _shellHookRegistered = false;
        _shellHookMessage = 0;
        Log.Info("Shell-hook window deregistered.");
    }

    /// <summary>Subscribes this window to the session's display on/off notifications.
    /// Idempotent; safe to call when the feature toggle turns on at runtime.</summary>
    /// <returns>True when the registration is active.</returns>
    public bool RegisterDisplayStateNotifications()
    {
        if (_displayNotify != 0)
        {
            return true;
        }
        _displayNotify = NativeMethods.RegisterPowerSettingNotification(
            _hwnd, NativeMethods.GuidSessionDisplayStatus, NativeMethods.DeviceNotifyWindowHandle);
        if (_displayNotify == 0)
        {
            Log.Warn("RegisterPowerSettingNotification(display status) failed "
                + $"(error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        Log.Info("Display-state notifications registered.");
        return true;
    }

    /// <summary>Stops this window receiving display on/off notifications.</summary>
    public void DeregisterDisplayStateNotifications()
    {
        if (_displayNotify == 0)
        {
            return;
        }
        if (!NativeMethods.UnregisterPowerSettingNotification(_displayNotify))
        {
            Log.Warn("UnregisterPowerSettingNotification failed "
                + $"(error {Marshal.GetLastWin32Error()}).");
        }
        _displayNotify = 0;
        Log.Info("Display-state notifications deregistered.");
    }

    /// <summary>Shared class-registration + window-creation path for the process's
    /// message-only (HWND_MESSAGE) windows. Class registration is idempotent:
    /// ERROR_CLASS_ALREADY_EXISTS (1410) is benign — a re-create after a destroy
    /// reuses the still-registered class. Any other registration failure is only
    /// logged, because CreateWindowExW then fails on the unknown class and throws
    /// <paramref name="failureMessage"/> anyway.</summary>
    internal static nint CreateMessageOnlyWindow(
        string className,
        delegate* unmanaged<nint, uint, nint, nint, nint> wndProc,
        string failureMessage)
    {
        var hInstance = NativeMethods.GetModuleHandleW(0);
        var terminatedClassName = className + "\0";
        fixed (char* pClassName = terminatedClassName)
        {
            var wc = new NativeMethods.WndClassW
            {
                lpfnWndProc = wndProc,
                hInstance = hInstance,
                lpszClassName = (nint)pClassName,
            };
            if (NativeMethods.RegisterClassW(&wc) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1410)
                {
                    Log.Warn($"RegisterClassW({className}) failed (error {error}).");
                }
            }
        }

        var hwnd = NativeMethods.CreateWindowExW(
            0, className, null, 0,
            0, 0, 0, 0,
            NativeMethods.HwndMessage, 0, hInstance, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException(failureMessage);
        }
        return hwnd;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        var instance = _instance;
        if (instance is null)
        {
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        if (msg == NativeMethods.WmHotkey)
        {
            var id = (int)wParam;
            Dispatcher.UIThread.Post(() => instance.HotkeyPressed?.Invoke(id));
            return 0;
        }
        if (msg == NativeMethods.WmPowerBroadcast
            && wParam == NativeMethods.PbtPowerSettingChange
            && lParam != 0
            && instance._displayNotify != 0)
        {
            var setting = Marshal.PtrToStructure<NativeMethods.PowerBroadcastSetting>(lParam);
            // The same window could later carry other power settings; only the display
            // status is ours, and only a 4-byte DWORD payload is the documented shape.
            if (setting.PowerSetting == NativeMethods.GuidSessionDisplayStatus
                && setting.DataLength >= 4)
            {
                var state = Marshal.ReadInt32(
                    lParam + (int)Marshal.OffsetOf<NativeMethods.PowerBroadcastSetting>(
                        nameof(NativeMethods.PowerBroadcastSetting.Data)));
                Dispatcher.UIThread.Post(() => instance.DisplayStateChanged?.Invoke(state));
            }
            return 1;
        }
        if (msg == instance._shellHookMessage && instance._shellHookRegistered)
        {
            Dispatcher.UIThread.Post(() => instance.ShellHookReceived?.Invoke(wParam, lParam));
            return 0;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Destroys the native window and clears the process singleton.</summary>
    public void Dispose()
    {
        DeregisterShellHook();
        DeregisterDisplayStateNotifications();
        if (_hwnd != 0)
        {
            if (!NativeMethods.DestroyWindow(_hwnd))
            {
                // Fails from the wrong thread; the handle then leaks until exit.
                Log.Warn($"DestroyWindow(message window) failed (error {Marshal.GetLastWin32Error()}).");
            }
            _hwnd = 0;
        }
        _instance = null;
    }
}
