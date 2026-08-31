using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.Interop;

/// <summary>Converts a local DOS path to the NT device notation kernel drivers consume.</summary>
internal static class NativeDevicePath
{
    /// <summary>Returns an NT device path, or the normalized input when Windows cannot translate it.</summary>
    internal static string FromDosPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        string? root = Path.GetPathRoot(fullPath);
        if (root is null || root.Length < 2 || root[1] != ':')
        {
            WSGM.Core.Log.Warn(
                $"NT device-path conversion skipped: application path is not on a local drive ({fullPath}).");
            return fullPath;
        }

        StringBuilder target = new(1024);
        if (QueryDosDevice(root[..2], target, target.Capacity) == 0 || target.Length == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            WSGM.Core.Log.Warn(
                $"NT device-path conversion failed for {root[..2]} with Win32 error {error}; "
                + "HidHide readability may be unavailable.");
            return fullPath;
        }

        return target + fullPath[2..];
    }

    // StringBuilder matches QueryDosDevice's variable-length MULTI_SZ. The first mapping is the
    // active drive target, which is the one HidHide compares against.
    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint QueryDosDevice(
        string deviceName,
        StringBuilder targetPath,
        int maxLength);
}
