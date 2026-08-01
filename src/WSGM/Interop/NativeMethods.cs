using System.Runtime.InteropServices;

namespace WSGM.Interop;

internal static partial class NativeMethods
{
    // ---- Shell / desktop detection ----
    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    // ---- Hotkey ----
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNoRepeat = 0x4000;
    internal const uint WmHotkey = 0x0312;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);

    // ---- Low-level keyboard hook (shortcut recording only — see KeyRecorder) ----
    internal const int WhKeyboardLl = 13;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static partial nint SetWindowsHookExW(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    internal static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    // ---- Message-only window ----
    internal const nint HwndMessage = -3;

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct WndClassW
    {
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    internal static unsafe partial ushort RegisterClassW(WndClassW* lpWndClass);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetModuleHandleW(nint lpModuleName);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    // ---- Raw touch input (edge swipes) ----
    internal const ushort HidUsagePageGenericDesktop = 0x01;
    internal const ushort HidUsagePageDigitizer = 0x0D;
    internal const ushort HidUsageTouchScreen = 0x04;
    internal const ushort HidUsageX = 0x30;
    internal const ushort HidUsageY = 0x31;
    internal const ushort HidUsageTipSwitch = 0x42;
    internal const uint RidevRemove = 0x00000001;
    internal const uint RidevInputSink = 0x00000100;
    internal const uint RidevDevNotify = 0x00002000;
    internal const uint WmInput = 0x00FF;
    internal const uint WmInputDeviceChange = 0x00FE;
    internal const nint GidcArrival = 1;
    internal const nint GidcRemoval = 2;
    internal const uint RidInput = 0x10000003;
    internal const uint RidiPreparsedData = 0x20000005;
    internal const uint RimTypeHid = 2;
    internal const int HidpStatusSuccess = 0x00110000;
    internal const int HidpInput = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    /// <summary>The RAWHID payload follows this header in the WM_INPUT buffer:
    /// uint dwSizeHid; uint dwCount; byte bRawData[dwSizeHid * dwCount].</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputHeader
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterRawInputDevices(
        RawInputDevice[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [LibraryImport("user32.dll")]
    internal static partial uint GetRawInputData(
        nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    internal static partial uint GetRawInputDeviceInfoW(
        nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>HIDP_VALUE_CAPS (72 bytes). The trailing fields are the Range variant
    /// of the union; for NotRange caps, UsageMin holds the single usage.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpValueCaps
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2a;
        public ushort Reserved2b;
        public ushort Reserved2c;
        public ushort Reserved2d;
        public ushort Reserved2e;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetCaps(nint preparsedData, out HidpCaps capabilities);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetValueCaps(
        int reportType, [Out] HidpValueCaps[] valueCaps, ref ushort valueCapsLength, nint preparsedData);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetUsageValue(
        int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, nint preparsedData, nint report, uint reportLength);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetUsages(
        int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, nint preparsedData, nint report, uint reportLength);

    // ---- Window finding / focus ----
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(nint lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "RealGetWindowClassW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RealGetWindowClassW(nint hWnd, [Out] char[] pszType, uint cchType);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    internal const int SwRestore = 9;
    internal const int SwShowMaximized = 3;

    // ---- Touch-synthesized mouse message detection (overlay ghost-click eater) ----
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    /// <summary>GetMessageExtraInfo() upper bits marking touch/pen-synthesized mouse messages.</summary>
    internal const uint MiWpSignatureMask = 0xFFFFFF00;
    internal const uint MiWpSignature = 0xFF515700;

    [LibraryImport("user32.dll")]
    internal static partial nint GetMessageExtraInfo();

    // ---- Idle memory trim (Core\MemoryTrim) ----
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(nint hProcess);

    // ---- Boot-splash fade-out (layered-window alpha) ----
    internal const int WsExLayered = 0x00080000;
    internal const uint LwaAlpha = 0x00000002;

    // Ex-style is a 32-bit LONG even on x64 — SetWindowLongW, not the Ptr variant.
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static partial int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    // ---- Switchable-window enumeration (alt-tab style) ----
    internal const int GwlExStyle = -20;
    internal const int WsExToolWindow = 0x0080;
    internal const uint DwmWaCloaked = 14;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowTextW(nint hWnd, [Out] char[] text, int maxCount);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hWnd, uint attribute, out uint value, uint size);

    // ---- Update-exit event with explicit security (signalable from unelevated) ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint securityDescriptor, out uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateEventW(ref SecurityAttributes securityAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

    internal const uint Synchronize = 0x00100000;
    internal const uint EventModifyState = 0x0002;

    [LibraryImport("kernel32.dll", EntryPoint = "OpenEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenEventW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ResetEvent(nint eventHandle);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint mem);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    // ---- Elevation check of other processes ----
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevationClass = 20;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    // ---- Power ----
    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    // ---- Window icons (taskbar) ----
    internal const uint WmGetIcon = 0x007F;
    internal const uint WmQueryDragIcon = 0x0037;
    internal const nint IconSmall = 0;
    internal const nint IconBig = 1;
    internal const nint IconSmall2 = 2;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const int GclpHicon = -14;
    internal const int GclpHiconSm = -34;
    internal const uint DiMask = 0x0001;
    internal const uint DiNormal = 0x0003;
    internal const uint DibRgbColors = 0;
    internal const uint BiRgb = 0;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    // 64-bit-only entry point; the app ships win-x64 exclusively.
    [LibraryImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    internal static partial nint GetClassLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial nint CopyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawIconEx(
        nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth,
        uint istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ExtractIconExW(
        string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(
        nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    internal static unsafe partial nint CreateDIBSection(
        nint hdc, BitmapInfoHeader* pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    // ---- Tray host (Shell_TrayWnd) ----
    internal const uint WmCopyData = 0x004A;
    internal const uint WmWindowPosChanged = 0x0047;
    internal const uint WmContextMenu = 0x007B;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint NinSelect = 0x0400;
    internal const uint MsgfltAllow = 1;
    internal const uint WsPopup = 0x80000000;
    internal const uint WsChild = 0x40000000;
    internal const uint WsClipChildren = 0x02000000;
    internal const uint WsClipSiblings = 0x04000000;
    internal const uint WsExTopmost = 0x00000008;
    internal const nint HwndBroadcast = 0xFFFF;
    internal const int SwHide = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyDataStruct
    {
        public nint dwData;
        public uint cbData;
        public nint lpData;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeWindowMessageFilterEx(
        nint hwnd, uint message, uint action, nint pChangeFilterStruct);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessageW(string lpString);

    [LibraryImport("user32.dll", EntryPoint = "SendNotifyMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SendNotifyMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);
}
