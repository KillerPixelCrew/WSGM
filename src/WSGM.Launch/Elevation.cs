using System.Runtime.InteropServices;

namespace WSGM.Launch;

internal static partial class Elevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private const int TokenElevationType = 18;
    private const uint TokenElevationTypeFull = 2;

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

    /// <summary>Reports whether this process runs with a FULL token that has a linked
    /// limited token — i.e. TOKEN_ELEVATION_TYPE is TokenElevationTypeFull, which only
    /// a split-token elevation produces. Returns <c>null</c> when the token cannot be
    /// queried. A UAC-disabled machine, a built-in Administrator and a standard user all
    /// report TokenElevationTypeDefault and therefore <c>false</c>: there is no limited
    /// token for Task Scheduler to hand out on any of them, which is exactly the
    /// condition the de-elevation fail-open exists to serve.</summary>
    internal static bool? HasLinkedLimitedToken()
    {
        if (OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token) == 0)
        {
            return null;
        }

        try
        {
            return GetTokenInformation(token, TokenElevationType, out var elevationType,
                sizeof(uint), out _) != 0
                ? elevationType == TokenElevationTypeFull
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
