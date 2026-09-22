using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.PackagedLaunch;

/// <summary>
///     The Windows entry points this launcher calls, grouped by what they are for.
/// </summary>
/// <remarks>
///     Declarations only: every policy decision lives in the caller. The spike's PEB group is gone
///     with the raw environment replacement it served, and so are the module-enumeration and
///     mitigation-policy groups, which only ever fed a transcript.
/// </remarks>
internal static class NativeMethods
{
    // ---- Process access ----
    internal const uint ProcessTerminate = 0x0001;
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessSetQuota = 0x0100;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint Synchronize = 0x0010_0000;

    /// <summary>What a remote DLL load needs, so a refusal names the mask it was refused.</summary>
    internal const uint InjectorAccess = 0x0002 | 0x0008 | ProcessVmRead | 0x0020 | 0x0400 | Synchronize;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        IntPtr process, out long creation, out long exit, out long kernel, out long user);

    // ---- Process enumeration ----
    internal const uint Th32CsSnapProcess = 0x0000_0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry32W
    {
        internal uint dwSize;
        internal uint cntUsage;
        internal uint th32ProcessID;
        internal IntPtr th32DefaultHeapID;
        internal uint th32ModuleID;
        internal uint cntThreads;
        internal uint th32ParentProcessID;
        internal int pcPriClassBase;
        internal uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

    // ---- Job containment ----
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint JobObjectLimitKillOnJobClose = 0x2000;
    internal const int JobObjectBasicAccountingInformationClass = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformationData
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicAccountingInformation
    {
        internal long TotalUserTime;
        internal long TotalKernelTime;
        internal long ThisPeriodTotalUserTime;
        internal long ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount;
        internal uint TotalProcesses;
        internal uint ActiveProcesses;
        internal uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        IntPtr job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(
        IntPtr job, int informationClass, IntPtr information, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    // ---- Package identity ----
    internal const int ErrorSuccess = 0;
    internal const int ErrorInsufficientBuffer = 122;
    internal const uint PackageFilterHead = 0x0000_0010;
    internal const uint PackageFilterDirect = 0x0000_0020;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetPackageFamilyName(
        IntPtr process, ref uint length, StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetPackagePathByFullName(
        string packageFullName, ref uint length, StringBuilder? path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int FindPackagesByPackageFamily(
        string packageFamilyName,
        uint packageFilters,
        ref uint count,
        IntPtr packageFullNames,
        ref uint bufferLength,
        IntPtr buffer,
        IntPtr packageProperties);

    // ---- Token ----
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevation = 20;
    internal const int TokenIntegrityLevel = 25;
    internal const int TokenIsAppContainer = 29;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);

    // Both return interior pointers into the SID, never a value: declaring the count as an int
    // would truncate the pointer on x64 and read garbage.
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    // ---- Process Lifetime Management ----
    // The interface a debugger attaches with. A package marked for debugging is exempt from PLM
    // suspension, which is the only reason a packaged game freezes behind an overlay.
    [ComImport]
    [Guid("F27C3930-8029-4AD1-94E3-3DBA417810C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPackageDebugSettings
    {
        [PreserveSig]
        int EnableDebugging(
            [MarshalAs(UnmanagedType.LPWStr)] string packageFullName,
            [MarshalAs(UnmanagedType.LPWStr)] string? debuggerCommandLine,
            IntPtr environment);

        [PreserveSig]
        int DisableDebugging([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);

        [PreserveSig]
        int Suspend([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);

        [PreserveSig]
        int Resume([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);

        [PreserveSig]
        int TerminateAllProcesses([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
    }

    [ComImport]
    [Guid("B1AEC16F-2383-4852-B0E9-8F0B1DC66B4D")]
    internal class PackageDebugSettings
    {
    }

    // ---- Package activation ----
    internal const int AoNoErrorUi = 0x2;
    internal const int AoNoSplashScreen = 0x4;

    [DllImport("ole32.dll")]
    internal static extern int CoAllowSetForegroundWindow(
        [MarshalAs(UnmanagedType.IUnknown)] object unknown, IntPtr reserved);

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            int options,
            out uint processId);

        // The remaining two slots exist only to keep the vtable layout correct.
        [PreserveSig]
        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string? verb,
            out uint processId);

        [PreserveSig]
        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    internal class ApplicationActivationManager
    {
    }

    // ---- Console ----
    internal const int SwHide = 0;

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr window, int command);

    internal delegate bool ConsoleCtrlHandler(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler? handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
