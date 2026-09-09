using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Places a window on the monitor identified by a current GDI display name.</summary>
internal static unsafe partial class DisplayWindowPlacement
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Bounds
    {
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal Bounds Monitor;
        internal Bounds Work;
        internal uint Flags;
        internal fixed char Device[32];
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MonitorCallback(nint monitor, nint dc, nint rectangle, nint data);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static partial int GetMonitorInfo(nint monitor, MonitorInfo* info);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowRect(nint window, out Bounds rectangle);

    internal static bool TryPlace(nint window, string sourceName)
    {
        Bounds? target = null;
        MonitorCallback callback = (monitor, _, _, _) =>
        {
            MonitorInfo info = new() { Size = (uint)sizeof(MonitorInfo) };
            if (GetMonitorInfo(monitor, &info) != 0
                && string.Equals(new string(info.Device), sourceName, StringComparison.OrdinalIgnoreCase))
            { target = info.Monitor; }
            return 1;
        };
        if (EnumDisplayMonitors(0, 0, callback, 0) == 0 || target is not { } bounds) { return false; }
        if (SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top, 0x0014) == 0) { return false; }
        return GetWindowRect(window, out var actual) != 0 && actual.Left == bounds.Left && actual.Top == bounds.Top
            && actual.Right == bounds.Right && actual.Bottom == bounds.Bottom;
    }
}
