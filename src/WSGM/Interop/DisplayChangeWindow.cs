using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Interop;

/// <summary>A hidden top-level window that exists only to hear about displays appearing.
///
/// <see cref="MessageWindow"/> cannot do this job. It is a message-only window (HWND_MESSAGE), and
/// Windows broadcasts <c>WM_DISPLAYCHANGE</c> to top-level windows only, so a message-only window
/// is never told. This window is therefore a real top-level window that is created but never shown:
/// <c>WS_POPUP</c> with no size, a tool window so it cannot reach the taskbar or Alt-Tab, and
/// <c>WS_EX_NOACTIVATE</c> so nothing can steal focus to it.
///
/// It also forwards <c>DBT_DEVNODES_CHANGED</c>, which arrives earlier than
/// <c>WM_DISPLAYCHANGE</c> when a monitor is being enumerated and needs no device registration.
/// Both are hints, not facts: a listener re-reads the display topology and decides for itself.
/// </summary>
public sealed unsafe class DisplayChangeWindow : IDisposable
{
    private static DisplayChangeWindow? _instance;
    private nint _hwnd;

    private DisplayChangeWindow()
    {
    }

    /// <summary>Raised on the Avalonia UI thread when the set of displays may have changed.</summary>
    /// <remarks>
    /// Fires more often than the display set actually changes, including for device-tree churn
    /// that has nothing to do with monitors. Subscribers must re-observe rather than assume.
    /// </remarks>
    public event Action? DisplaysChanged;

    /// <summary>Gets the native handle, or zero once disposed.</summary>
    public nint Handle => _hwnd;

    /// <summary>Gets or creates the process-wide display-change window.</summary>
    /// <returns>The singleton window.</returns>
    /// <exception cref="InvalidOperationException">The native window could not be created.</exception>
    public static DisplayChangeWindow Create()
    {
        if (_instance is not null) { return _instance; }

        const string className = "WSGM.DisplayChangeWindow";
        nint hInstance = NativeMethods.GetModuleHandleW(0);
        string terminated = className + "\0";
        fixed (char* pClassName = terminated)
        {
            NativeMethods.WndClassW wc = new()
            {
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                lpszClassName = (nint)pClassName,
            };
            if (NativeMethods.RegisterClassW(&wc) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1410) { Log.Warn($"RegisterClassW({className}) failed (error {error})."); }
            }
        }

        nint hwnd = NativeMethods.CreateWindowExW(
            (uint)(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate),
            className, null, NativeMethods.WsPopup,
            0, 0, 0, 0,
            0, 0, hInstance, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException("Failed to create the display-change window.");
        }
        _instance = new DisplayChangeWindow { _hwnd = hwnd };
        Log.Info("Display-change window created.");
        return _instance;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        DisplayChangeWindow? instance = _instance;
        if (instance is null) { return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam); }
        if (msg == NativeMethods.WmDisplayChange
            || (msg == NativeMethods.WmDeviceChange && wParam == NativeMethods.DbtDevnodesChanged))
        {
            Dispatcher.UIThread.Post(() => instance.DisplaysChanged?.Invoke());
            return 0;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Destroys the native window and clears the process singleton.</summary>
    public void Dispose()
    {
        if (_hwnd != 0)
        {
            if (!NativeMethods.DestroyWindow(_hwnd))
            {
                // Fails from the wrong thread; the handle then leaks until exit.
                Log.Warn($"DestroyWindow(display-change window) failed (error {Marshal.GetLastWin32Error()}).");
            }
            _hwnd = 0;
        }
        _instance = null;
    }
}
