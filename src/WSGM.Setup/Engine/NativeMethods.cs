using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WSGM.Setup.Engine;

/// <summary>The few Win32 calls setup needs that .NET does not wrap.</summary>
internal static partial class NativeMethods
{
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const int ErrorServiceDoesNotExist = 1060;
    internal const uint ServiceStopped = 0x00000001;
    internal const uint ServiceRunning = 0x00000004;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenServiceW(nint hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatus(nint hService, out ServiceStatus lpServiceStatus);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint hSCObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileExW(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    internal const uint MoveFileDelayUntilReboot = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    /// <summary>Shell link, for Start menu and desktop shortcuts.</summary>
    [GeneratedComInterface]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(nint pszFile, int cch, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription(nint pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(nint pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(nint pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(nint pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>Saves a shell link to disk.</summary>
    [GeneratedComInterface]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile(out nint ppszFileName);
    }

    internal static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid,
        out nint ppv);
}
