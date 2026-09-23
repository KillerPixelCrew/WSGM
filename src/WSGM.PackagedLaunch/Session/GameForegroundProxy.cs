using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WSGM.PackagedLaunch;

/// <summary>
///     A tiny invisible window owned by this wrapper, which hands the foreground to the real game
///     whenever something activates it.
/// </summary>
/// <remarks>
///     <para>
///         Steam resumes a running app by activating a window belonging to the process it launched.
///         That process is this wrapper, whose only window is a console it has hidden, so Resume in
///         Steam does nothing at all and the game stays behind whatever is in front of it. Giving
///         the wrapper a real window to activate closes that gap without pretending to be the game:
///         the window exists purely to catch the activation and pass it on.
///     </para>
///     <para>
///         It also corrects the case that broke input in the recorded trial. A UWP title's frame
///         belongs to ApplicationFrameHost, not the game, and Steam Input follows the foreground
///         window's owner: with the frame in front, Steam selected a different layout and the
///         controller stopped working in the game. Raising the game-owned CoreWindow restored it,
///         in the same process, with nothing reinjected.
///     </para>
///     <para>
///         Layered at zero alpha and marked as a tool window, so it never paints and never appears
///         in Alt-Tab, and shown with SW_SHOWNA so creating it does not steal the foreground from
///         the game at launch. It rechecks the foreground before raising anything, so a queued
///         correction can never pull the game over an application the user deliberately switched to.
///     </para>
/// </remarks>
internal sealed class GameForegroundProxy : IDisposable
{
    private const string ClassName = "WsgmPackagedLaunchForegroundProxy";
    private const uint ReconcileForegroundMessage = 0x8001;
    private readonly WinEventProc foregroundChanged;

    // The delegate has to outlive the window: the class keeps only a raw function pointer.
    private readonly NativeMethods.WindowProc procedure;

    private readonly ManualResetEventSlim ready = new(false);
    private readonly Thread thread;
    private IntPtr foregroundHook;
    private int reconciliationPending;
    private int targetPid;

    private IntPtr window;

    internal GameForegroundProxy()
    {
        procedure = WindowProcedure;
        foregroundChanged = ForegroundChanged;
        thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "packaged-launch-foreground-proxy"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    internal bool Available => window != IntPtr.Zero;

    public void Dispose()
    {
        if (window != IntPtr.Zero)
        {
            NativeMethods.PostMessageW(window, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
            window = IntPtr.Zero;
        }

        thread.Join(TimeSpan.FromSeconds(2));
        ready.Dispose();
    }

    /// Points the proxy at the process whose window should receive the foreground.
    internal void SetTarget(int pid)
    {
        Interlocked.Exchange(ref targetPid, pid);
        NativeMethods.AllowSetForegroundWindow((uint)pid);
        ReconcileForeground();
    }

    /// Uses the supervisor's existing sample when a UWP foreground event is missed or
    /// arrives before the CoreWindow has been attached to its frame.
    internal void ReconcileForeground()
    {
        var proxyWindow = window;
        if (proxyWindow == IntPtr.Zero || Interlocked.Exchange(ref reconciliationPending, 1) != 0)
        {
            return;
        }

        if (!NativeMethods.PostMessageW(proxyWindow, ReconcileForegroundMessage, IntPtr.Zero, IntPtr.Zero))
        {
            Interlocked.Exchange(ref reconciliationPending, 0);
        }
    }

    private void Pump()
    {
        var instance = NativeMethods.GetModuleHandleW(null);
        var classNamePointer = Marshal.StringToHGlobalUni(ClassName);
        try
        {
            var windowClass = default(NativeMethods.WndClassExW);
            windowClass.cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassExW>();
            windowClass.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure);
            windowClass.hInstance = instance;
            windowClass.lpszClassName = classNamePointer;

            if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
            {
                PackagedLaunchLog.Warn(
                    $"foreground proxy: RegisterClassEx failed (error {Marshal.GetLastWin32Error()}); "
                    + "resuming from Steam will not raise the game.");
                ready.Set();
                return;
            }

            window = NativeMethods.CreateWindowExW(
                NativeMethods.WsExLayered | NativeMethods.WsExToolWindow,
                classNamePointer,
                "WSGM packaged launch",
                NativeMethods.WsPopup,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            if (window == IntPtr.Zero)
            {
                PackagedLaunchLog.Warn(
                    $"foreground proxy: CreateWindowEx failed (error {Marshal.GetLastWin32Error()}); "
                    + "resuming from Steam will not raise the game.");
                ready.Set();
                return;
            }

            NativeMethods.SetLayeredWindowAttributes(window, 0, 0, NativeMethods.LwaAlpha);
            NativeMethods.ShowWindow(window, NativeMethods.SwShowNa);
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundChanged, 0, 0, 0);
            if (foregroundHook == IntPtr.Zero)
            {
                PackagedLaunchLog.Warn(
                    $"foreground proxy: foreground event hook failed ({Marshal.GetLastWin32Error()}).");
            }

            PackagedLaunchLog.Info(
                $"foreground proxy: window 0x{window.ToInt64():X} is up; activating it raises the game.");
            ready.Set();

            while (NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessageW(ref message);
            }
        }
        finally
        {
            if (foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(foregroundHook);
            }

            ready.Set();
            Marshal.FreeHGlobal(classNamePointer);
        }
    }

    private void ForegroundChanged(IntPtr hook, uint eventId, IntPtr foreground, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        var pid = Volatile.Read(ref targetPid);
        if (pid == 0 || foreground == IntPtr.Zero || foreground != GetForegroundWindow())
        {
            return;
        }

        var className = new StringBuilder(128);
        GetClassNameW(foreground, className, className.Capacity);
        if (!className.ToString().Equals("ApplicationFrameWindow", StringComparison.Ordinal))
        {
            return;
        }

        var coreWindow = CoreWindowIn(foreground, pid);
        if (coreWindow == IntPtr.Zero || foreground != GetForegroundWindow())
        {
            return;
        }

        var raised = NativeMethods.SetForegroundWindow(coreWindow);
        PackagedLaunchLog.Info(
            $"foreground proxy: game frame 0x{foreground.ToInt64():X} -> CoreWindow 0x{coreWindow.ToInt64():X} pid {pid}; SetForegroundWindow={raised}");
    }

    private static IntPtr CoreWindowIn(IntPtr parent, int pid)
    {
        var found = IntPtr.Zero;

        bool Inspect(IntPtr candidate, IntPtr ignored)
        {
            NativeMethods.GetWindowThreadProcessId(candidate, out var owner);
            if (owner != (uint)pid)
            {
                return true;
            }

            var className = new StringBuilder(128);
            GetClassNameW(candidate, className, className.Capacity);
            if (!className.ToString().Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
            {
                return true;
            }

            found = candidate;
            return false;
        }

        Inspect(parent, IntPtr.Zero);
        if (found == IntPtr.Zero)
        {
            EnumChildWindows(parent, Inspect, IntPtr.Zero);
        }

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
            case NativeMethods.WmActivate when wParam != IntPtr.Zero:
            case NativeMethods.WmSetFocus:
            case NativeMethods.WmNcActivate when wParam != IntPtr.Zero:
                RaiseGame(message);
                break;
            case NativeMethods.WmActivateApp when wParam != IntPtr.Zero:
                RaiseGame(message);
                break;
            case NativeMethods.WmClose:
                NativeMethods.DestroyWindow(hWnd);
                return IntPtr.Zero;
            case NativeMethods.WmDestroy:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hWnd, message, wParam, lParam);
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
            PackagedLaunchLog.Warn(
                $"foreground proxy: activated (message 0x{message:X}) but pid {pid} has no window to raise.");
            return;
        }

        if (NativeMethods.IsIconic(target))
        {
            NativeMethods.ShowWindow(target, NativeMethods.SwRestore);
        }

        if (target == GetForegroundWindow())
        {
            return;
        }

        var raised = NativeMethods.SetForegroundWindow(target);
        PackagedLaunchLog.Info($"foreground proxy: activation (message 0x{message:X}) forwarded to pid {pid} "
                               + $"window 0x{target.ToInt64():X}; SetForegroundWindow={raised}");
    }

    /// The game's most plausible main window: visible, titled, and not the proxy itself.
    private IntPtr MainWindowOf(int pid)
    {
        var best = IntPtr.Zero;
        foreach (var candidate in ProcessInspector.WindowsOf(pid))
        {
            if (candidate.Handle == window || !candidate.Visible)
            {
                continue;
            }

            var coreWindow = CoreWindowIn(candidate.Handle, pid);
            if (coreWindow != IntPtr.Zero)
            {
                return coreWindow;
            }

            if (best == IntPtr.Zero)
            {
                best = candidate.Handle;
            }
        }

        return best;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module, WinEventProc callback,
        uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr parameter);

    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread,
        uint time);

    private delegate bool EnumChildProc(IntPtr window, IntPtr parameter);
}
