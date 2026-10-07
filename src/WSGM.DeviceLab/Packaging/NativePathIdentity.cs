using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static WSGM.Interop.Kernel32;

namespace WSGM.Interop;

/// <summary>Stable filesystem identity for one existing path.</summary>
/// <param name="VolumeSerialNumber">Volume identifier returned for the open handle.</param>
/// <param name="FileId">Combined high and low file-index words; compare together with the volume identifier.</param>
internal readonly record struct NativePathIdentity(uint VolumeSerialNumber, ulong FileId);

/// <summary>Stable identity and bounded metadata read from one already-open path handle.</summary>
/// <param name="Identity">Volume and file-index identity observed through the handle.</param>
/// <param name="Attributes">Windows file-attribute bits at the time of the read.</param>
/// <param name="Length">File length in bytes, checked to fit in a signed 64-bit value.</param>
internal readonly record struct NativePathInformation(
    NativePathIdentity Identity,
    uint Attributes,
    long Length);

/// <summary>Reads filesystem identity without following application-owned path conventions.</summary>
internal static partial class NativePathIdentityReader
{
    /// <summary>Returns the identity of an existing file or directory, or null when it is absent.</summary>
    /// <param name="path">Existing file or directory to open for metadata; the temporary handle is closed before return.</param>
    /// <returns>The observed identity, or null only for file/path-not-found errors; other inspection failures throw.</returns>
    internal static NativePathIdentity? Read(string path)
    {
        var rawHandle = CreateFileHandleW(
            path,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagBackupSemantics,
            0);
        if (rawHandle == -1)
        {
            var openError = Marshal.GetLastPInvokeError();
            if (openError is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            throw new IOException(
                $"Could not inspect filesystem identity for '{path}'.",
                new Win32Exception(openError));
        }

        using SafeFileHandle handle = new(rawHandle, true);

        if (!TryRead(handle, out var information, out var error))
        {
            throw new IOException(
                $"Could not read filesystem identity for '{path}'.",
                new Win32Exception(error));
        }

        return information.Identity;
    }

    /// <summary>Reads identity, attributes, and length from a borrowed open handle.</summary>
    /// <param name="handle">Caller-owned handle, kept open for this synchronous call; ownership is not transferred.</param>
    /// <param name="result">Observed metadata on success; default on failure.</param>
    /// <param name="error">Zero on success, otherwise the Win32 error or ERROR_FILE_TOO_LARGE for an unrepresentable length.</param>
    /// <returns>True when metadata was read and its length fits; false with an error otherwise.</returns>
    internal static bool TryRead(
        SafeFileHandle handle,
        out NativePathInformation result,
        out int error)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (GetFileInformationByHandle(
                handle.DangerousGetHandle(),
                out var information) == 0)
        {
            result = default;
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        var length = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (length > long.MaxValue)
        {
            result = default;
            error = 223; // ERROR_FILE_TOO_LARGE
            return false;
        }

        result = new NativePathInformation(
            new NativePathIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow),
            information.FileAttributes,
            (long)length);
        error = 0;
        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetFileInformationByHandle(
        nint file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
