using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Routes a Settings shortcut to the resident process that owns input.</summary>
internal sealed unsafe class SettingsActivation : IDisposable
{
    private const string WindowClass = "WSGM.SettingsActivation";
    private const uint OpenSettingsMessage = 0x8001;
    private static SettingsActivation? _instance;
    private readonly Action _open;
    private nint _window;

    internal SettingsActivation(Action open, string windowClass = WindowClass)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_instance is not null)
        {
            throw new InvalidOperationException("Settings activation already has an owner.");
        }
        _open = open;
        _window = MessageWindow.CreateMessageOnlyWindow(
            windowClass, &WindowProc, "Could not create Settings activation window.");
        if (!NativeMethods.ChangeWindowMessageFilterEx(
            _window, OpenSettingsMessage, NativeMethods.MsgfltAllow, 0))
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.DestroyWindow(_window);
            _window = 0;
            throw new Win32Exception(error, "Could not allow desktop Settings activation.");
        }
        _instance = this;
    }

    internal static bool TryRequest(string windowClass = WindowClass)
    {
        var window = NativeMethods.FindWindowExW(NativeMethods.HwndMessage, 0, windowClass, null);
        if (window == 0)
        {
            return false;
        }
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        // Transfer this user launch's foreground permission to the existing UI owner.
        NativeMethods.AllowSetForegroundWindow(processId);
        return NativeMethods.SendMessageTimeoutW(window, OpenSettingsMessage, 0, 0,
            NativeMethods.SmtoAbortIfHung, 1000, out var accepted) != 0 && accepted == 1;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == OpenSettingsMessage && wParam == 0 && lParam == 0
            && _instance is { } owner && owner._window == window)
        {
            // Acknowledge promptly. Constructing Settings may involve a cold XAML load.
            Dispatcher.UIThread.Post(() =>
            {
                if (owner._window != 0)
                {
                    try { owner._open(); }
                    catch (Exception ex) { Log.Error("Resident Settings could not open", ex); }
                }
            });
            return 1;
        }
        return NativeMethods.DefWindowProcW(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_window == 0) { return; }
        if (!NativeMethods.DestroyWindow(_window))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not close Settings activation window.");
        }
        _window = 0;
        _instance = null;
    }
}
