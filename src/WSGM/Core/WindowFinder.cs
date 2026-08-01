using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Finds the home app's main window by process name(s) + window class and
/// brings it to the foreground. Port of AnyFSE's window matching (MIT).</summary>
public static class WindowFinder
{
    private sealed class SearchState
    {
        public required HashSet<uint> ProcessIds;
        public string? WindowClass;
        public nint Found;
    }

    private sealed class ListState
    {
        public required List<AppWindow> Result;
        public uint OwnPid;
        public nint ShellWindow;
    }

    /// <summary>Finds process identifiers whose names appear in a semicolon-separated allowlist.</summary>
    /// <param name="semicolonNames">Case-insensitive process names separated by semicolons.</param>
    /// <returns>The matching process identifiers.</returns>
    public static HashSet<uint> FindProcessIds(string semicolonNames)
    {
        var result = new HashSet<uint>();
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var name in semicolonNames.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var plain = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            foreach (var p in Process.GetProcessesByName(plain))
            {
                // Other sessions (RDP, fast user switching) run their own Steam and
                // startup tools — only this session's processes count.
                try
                {
                    if (p.SessionId == session)
                    {
                        result.Add((uint)p.Id);
                    }
                }
                catch { /* process may have exited */ }
                finally { p.Dispose(); }
            }
        }
        return result;
    }

    /// <summary>Finds the first qualifying top-level window owned by an allowed process.</summary>
    /// <param name="processNames">Semicolon-separated process names that may own the window.</param>
    /// <param name="windowClass">An optional exact Win32 window-class filter.</param>
    /// <returns>The native window handle, or zero when no qualifying window exists.</returns>
    public static unsafe nint FindWindow(string processNames, string? windowClass)
    {
        var pids = FindProcessIds(processNames);
        if (pids.Count == 0)
        {
            return 0;
        }

        var state = new SearchState { ProcessIds = pids, WindowClass = string.IsNullOrWhiteSpace(windowClass) ? null : windowClass };
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
            var buffer = new char[256];
            var len = NativeMethods.RealGetWindowClassW(hWnd, buffer, (uint)buffer.Length);
            var className = new string(buffer, 0, (int)len);
            if (!string.Equals(className, state.WindowClass, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
        }

        state.Found = hWnd;
        return 0; // stop enumeration
    }

    /// <summary>A visible, switchable top-level window discovered during enumeration.</summary>
    public sealed record AppWindow
    {
        /// <summary>Creates a switchable-window snapshot.</summary>
        /// <param name="hwnd">The native window handle.</param>
        /// <param name="title">The title presented in the switcher.</param>
        /// <param name="processId">The identifier of the owning process.</param>
        public AppWindow(nint hwnd, string title, uint processId)
        {
            Hwnd = hwnd;
            Title = title;
            ProcessId = processId;
        }

        /// <summary>Gets the native window handle.</summary>
        public nint Hwnd { get; init; }

        /// <summary>Gets the title presented in the switcher.</summary>
        public string Title { get; init; }

        /// <summary>Gets the identifier of the owning process.</summary>
        public uint ProcessId { get; init; }

        /// <summary>Gets whether the window was minimized at enumeration time.</summary>
        public bool IsMinimized { get; init; }

        /// <summary>Deconstructs the window using its original positional-record shape.</summary>
        /// <param name="hwnd">Receives the native window handle.</param>
        /// <param name="title">Receives the title presented in the switcher.</param>
        /// <param name="processId">Receives the identifier of the owning process.</param>
        public void Deconstruct(out nint hwnd, out string title, out uint processId)
        {
            hwnd = Hwnd;
            title = Title;
            processId = ProcessId;
        }
    }

    /// <summary>Alt-tab style enumeration: visible, titled, top-level windows that
    /// are not tool windows, not DWM-cloaked (suspended UWP ghosts), not the shell's
    /// desktop window ("Program Manager"), and not ours. Z-order top first.</summary>
    public static unsafe List<AppWindow> ListSwitchableWindows()
    {
        var state = new ListState
        {
            Result = [],
            OwnPid = (uint)Environment.ProcessId,
            ShellWindow = NativeMethods.GetShellWindow(),
        };
        RunEnumWindows(&ListWindowsProc, state);
        return state.Result;
    }

    /// <summary>The pure alt-tab filter decision, separated from the Win32 queries
    /// that feed it so the specification is unit-testable: a window is switchable
    /// when it is visible, titled, not the shell's desktop window, not a tool
    /// window, not DWM-cloaked, and not owned by this process.</summary>
    /// <param name="isVisible">Whether the window reports WS_VISIBLE (minimized windows still do).</param>
    /// <param name="isShellWindow">Whether the window is the shell's desktop window (Progman).</param>
    /// <param name="exStyle">The window's extended style bits.</param>
    /// <param name="isOwnProcess">Whether this process owns the window.</param>
    /// <param name="cloaked">The DWM cloaked attribute (non-zero for suspended UWP ghosts).</param>
    /// <param name="titleLength">The window title length in characters.</param>
    /// <returns>Whether the window belongs in an alt-tab-style list.</returns>
    public static bool PassesSwitchableFilter(
        bool isVisible, bool isShellWindow, int exStyle, bool isOwnProcess, uint cloaked, int titleLength)
        => isVisible
            && !isShellWindow
            && (exStyle & NativeMethods.WsExToolWindow) == 0
            && !isOwnProcess
            && cloaked == 0
            && titleLength > 0;

    [UnmanagedCallersOnly]
    private static int ListWindowsProc(nint hWnd, nint lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is not ListState state)
        {
            return 0;
        }
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        // Cloak query failure counts as not cloaked, matching the old inline check.
        var cloaked = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DwmWaCloaked, out var value, 4) == 0
            ? value
            : 0u;
        var buffer = new char[256];
        var length = NativeMethods.GetWindowTextW(hWnd, buffer, buffer.Length);
        if (!PassesSwitchableFilter(
                NativeMethods.IsWindowVisible(hWnd),
                // Explorer's Progman is visible, plain-styled, and titled "Program
                // Manager", yet real Alt-Tab never offers it.
                hWnd == state.ShellWindow,
                NativeMethods.GetWindowLong(hWnd, NativeMethods.GwlExStyle),
                pid == state.OwnPid,
                cloaked,
                length))
        {
            return 1;
        }
        state.Result.Add(new AppWindow(hWnd, new string(buffer, 0, length), pid)
        {
            IsMinimized = NativeMethods.IsIconic(hWnd),
        });
        return 1;
    }

    /// <summary>UnmanagedCallersOnly callbacks cannot capture state, so it travels
    /// through EnumWindows' lParam as a GCHandle — one pattern for both callbacks,
    /// no shared statics, no lock.</summary>
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

    /// <summary>Best-effort focus. Against an elevated window SetForegroundWindow may
    /// fail silently under UIPI — callers should prefer protocol re-activation.</summary>
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
}
