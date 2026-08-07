using System.Runtime.InteropServices;

namespace WSGM.Deelevate;

internal static partial class Elevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    internal static bool? IsCurrentProcessElevated()
    {
        if (OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token) == 0)
        {
            return null;
        }

        try
        {
            return GetTokenInformation(token, TokenElevation, out var elevated,
                sizeof(uint), out _) != 0
                ? elevated != 0
                : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        out uint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint handle);
}
