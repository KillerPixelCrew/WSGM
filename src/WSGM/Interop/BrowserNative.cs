using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Interop;

/// <summary>Narrow native primitives shared with the unelevated browser host.</summary>
internal static partial class BrowserNative
{
    internal static void SetMediumPipeLabel(SafePipeHandle pipe)
    {
        // PipeSecurity treats every SACL as auditing and requests ACCESS_SYSTEM_SECURITY.
        // A mandatory label instead uses LABEL_SECURITY_INFORMATION and WRITE_OWNER only.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;;NW;;;ME)", 1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || !present)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var error = SetSecurityInfo(pipe, 6, 0x10, 0, 0, 0, sacl);
            if (error != 0)
            {
                throw new Win32Exception((int)error, "Could not label the browser pipe for medium-integrity IPC.");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    internal static bool IsMediumIntegrity(int processId)
    {
        using var process = OpenProcess(0x1000, false, (uint)processId);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token))
        {
            return false;
        }

        using (token)
        {
            GetTokenInformation(token, 25, 0, 0, out var size);
            if (size is < 8 or > 4096)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, size, out _))
                {
                    return false;
                }

                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                if (count == 0)
                {
                    return false;
                }

                var level = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                return level is >= 0x2000 and < 0x3000;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    internal static void Attach(nint child, nint parent, int processId)
    {
        GetWindowThreadProcessId(child, out var owner);
        if (child == 0 || parent == 0 || owner != processId || !IsWindow(child))
        {
            throw new InvalidOperationException("The browser window does not belong to the verified helper.");
        }

        // Cross-process reparenting with differing awareness can silently reset the helper's
        // DPI context. Refuse that boundary instead of corrupting the Overlay's input geometry.
        if (!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(child), GetWindowDpiAwarenessContext(parent)))
        {
            throw new InvalidOperationException("Browser and Overlay display scaling contexts do not match.");
        }

        ShowWindow(child, 0);
        var style = GetWindowLongPtrW(child, -16).ToInt64();
        SetWindowLongPtrW(child, -16, (nint)((style & ~0x80000000L) | 0x46000000L));
        Marshal.SetLastPInvokeError(0);
        var previous = SetParent(child, parent);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not embed the browser window.");
        }
    }

    internal static void Detach(nint child)
    {
        if (child == 0 || !IsWindow(child))
        {
            return;
        }

        ShowWindow(child, 0);
        SetParent(child, 0);
        var style = GetWindowLongPtrW(child, -16).ToInt64();
        SetWindowLongPtrW(child, -16, (nint)((style & ~0x40000000L) | 0x80000000L));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorSacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out nint sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [LibraryImport("advapi32.dll")]
    private static partial uint SetSecurityInfo(SafePipeHandle handle, int objectType, uint information, nint owner, nint group, nint dacl, nint sacl);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, nint data, int size, out int required);

    [LibraryImport("advapi32.dll")]
    private static partial nint GetSidSubAuthorityCount(nint sid);

    [LibraryImport("advapi32.dll")]
    private static partial nint GetSidSubAuthority(nint sid, uint index);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetParent(nint child, nint parent);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint window, out WindowRect rectangle);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowDpiAwarenessContext(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
