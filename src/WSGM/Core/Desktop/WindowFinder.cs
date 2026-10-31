using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>
///     Finds current-session application windows by process identity and window class and
///     brings it to the foreground. Port of AnyFSE's window matching (MIT).
/// </summary>
public static class WindowFinder
{
    // Own-process windows normally never appear in the switcher (overlay, taskbar,
    // tray host, splash — UI chrome). The settings window is the exception: in game
    // mode WSGM hosts the only taskbar, so a settings window that drops behind Big
    // Picture is otherwise unreachable. It opts in here for its lifetime.
    private static readonly Lock IncludeGate = new();
    private static readonly HashSet<nint> IncludedOwnWindows = [];

    /// <summary>
    ///     Adds an own-process top-level window to the switchable list despite
    ///     the own-process exclusion (the settings window). Safe to call repeatedly.
    /// </summary>
    /// <param name="hwnd">The window handle to include; zero is ignored.</param>
    public static void IncludeOwnWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        lock (IncludeGate)
        {
            IncludedOwnWindows.Add(hwnd);
        }
    }

    /// <summary>Removes a window previously added by <see cref="IncludeOwnWindow" />.</summary>
    /// <param name="hwnd">The window handle to stop including.</param>
    public static void ExcludeOwnWindow(nint hwnd)
    {
        lock (IncludeGate)
        {
            IncludedOwnWindows.Remove(hwnd);
        }
    }

    /// <summary>
    ///     Describes the current foreground window as "0x&lt;hwnd&gt; (process)" for
    ///     the log. <c>SendInput</c> has no window target — Windows delivers a synthetic
    ///     chord to whatever holds focus — so this is the only way a pasted log can show
    ///     where a "shortcut sent" line actually went.
    /// </summary>
    /// <returns>A short description, or "none" when there is no foreground window.</returns>
    public static string DescribeForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
        {
            return "none";
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return $"0x{hwnd:X} ({process.ProcessName})";
        }
        catch (Exception)
        {
            return $"0x{hwnd:X}";
        }
    }

    /// <summary>Finds the first qualifying top-level window owned by an allowed process.</summary>
    /// <param name="processNames">Semicolon-separated process names that may own the window.</param>
    /// <param name="windowClass">An optional exact Win32 window-class filter.</param>
    /// <returns>The native window handle, or zero when no qualifying window exists.</returns>
    public static nint FindWindow(string processNames, string? windowClass)
    {
        return FindWindow(ProcessLookup.FindProcessIds(processNames), windowClass);
    }

    /// <summary>Finds the first qualifying top-level window owned by one of the given processes.</summary>
    /// <param name="processIds">The process ids that may own the window.</param>
    /// <param name="windowClass">An optional exact Win32 window-class filter.</param>
    /// <returns>The native window handle, or zero when no qualifying window exists.</returns>
    public static unsafe nint FindWindow(HashSet<uint> processIds, string? windowClass)
    {
        if (processIds.Count == 0)
        {
            return 0;
        }

        var state = new SearchState
            { ProcessIds = processIds, WindowClass = string.IsNullOrWhiteSpace(windowClass) ? null : windowClass };
        RunEnumWindows(&EnumWindowsProc, state);
        return state.Found;
    }

    [UnmanagedCallersOnly]
    private static int EnumWindowsProc(nint hWnd, nint lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is not SearchState state)
        {
            return 0;
        }

        if (!NativeMethods.IsWindowVisible(hWnd))
        {
            return 1;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (!state.ProcessIds.Contains(pid))
        {
            return 1;
        }

        if (state.WindowClass is not null)
        {
            Span<char> buffer = stackalloc char[256];
            uint len;
            unsafe
            {
                fixed (char* pointer = buffer)
                {
                    len = NativeMethods.RealGetWindowClassW(hWnd, pointer, (uint)buffer.Length);
                }
            }

            if (!buffer[..(int)len].Equals(state.WindowClass.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
        }

        state.Found = hWnd;
        return 0; // stop enumeration
    }

    /// <summary>
    ///     Alt-tab style enumeration: visible, titled, top-level windows that
    ///     are not tool windows, not DWM-cloaked (suspended UWP ghosts), not the shell's
    ///     desktop window ("Program Manager"), and not ours. Z-order top first.
    /// </summary>
    /// <returns>A new Z-order snapshot of borrowed window identities; callers must tolerate later window destruction.</returns>
    public static unsafe List<AppWindow> ListSwitchableWindows()
    {
        HashSet<nint> included;
        lock (IncludeGate)
        {
            included = [.. IncludedOwnWindows];
        }

        var state = new ListState
        {
            Result = [],
            OwnPid = (uint)Environment.ProcessId,
            ShellWindow = NativeMethods.GetShellWindow(),
            IncludedWindows = included
        };
        RunEnumWindows(&ListWindowsProc, state);
        return state.Result;
    }

    /// <summary>
    ///     The pure alt-tab filter decision, separated from the Win32 queries
    ///     that feed it so the specification is unit-testable: a window is switchable
    ///     when it is visible, titled, not the shell's desktop window, not a tool
    ///     window, not DWM-cloaked, and not owned by this process.
    /// </summary>
    /// <param name="isVisible">Whether the window reports WS_VISIBLE (minimized windows still do).</param>
    /// <param name="isShellWindow">Whether the window is the shell's desktop window (Progman).</param>
    /// <param name="exStyle">The window's extended style bits.</param>
    /// <param name="isOwnProcess">Whether this process owns the window.</param>
    /// <param name="cloaked">The DWM cloaked attribute (non-zero for suspended UWP ghosts).</param>
    /// <param name="titleLength">The window title length in characters.</param>
    /// <returns>Whether the window belongs in an alt-tab-style list.</returns>
    public static bool PassesSwitchableFilter(
        bool isVisible, bool isShellWindow, int exStyle, bool isOwnProcess, uint cloaked, int titleLength)
    {
        return isVisible
               && !isShellWindow
               && (exStyle & NativeMethods.WsExToolWindow) == 0
               && !isOwnProcess
               && cloaked == 0
               && titleLength > 0;
    }

    /// <summary>Whether one window passes the filter <see cref="ListSwitchableWindows" /> applies.</summary>
    /// <param name="hWnd">Borrowed window handle.</param>
    /// <returns>Whether the window is switchable; this process's windows never are.</returns>
    public static bool IsSwitchableWindow(nint hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        var cloaked = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DwmWaCloaked, out var value, 4) == 0
            ? value
            : 0u;
        return PassesSwitchableFilter(
            NativeMethods.IsWindowVisible(hWnd),
            hWnd == NativeMethods.GetShellWindow(),
            NativeMethods.GetWindowLong(hWnd, NativeMethods.GwlExStyle),
            pid == (uint)Environment.ProcessId,
            cloaked,
            NativeMethods.GetWindowTextLengthW(hWnd));
    }

    [UnmanagedCallersOnly]
    private static int ListWindowsProc(nint hWnd, nint lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is not ListState state)
        {
            return 0;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        // Cloak query failure counts as not cloaked.
        var cloaked = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DwmWaCloaked, out var value, 4) == 0
            ? value
            : 0u;
        var title = ReadWindowTitle(hWnd);
        var length = title.Length;
        // An opted-in own window (the settings window) is treated as not-ours so it
        // still has to clear every other filter (visible, titled, not a tool window).
        var treatAsOwn = pid == state.OwnPid && !state.IncludedWindows.Contains(hWnd);
        // Explorer's Progman is visible, plain-styled, and titled "Program
        // Manager", yet real Alt-Tab never offers it.
        if (!PassesSwitchableFilter(
                NativeMethods.IsWindowVisible(hWnd),
                hWnd == state.ShellWindow,
                NativeMethods.GetWindowLong(hWnd, NativeMethods.GwlExStyle),
                treatAsOwn,
                cloaked,
                length))
        {
            return 1;
        }

        state.Result.Add(new AppWindow(hWnd, title, pid)
        {
            IsMinimized = NativeMethods.IsIconic(hWnd)
        });
        return 1;
    }

    /// <summary>
    ///     UnmanagedCallersOnly callbacks cannot capture state, so it travels
    ///     through EnumWindows' lParam as a GCHandle — one pattern for both callbacks,
    ///     no shared statics, no lock.
    /// </summary>
    private static unsafe void RunEnumWindows(delegate* unmanaged<nint, nint, int> callback, object state)
    {
        var handle = GCHandle.Alloc(state);
        try
        {
            NativeMethods.EnumWindows((nint)callback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    ///     Best-effort focus. Against an elevated window SetForegroundWindow may
    ///     fail silently under UIPI — callers should prefer protocol re-activation.
    /// </summary>
    /// <param name="hWnd">Borrowed target window handle; zero is ignored. Minimized windows are restored first.</param>
    public static void BringToForeground(nint hWnd)
    {
        if (hWnd == 0)
        {
            return;
        }

        // SW_RESTORE on a MAXIMIZED window would drop it back to normal size —
        // only a minimized window needs restoring before it can take foreground.
        if (NativeMethods.IsIconic(hWnd))
        {
            NativeMethods.ShowWindow(hWnd, NativeMethods.SwRestore);
        }

        NativeMethods.SetForegroundWindow(hWnd);
    }

    /// <summary>
    ///     Focuses a window while another process holds the foreground and this one has no
    ///     right to take it, by sharing the foreground thread's input state for the call.
    /// </summary>
    /// <param name="hWnd">Borrowed target window handle; the caller has excluded minimized windows.</param>
    /// <returns>Whether the window is now the foreground window.</returns>
    /// <remarks>
    ///     Windows refuses <c>SetForegroundWindow</c> from a background process, and in Game Mode
    ///     WSGM is one whenever Big Picture holds focus. Attaching the calling thread to the
    ///     foreground thread makes the request come from the input owner, which Windows accepts.
    ///     The attachment is released before returning. Call it on a thread with a message queue.
    /// </remarks>
    public static bool ForceForeground(nint hWnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0
                       && foregroundThread != currentThread
                       && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hWnd);
            NativeMethods.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        return NativeMethods.GetForegroundWindow() == hWnd;
    }

    private static unsafe string ReadWindowTitle(nint window)
    {
        Span<char> small = stackalloc char[256];
        while (true)
        {
            var required = checked(NativeMethods.GetWindowTextLengthW(window) + 1);
            var rented = required > small.Length ? ArrayPool<char>.Shared.Rent(required) : null;
            try
            {
                var buffer = rented is null ? small : rented.AsSpan();
                int length;
                fixed (char* pointer = buffer)
                {
                    length = NativeMethods.GetWindowTextW(window, pointer, buffer.Length);
                }

                if (length >= buffer.Length - 1 && NativeMethods.GetWindowTextLengthW(window) > length)
                {
                    continue;
                }

                return length > 0 ? new string(buffer[..length]) : string.Empty;
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<char>.Shared.Return(rented);
                }
            }
        }
    }

    private sealed class SearchState
    {
        public nint Found;
        public required HashSet<uint> ProcessIds;
        public string? WindowClass;
    }

    private sealed class ListState
    {
        public required HashSet<nint> IncludedWindows;
        public uint OwnPid;
        public required List<AppWindow> Result;
        public nint ShellWindow;
    }

    /// <summary>A visible, switchable top-level window discovered during enumeration.</summary>
    /// <param name="Hwnd">The native window handle.</param>
    /// <param name="Title">The title presented in the switcher.</param>
    /// <param name="ProcessId">The identifier of the owning process.</param>
    public sealed record AppWindow(nint Hwnd, string Title, uint ProcessId)
    {
        /// <summary>Gets whether the window was minimized at enumeration time.</summary>
        public bool IsMinimized { get; init; }
    }
}
