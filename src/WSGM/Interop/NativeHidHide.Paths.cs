using System;
using System.IO;
using System.Runtime.InteropServices;
using WSGM.Core;

namespace WSGM.Interop;

// WSGM-only part of NativeHidHide: it logs through WSGM.Core, which Device Lab does not link.
internal static partial class NativeHidHide
{
    /// <summary>
    ///     Converts a local DOS path to the NT device notation kernel
    ///     drivers (HidHide) consume. Returns the normalized input when Windows
    ///     cannot translate it, logging why.
    /// </summary>
    /// <param name="path">The DOS path to translate.</param>
    internal static unsafe string FromDosPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        var root = Path.GetPathRoot(fullPath);
        if (root is null || root.Length < 2 || root[1] != ':')
        {
            Log.Warn(
                $"NT device-path conversion skipped: application path is not on a local drive ({fullPath}).");
            return fullPath;
        }

        // QueryDosDevice returns a MULTI_SZ; the first mapping is the active
        // drive target, which is the one HidHide compares against.
        var buffer = stackalloc char[1024];
        var target = QueryDosDeviceW(root[..2], buffer, 1024) == 0
            ? ""
            : BoundedString(new ReadOnlySpan<char>(buffer, 1024));
        if (target.Length != 0)
        {
            return target + fullPath[2..];
        }

        Log.Warn(
            $"NT device-path conversion failed for {root[..2]} with Win32 error "
            + $"{Marshal.GetLastPInvokeError()}; HidHide readability may be unavailable.");
        return fullPath;
    }

    /// <summary>Decodes a UTF-16 buffer up to its first NUL, never reading past its end.</summary>
    private static string BoundedString(ReadOnlySpan<char> buffer)
    {
        var end = buffer.IndexOf('\0');
        return new string(end >= 0 ? buffer[..end] : buffer);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint QueryDosDeviceW(string deviceName, char* targetPath, uint maxLength);
}
