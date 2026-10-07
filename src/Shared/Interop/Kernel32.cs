// Shared between WSGM and Device Lab (linked as a source file). Declarations only.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Interop;

/// <summary>Kernel32 file-handle imports and constants shared by the interop classes.</summary>
internal static partial class Kernel32
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorInsufficientBuffer = 122;

    /// <summary>Opens a file or device into a handle that closes itself.</summary>
    /// <param name="fileName">File, directory, or device path passed to CreateFileW.</param>
    /// <param name="desiredAccess">Requested Win32 access mask; zero requests metadata access only.</param>
    /// <param name="shareMode">Win32 share flags controlling subsequent opens.</param>
    /// <param name="securityAttributes">Borrowed SECURITY_ATTRIBUTES pointer, or zero for default security and a noninheritable handle.</param>
    /// <param name="creationDisposition">Win32 create/open disposition.</param>
    /// <param name="flagsAndAttributes">File attributes and open flags.</param>
    /// <param name="templateFile">Borrowed template handle for file creation, or zero.</param>
    /// <returns>Caller-owned safe handle; inspect IsInvalid and the last P/Invoke error on failure.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>
    ///     Opens a file or device into a raw handle, for callers that inspect
    ///     INVALID_HANDLE_VALUE and the last error before taking ownership.
    /// </summary>
    /// <param name="fileName">File, directory, or device path passed to CreateFileW.</param>
    /// <param name="desiredAccess">Requested Win32 access mask; zero requests metadata access only.</param>
    /// <param name="shareMode">Win32 share flags controlling subsequent opens.</param>
    /// <param name="securityAttributes">Borrowed SECURITY_ATTRIBUTES pointer, or zero for default security and a noninheritable handle.</param>
    /// <param name="creationDisposition">Win32 create/open disposition.</param>
    /// <param name="flagsAndAttributes">File attributes and open flags.</param>
    /// <param name="templateFile">Borrowed template handle for file creation, or zero.</param>
    /// <returns>Caller-owned raw handle to close, or INVALID_HANDLE_VALUE (-1) on failure; inspect the last P/Invoke error.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFileHandleW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}
