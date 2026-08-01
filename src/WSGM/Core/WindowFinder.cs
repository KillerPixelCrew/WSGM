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

    public sealed record AppWindow(nint Hwnd, string Title, uint ProcessId);

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

    [UnmanagedCallersOnly]
    private static int ListWindowsProc(nint hWnd, nint lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is not ListState state)
        {
            return 0;
        }
        if (!NativeMethods.IsWindowVisible(hWnd))
        {
            return 1;
        }
        // Explorer's Progman is visible, plain-styled, and titled "Program Manager",
        // yet real Alt-Tab never offers it.
        if (hWnd == state.ShellWindow)
        {
            return 1;
        }
        if ((NativeMethods.GetWindowLong(hWnd, NativeMethods.GwlExStyle) & NativeMethods.WsExToolWindow) != 0)
        {
            return 1;
        }
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == state.OwnPid)
        {
            return 1;
        }
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DwmWaCloaked, out var cloaked, 4) == 0 && cloaked != 0)
        {
            return 1;
        }
        var buffer = new char[256];
        var length = NativeMethods.GetWindowTextW(hWnd, buffer, buffer.Length);
        if (length <= 0)
        {
            return 1;
        }
        state.Result.Add(new AppWindow(hWnd, new string(buffer, 0, length), pid));
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
