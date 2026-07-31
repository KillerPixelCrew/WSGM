using System;
using Avalonia.Threading;

namespace WSGM.Interop;

/// <summary>A raw message-only (HWND_MESSAGE) window whose queue is pumped by the
/// Avalonia UI thread. Hosts RegisterHotKey registrations.</summary>
public sealed unsafe class MessageWindow : IDisposable
{
    private static MessageWindow? _instance;
    private nint _hwnd;

    public nint Handle => _hwnd;

    /// <summary>Raised on the Avalonia UI thread with the hotkey id.</summary>
    public event Action<int>? HotkeyPressed;

    public static MessageWindow Create()
    {
        if (_instance is not null)
        {
            return _instance;
        }

        var hInstance = NativeMethods.GetModuleHandleW(0);
        var className = "WSGM.MessageWindow\0";
        fixed (char* pClassName = className)
        {
            var wc = new NativeMethods.WndClassW
            {
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                lpszClassName = (nint)pClassName,
            };
            NativeMethods.RegisterClassW(&wc);
        }

        var hwnd = NativeMethods.CreateWindowExW(
            0, "WSGM.MessageWindow", null, 0,
            0, 0, 0, 0,
            NativeMethods.HwndMessage, 0, hInstance, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException("Failed to create message window");
        }

        _instance = new MessageWindow { _hwnd = hwnd };
        return _instance;
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
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        _instance = null;
    }
}
