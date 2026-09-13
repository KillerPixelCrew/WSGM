using System;
using System.Runtime.InteropServices;
using System.Text;
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
    private const uint ReconcileForegroundMessage = 0x8001;

    private readonly SpikeLog log;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Thread thread;

    // The delegate has to outlive the window: the class keeps only a raw function pointer.
    private readonly Native.WindowProc procedure;
    private readonly WinEventProc foregroundChanged;

    private IntPtr window;
    private int targetPid;
    private IntPtr foregroundHook;
    private int reconciliationPending;

    internal ForegroundProxy(SpikeLog log)
    {
        this.log = log;
        procedure = WindowProcedure;
        foregroundChanged = ForegroundChanged;
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
        ReconcileForeground();
    }

    /// Uses the supervisor's existing sample when a UWP foreground event is missed or
    /// arrives before the CoreWindow has been attached to its frame.
    internal void ReconcileForeground()
    {
        var proxyWindow = window;
        if (proxyWindow == IntPtr.Zero || Interlocked.Exchange(ref reconciliationPending, 1) != 0) { return; }
        if (!Native.PostMessageW(proxyWindow, ReconcileForegroundMessage, IntPtr.Zero, IntPtr.Zero))
        {
            Interlocked.Exchange(ref reconciliationPending, 0);
        }
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
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundChanged, 0, 0, 0);
            if (foregroundHook == IntPtr.Zero)
            {
                log.Warn($"foreground proxy: foreground event hook failed ({Marshal.GetLastWin32Error()}).");
            }
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
            if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); }
            ready.Set();
            Marshal.FreeHGlobal(classNamePointer);
        }
    }

    private void ForegroundChanged(IntPtr hook, uint eventId, IntPtr foreground, int objectId, int childId, uint eventThread, uint eventTime)
    {
        var pid = Volatile.Read(ref targetPid);
        if (pid == 0 || foreground == IntPtr.Zero || foreground != GetForegroundWindow()) { return; }
        var className = new StringBuilder(128);
        GetClassNameW(foreground, className, className.Capacity);
        if (!className.ToString().Equals("ApplicationFrameWindow", StringComparison.Ordinal)) { return; }
        var coreWindow = CoreWindowIn(foreground, pid);
        if (coreWindow == IntPtr.Zero || foreground != GetForegroundWindow()) { return; }
        var raised = Native.SetForegroundWindow(coreWindow);
        log.Info($"foreground proxy: game frame 0x{foreground.ToInt64():X} -> CoreWindow 0x{coreWindow.ToInt64():X} pid {pid}; SetForegroundWindow={raised}");
    }

    private static IntPtr CoreWindowIn(IntPtr parent, int pid)
    {
        var found = IntPtr.Zero;
        bool Inspect(IntPtr candidate, IntPtr ignored)
        {
            Native.GetWindowThreadProcessId(candidate, out var owner);
            if (owner != (uint)pid) { return true; }
            var className = new StringBuilder(128);
            GetClassNameW(candidate, className, className.Capacity);
            if (!className.ToString().Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal)) { return true; }
            found = candidate;
            return false;
        }

        Inspect(parent, IntPtr.Zero);
        if (found == IntPtr.Zero) { EnumChildWindows(parent, Inspect, IntPtr.Zero); }
        return found;
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case ReconcileForegroundMessage:
                Interlocked.Exchange(ref reconciliationPending, 0);
                ForegroundChanged(foregroundHook, 3, GetForegroundWindow(), 0, 0, 0, 0);
                break;
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

        if (target == GetForegroundWindow()) { return; }

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

            var coreWindow = CoreWindowIn(candidate.Handle, pid);
            if (coreWindow != IntPtr.Zero) { return coreWindow; }

            if (best == IntPtr.Zero)
            {
                best = candidate.Handle;
            }
        }

        return best;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumChildProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr parameter);

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
