using System.Runtime.InteropServices;

namespace WSGM.PackagedLaunch;

internal static partial class ManagedDirectNative
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateIoCompletionPort(nint file, nint existing, nuint key, uint threads);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetQueuedCompletionStatus(nint port, out uint message, out nuint key,
        out nint overlapped, uint milliseconds);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(nint owner, string text, string caption, uint flags);
}
