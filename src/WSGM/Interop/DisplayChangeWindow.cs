using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Interop;

/// <summary>
///     A hidden top-level window that exists only to hear about displays appearing.
///     <see cref="MessageWindow" /> cannot do this job. It is a message-only window (HWND_MESSAGE), and
///     Windows broadcasts <c>WM_DISPLAYCHANGE</c> to top-level windows only, so a message-only window
///     is never told. This window is therefore a real top-level window that is created but never shown:
///     <c>WS_POPUP</c> with no size, a tool window so it cannot reach the taskbar or Alt-Tab, and
///     <c>WS_EX_NOACTIVATE</c> so nothing can steal focus to it.
///     It also forwards <c>DBT_DEVNODES_CHANGED</c>, which arrives earlier than
///     <c>WM_DISPLAYCHANGE</c> when a monitor is being enumerated and needs no device registration.
///     Both are hints, not facts: a listener re-reads the display topology and decides for itself.
/// </summary>
public sealed unsafe class DisplayChangeWindow : IDisposable
{
    private static DisplayChangeWindow? _instance;

    /// <summary>
    ///     Creates the process's display-change window on the UI thread, whose pump services it. The
    ///     composition that constructs it owns it and disposes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Another owner already holds the window, or the native window could not be created.
    /// </exception>
    public DisplayChangeWindow()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_instance is not null)
        {
            throw new InvalidOperationException("The display-change window already has an owner.");
        }

        const string className = "WSGM.DisplayChangeWindow";
        var hInstance = NativeMethods.GetModuleHandleW(0);
        _ = MessageWindow.RegisterWindowClass(className, &WndProc);

        Handle = NativeMethods.CreateWindowExW(
            NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate,
            className, null, NativeMethods.WsPopup,
            0, 0, 0, 0,
            0, 0, hInstance, 0);
        if (Handle == 0)
        {
            throw new InvalidOperationException("Failed to create the display-change window.");
        }

        _instance = this;
        Log.Info("Display-change window created.");
    }

    /// <summary>Gets the native handle, or zero once disposed.</summary>
    private nint Handle { get; set; }

    /// <summary>
    ///     Destroys the native window on the UI thread. A window that could not be destroyed keeps its
    ///     handle and its dispatch slot.
    /// </summary>
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Handle == 0)
        {
            return;
        }

        if (!NativeMethods.DestroyWindow(Handle))
        {
            Log.Warn($"DestroyWindow(display-change window) failed (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        Handle = 0;
        if (_instance == this)
        {
            _instance = null;
        }
    }

    /// <summary>Raised on the Avalonia UI thread when the set of displays may have changed.</summary>
    /// <remarks>
    ///     Fires more often than the display set actually changes, including for device-tree churn
    ///     that has nothing to do with monitors. Subscribers must re-observe rather than assume.
    /// </remarks>
    public event Action? DisplaysChanged;

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        var instance = _instance;
        if (instance is null
            || (msg != NativeMethods.WmDisplayChange
                && (msg != NativeMethods.WmDeviceChange || wParam != NativeMethods.DbtDevnodesChanged)))
        {
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        Dispatcher.UIThread.Post(() => instance.DisplaysChanged?.Invoke());
        return 0;
    }
}
