using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wsgm.UwpSpike;

/// A tiny invisible top-level window owned by the wrapper, which hands the foreground to
/// the real game whenever something activates it.
///
/// Steam resumes a running app by activating a window belonging to the process it
/// launched. That process is this wrapper, and its only window is a console it has
/// hidden, so "Resume" in Steam does nothing at all and the game stays behind whatever
/// is in front of it. Giving the wrapper a real window to activate closes that gap
/// without pretending the wrapper is the game: the window exists purely to catch the
/// activation and pass it on.
///
/// It is layered at zero alpha and marked as a tool window, so it never paints and never
/// appears in alt-tab, and it is shown with SW_SHOWNA so that creating it does not steal
/// the foreground from the game at launch.
internal sealed class ForegroundProxy : IDisposable
{
    private const string ClassName = "WsgmUwpSpikeForegroundProxy";

    private readonly SpikeLog log;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Thread thread;

    // The delegate has to outlive the window: the class keeps only a raw function pointer.
    private readonly Native.WindowProc procedure;

    private IntPtr window;
    private int targetPid;

    internal ForegroundProxy(SpikeLog log)
    {
        this.log = log;
        procedure = WindowProcedure;
        thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "uwp-spike-foreground-proxy",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    internal bool Available => window != IntPtr.Zero;

    /// Points the proxy at the process whose window should receive the foreground.
    internal void SetTarget(int pid)
    {
        Interlocked.Exchange(ref targetPid, pid);
        Native.AllowSetForegroundWindow((uint)pid);
    }

    private void Pump()
    {
        var instance = Native.GetModuleHandleW(null);
        var classNamePointer = Marshal.StringToHGlobalUni(ClassName);
        try
        {
            var windowClass = default(Native.WndClassExW);
            windowClass.cbSize = (uint)Marshal.SizeOf<Native.WndClassExW>();
            windowClass.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure);
            windowClass.hInstance = instance;
            windowClass.lpszClassName = classNamePointer;

            if (Native.RegisterClassExW(ref windowClass) == 0)
            {
                log.Warn($"foreground proxy: RegisterClassEx failed (error {Marshal.GetLastWin32Error()}); "
                    + "resuming from Steam will not raise the game.");
                ready.Set();
                return;
            }

            window = Native.CreateWindowExW(
                Native.WsExLayered | Native.WsExToolWindow,
                classNamePointer,
                "WSGM UWP spike",
                Native.WsPopup,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            if (window == IntPtr.Zero)
            {
                log.Warn($"foreground proxy: CreateWindowEx failed (error {Marshal.GetLastWin32Error()}); "
                    + "resuming from Steam will not raise the game.");
                ready.Set();
                return;
            }

            Native.SetLayeredWindowAttributes(window, 0, 0, Native.Lwa_Alpha);
            Native.ShowWindow(window, Native.SwShowNa);
            log.Info($"foreground proxy: window 0x{window.ToInt64():X} is up; activating it raises the game.");
            ready.Set();

            while (Native.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessageW(ref message);
            }
        }
        finally
        {
            ready.Set();
            Marshal.FreeHGlobal(classNamePointer);
        }
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Native.WmActivate when wParam != IntPtr.Zero:
            case Native.WmSetFocus:
            case Native.WmNcActivate when wParam != IntPtr.Zero:
                RaiseGame(message);
                break;
            case Native.WmActivateApp when wParam != IntPtr.Zero:
                RaiseGame(message);
                break;
            case Native.WmClose:
                Native.DestroyWindow(hWnd);
                return IntPtr.Zero;
            case Native.WmDestroy:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private void RaiseGame(uint message)
    {
        var pid = Volatile.Read(ref targetPid);
        if (pid == 0)
        {
            return;
        }

        var target = MainWindowOf(pid);
        if (target == IntPtr.Zero)
        {
            log.Warn($"foreground proxy: activated (message 0x{message:X}) but pid {pid} has no window to raise.");
            return;
        }

        if (Native.IsIconic(target))
        {
            Native.ShowWindow(target, Native.SwRestore);
        }

        var raised = Native.SetForegroundWindow(target);
        log.Info($"foreground proxy: activation (message 0x{message:X}) forwarded to pid {pid} "
            + $"window 0x{target.ToInt64():X}; SetForegroundWindow={raised}");
    }

    /// The game's most plausible main window: visible, titled, and not the proxy itself.
    private IntPtr MainWindowOf(int pid)
    {
        var best = IntPtr.Zero;
        foreach (var candidate in ProcessProbe.WindowsOf(pid))
        {
            if (candidate.Handle == window || !candidate.Visible)
            {
                continue;
            }

            if (candidate.Title.Length > 0)
            {
                return candidate.Handle;
            }

            if (best == IntPtr.Zero)
            {
                best = candidate.Handle;
            }
        }

        return best;
    }

    public void Dispose()
    {
        if (window != IntPtr.Zero)
        {
            Native.PostMessageW(window, Native.WmClose, IntPtr.Zero, IntPtr.Zero);
            window = IntPtr.Zero;
        }

        thread.Join(TimeSpan.FromSeconds(2));
        ready.Dispose();
    }
}
