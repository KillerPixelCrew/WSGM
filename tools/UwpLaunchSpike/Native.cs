using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Wsgm.UwpSpike;

/// Raw Win32/COM surface the spike needs. Kept in one file so the experiment code
/// below reads as experiment code rather than as interop.
internal static class Native
{
    internal const int MaxPath = 260;

    // ---- Process access rights ----
    internal const uint ProcessTerminate = 0x0001;
    internal const uint ProcessCreateThread = 0x0002;
    internal const uint ProcessVmOperation = 0x0008;
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessVmWrite = 0x0020;
    internal const uint ProcessDupHandle = 0x0040;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ProcessAllAccess = 0x001F_FFFF;
    internal const uint Synchronize = 0x0010_0000;

    // The set a conventional remote-DLL injector needs to do its work.
    internal const uint InjectorAccess =
        ProcessCreateThread | ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation;

    // ---- Toolhelp ----
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        internal string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process2(IntPtr hProcess, out ushort pProcessMachine, out ushort pNativeMachine);

    // ---- Package identity ----
    // Both return ERROR_SUCCESS / APPMODEL_ERROR_NO_PACKAGE (15700) / APPMODEL_ERROR_NO_APPLICATION (15703).
    internal const int ErrorSuccess = 0;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int AppModelErrorNoPackage = 15700;
    internal const int AppModelErrorNoApplication = 15703;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetPackageFamilyName(IntPtr hProcess, ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint applicationUserModelIdLength, StringBuilder? applicationUserModelId);

    // ---- Token ----
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevation = 20;
    internal const int TokenIntegrityLevel = 25;
    internal const int TokenIsAppContainer = 29;
    internal const int TokenAppContainerSid = 31;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    // Both return interior pointers into the SID, never a value: declaring the count as
    // an int would truncate the pointer on x64 and read garbage.
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    // ---- Mitigation policies ----
    // PROCESS_MITIGATION_POLICY values that bear on whether a foreign DLL can be loaded.
    internal const int ProcessDynamicCodePolicy = 2;
    internal const int ProcessExtensionPointDisablePolicy = 6;
    internal const int ProcessSignaturePolicy = 8;
    internal const int ProcessImageLoadPolicy = 10;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessMitigationPolicy(IntPtr hProcess, int MitigationPolicy, out uint lpBuffer, IntPtr dwLength);

    // ---- Modules ----
    internal const uint ListModulesAll = 0x03;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool K32EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint K32GetModuleFileNameExW(IntPtr hProcess, IntPtr hModule, StringBuilder lpFilename, uint nSize);

    // ---- Windows ----
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    internal const int SwHide = 0;

    // ---- COM / package activation ----
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    internal static extern int CoAllowSetForegroundWindow([MarshalAs(UnmanagedType.IUnknown)] object pUnk, IntPtr lpvReserved);

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        // HRESULT ActivateApplication(LPCWSTR, LPCWSTR, ACTIVATEOPTIONS, DWORD*)
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

    internal const int AoNone = 0x0;
    internal const int AoNoErrorUi = 0x2;
    internal const int AoNoSplashScreen = 0x4;
}
