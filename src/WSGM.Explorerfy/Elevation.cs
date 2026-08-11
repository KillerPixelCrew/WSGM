using System.Runtime.InteropServices;

namespace WSGM.Explorerfy;

/// <summary>Whether this process runs with an elevated (high-integrity) token.
/// Diagnostic only: an elevated wrapper vs the shell's integrity decides whether
/// the log file and the shell pipe are reachable.</summary>
internal static partial class Elevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        nint tokenHandle, int tokenInformationClass, out uint tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    internal static bool? IsCurrentProcessElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            return null;
        }
        try
        {
            return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(uint), out _)
                ? elevated != 0
                : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
