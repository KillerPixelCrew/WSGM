using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Interop;

/// <summary>Stable filesystem identity for one existing path.</summary>
internal readonly record struct NativePathIdentity(uint VolumeSerialNumber, ulong FileId);

/// <summary>Reads filesystem identity without following application-owned path conventions.</summary>
internal static partial class NativePathIdentityReader
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    /// <summary>Returns the identity of an existing file or directory, or null when it is absent.</summary>
    internal static NativePathIdentity? Read(string path)
    {
        nint rawHandle = CreateFileW(
            path,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagBackupSemantics,
            0);
        if (rawHandle == -1)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            throw new IOException(
                $"Could not inspect filesystem identity for '{path}'.",
                new Win32Exception(error));
        }
        using SafeFileHandle handle = new(rawHandle, ownsHandle: true);

        if (GetFileInformationByHandle(
            handle.DangerousGetHandle(),
            out ByHandleFileInformation information) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Could not read filesystem identity for '{path}'.",
                new Win32Exception(error));
        }

        ulong fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new NativePathIdentity(information.VolumeSerialNumber, fileId);
    }

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

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetFileInformationByHandle(
        nint file,
        out ByHandleFileInformation information);
}
