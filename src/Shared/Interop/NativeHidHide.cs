// Shared between WSGM and Device Lab (linked as a source file): HidHide's control device, declared
// once. It depends only on Kernel32 and logs nothing, so either project can compile it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static WSGM.Interop.Kernel32;

namespace WSGM.Interop;

internal static partial class NativeHidHide
{
    private const string ControlDevice = @"\\.\HidHide";
    private const uint ShareReadWriteDelete = 0x00000007;
    private const int InitialBufferBytes = 4096;
    private const int ErrorInvalidData = 13;
    private const int ErrorMoreData = 234;

    // These values are the CTL_CODE values published by HidHide's FilterDriverProxy.
    internal const uint GetApplications = 0x80016000;
    internal const uint SetApplications = 0x80016004;
    internal const uint GetDevices = 0x80016008;
    internal const uint SetDevices = 0x8001600C;
    internal const uint GetActive = 0x80016010;
    internal const uint SetActive = 0x80016014;
    internal const uint GetInverse = 0x80016018;

    internal static bool TryOpen(out SafeFileHandle handle, out int error)
    {
        handle = CreateFileW(
            ControlDevice,
            GenericRead,
            ShareReadWriteDelete,
            0,
            OpenExisting,
            0,
            0);
        if (!handle.IsInvalid)
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return false;
    }

    internal static unsafe bool TryReadBoolean(
        SafeFileHandle handle,
        uint controlCode,
        out bool value,
        out int error)
    {
        byte raw = 0;
        var success = DeviceIoControl(
            handle,
            controlCode,
            null,
            0,
            &raw,
            1,
            out var returned,
            0);
        if (!success || returned != 1)
        {
            value = false;
            error = success ? ErrorInvalidData : Marshal.GetLastPInvokeError();
            return false;
        }

        value = raw != 0;
        error = 0;
        return true;
    }

    internal static unsafe bool TryWriteBoolean(
        SafeFileHandle handle,
        uint controlCode,
        bool value,
        out int error)
    {
        var raw = value ? (byte)1 : (byte)0;
        var success = DeviceIoControl(
            handle,
            controlCode,
            &raw,
            1,
            null,
            0,
            out _,
            0);
        error = success ? 0 : Marshal.GetLastPInvokeError();
        return success;
    }

    internal static unsafe bool TryReadMultiString(
        SafeFileHandle handle,
        uint controlCode,
        out IReadOnlyList<string> values,
        out int error)
    {
        // The list is the user's own HidHide configuration, so it is read whole: the buffer doubles until
        // the driver fits, bounded only by the largest array the runtime can allocate.
        for (var size = InitialBufferBytes; size <= Array.MaxLength / 2; size *= 2)
        {
            var buffer = new byte[size];
            uint returned;
            bool success;
            fixed (byte* output = buffer)
            {
                success = DeviceIoControl(
                    handle,
                    controlCode,
                    null,
                    0,
                    output,
                    (uint)buffer.Length,
                    out returned,
                    0);
            }

            if (success)
            {
                if (returned <= (uint)buffer.Length && (returned & 1) == 0)
                {
                    return TryDecodeMultiString(buffer.AsSpan(0, (int)returned), out values, out error);
                }

                values = [];
                error = ErrorInvalidData;
                return false;
            }

            error = Marshal.GetLastPInvokeError();
            if (error is ErrorInsufficientBuffer or ErrorMoreData)
            {
                continue;
            }

            values = [];
            return false;
        }

        values = [];
        error = ErrorMoreData;
        return false;
    }

    internal static unsafe bool TryWriteMultiString(
        SafeFileHandle handle,
        uint controlCode,
        IReadOnlyList<string> values,
        out int error)
    {
        ArgumentNullException.ThrowIfNull(values);
        var buffer = EncodeMultiString(values);
        bool success;
        fixed (byte* input = buffer)
        {
            success = DeviceIoControl(
                handle,
                controlCode,
                input,
                (uint)buffer.Length,
                null,
                0,
                out _,
                0);
        }

        error = success ? 0 : Marshal.GetLastPInvokeError();
        return success;
    }

    private static bool TryDecodeMultiString(
        ReadOnlySpan<byte> bytes,
        out IReadOnlyList<string> values,
        out int error)
    {
        if (bytes.Length == 0)
        {
            values = [];
            error = 0;
            return true;
        }

        var text = Encoding.Unicode.GetString(bytes);
        List<string> result = [];
        var start = 0;
        while (start < text.Length)
        {
            var terminator = text.IndexOf('\0', start);
            if (terminator < 0)
            {
                values = [];
                error = ErrorInvalidData;
                return false;
            }

            if (terminator == start)
            {
                values = result;
                error = 0;
                return true;
            }

            result.Add(text[start..terminator]);
            start = terminator + 1;
        }

        values = [];
        error = ErrorInvalidData;
        return false;
    }

    private static byte[] EncodeMultiString(IReadOnlyList<string> values)
    {
        StringBuilder builder = new();
        foreach (var value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.Contains('\0'))
            {
                throw new ArgumentException("HidHide entries cannot contain NUL characters.",
                    nameof(values));
            }

            builder.Append(value);
            builder.Append('\0');
        }

        builder.Append('\0');
        if (values.Count == 0)
        {
            builder.Append('\0');
        }

        return Encoding.Unicode.GetBytes(builder.ToString());
    }

    /// <summary>
    ///     Converts a local DOS path to the NT device notation kernel drivers (HidHide) consume.
    /// </summary>
    /// <param name="path">The DOS path to translate.</param>
    /// <returns>
    ///     The converted path. When Windows cannot translate it, the normalized input with the Win32
    ///     error, or with the reason it was skipped, so the caller can log why.
    /// </returns>
    internal static unsafe (string Path, int Error, string? Skipped) FromDosPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return (fullPath, 0, null);
        }

        var root = Path.GetPathRoot(fullPath);
        if (root is null || root.Length < 2 || root[1] != ':')
        {
            return (fullPath, 0, $"application path is not on a local drive ({fullPath})");
        }

        // QueryDosDevice returns a MULTI_SZ; the first mapping is the active
        // drive target, which is the one HidHide compares against.
        var buffer = stackalloc char[1024];
        var target = QueryDosDeviceW(root[..2], buffer, 1024) == 0
            ? ""
            : BoundedString(new ReadOnlySpan<char>(buffer, 1024));
        if (target.Length != 0)
        {
            return (target + fullPath[2..], 0, null);
        }

        var error = Marshal.GetLastPInvokeError();
        return (fullPath, error == 0 ? ErrorInvalidData : error, null);
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        void* inputBuffer,
        uint inputBufferSize,
        void* outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
