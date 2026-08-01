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

    /// <summary>Create() is the only entry point: a directly constructed instance
    /// would carry Handle == 0, and RegisterHotKey on hwnd 0 registers a thread
    /// hotkey the WndProc never sees.</summary>
    private MessageWindow()
    {
    }

    public nint Handle => _hwnd;

    /// <summary>Raised on the Avalonia UI thread with the hotkey id.</summary>
    public event Action<int>? HotkeyPressed;

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
        if (msg == NativeMethods.WmHotkey && _instance is not null)
        {
            var id = (int)wParam;
            Dispatcher.UIThread.Post(() => _instance.HotkeyPressed?.Invoke(id));
            return 0;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
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
