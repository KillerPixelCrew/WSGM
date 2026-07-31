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

    private static SearchState? _state;
    private static readonly object Gate = new();

    public static HashSet<uint> FindProcessIds(string semicolonNames)
    {
        var result = new HashSet<uint>();
        foreach (var name in semicolonNames.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var plain = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            foreach (var p in Process.GetProcessesByName(plain))
            {
                try { result.Add((uint)p.Id); } catch { } finally { p.Dispose(); }
            }
        }
        return result;
    }

    public static nint FindWindow(string processNames, string? windowClass)
    {
        var pids = FindProcessIds(processNames);
        if (pids.Count == 0)
        {
            return 0;
        }

        lock (Gate)
        {
            _state = new SearchState { ProcessIds = pids, WindowClass = string.IsNullOrWhiteSpace(windowClass) ? null : windowClass };
            unsafe
            {
                delegate* unmanaged<nint, nint, int> callback = &EnumWindowsProc;
                NativeMethods.EnumWindows((nint)callback, 0);
            }
            var found = _state.Found;
            _state = null;
            return found;
        }
    }

    [UnmanagedCallersOnly]
    private static int EnumWindowsProc(nint hWnd, nint lParam)
    {
        var state = _state;
        if (state is null)
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

    private static List<AppWindow>? _listResult;
    private static uint _listOwnPid;

    /// <summary>Alt-tab style enumeration: visible, titled, top-level windows that
    /// are not tool windows, not DWM-cloaked (suspended UWP ghosts), and not ours.
    /// Z-order top first.</summary>
    public static List<AppWindow> ListSwitchableWindows()
    {
        lock (Gate)
        {
            _listResult = [];
            _listOwnPid = (uint)Environment.ProcessId;
            unsafe
            {
                delegate* unmanaged<nint, nint, int> callback = &ListWindowsProc;
                NativeMethods.EnumWindows((nint)callback, 0);
            }
            var result = _listResult;
            _listResult = null;
            return result;
        }
    }

    [UnmanagedCallersOnly]
    private static int ListWindowsProc(nint hWnd, nint lParam)
    {
        var list = _listResult;
        if (list is null)
        {
            return 0;
        }
        if (!NativeMethods.IsWindowVisible(hWnd))
        {
            return 1;
        }
        if ((NativeMethods.GetWindowLong(hWnd, NativeMethods.GwlExStyle) & NativeMethods.WsExToolWindow) != 0)
        {
            return 1;
        }
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == _listOwnPid)
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
        list.Add(new AppWindow(hWnd, new string(buffer, 0, length), pid));
        return 1;
    }

    /// <summary>Best-effort focus. Against an elevated window SetForegroundWindow may
    /// fail silently under UIPI — callers should prefer protocol re-activation.</summary>
    public static void BringToForeground(nint hWnd)
    {
        if (hWnd == 0)
        {
            return;
        }
        NativeMethods.ShowWindow(hWnd, NativeMethods.SwRestore);
        NativeMethods.SetForegroundWindow(hWnd);
    }
}
