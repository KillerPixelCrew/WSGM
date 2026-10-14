using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WSGM.Interop;

namespace WSGM.Launch;

/// <summary>Queries the wrapper token without changing its elevation.</summary>
internal static partial class Elevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private const int TokenElevationType = 18;
    private const uint TokenElevationTypeFull = 2;
    private const int TokenLinkedToken = 19;
    private const uint PrimaryTokenAccess = 0x0001 | 0x0002 | TokenQuery;

    /// <summary>Opens a verified limited primary token from this wrapper's own split token.</summary>
    /// <param name="error">The Windows error when querying or duplicating the token fails.</param>
    /// <returns>An owned limited token, or null when direct de-elevation is unavailable.</returns>
    internal static SafeAccessTokenHandle? OpenLimitedPrimaryToken(out int error)
    {
        error = 0;
        if (OpenProcessToken(GetCurrentProcess(), TokenQuery, out var current) == 0)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        try
        {
            if (GetTokenInformation(current, TokenElevationType, out var type, sizeof(uint), out _) == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            if (type != TokenElevationTypeFull)
            {
                return null;
            }

            if (GetLinkedTokenInformation(current, TokenLinkedToken, out var linked, nint.Size, out _) == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            try
            {
                if (GetTokenInformation(linked, TokenElevation, out var elevated, sizeof(uint), out _) == 0)
                {
                    error = Marshal.GetLastPInvokeError();
                    return null;
                }

                if (elevated != 0)
                {
                    error = 5; // Refuse an elevated token rather than silently defeating --deelevate.
                    return null;
                }

                if (!Win32Common.DuplicateTokenEx(linked, PrimaryTokenAccess, 0,
                        impersonationLevel: 2, tokenType: 1, out var primary))
                {
                    error = Marshal.GetLastPInvokeError();
                    return null;
                }

                return new SafeAccessTokenHandle(primary);
            }
            finally
            {
                Win32Common.CloseHandle(linked);
            }
        }
        finally
        {
            Win32Common.CloseHandle(current);
        }
    }

    /// <summary>Checks the current process token's elevation flag.</summary>
    /// <returns>True when elevated, false when limited, or null if the token cannot be queried.</returns>
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
            Win32Common.CloseHandle(token);
        }
    }

    /// <summary>
    ///     Reports whether this process runs with a FULL token that has a linked
    ///     limited token — i.e. TOKEN_ELEVATION_TYPE is TokenElevationTypeFull, which only
    ///     a split-token elevation produces. Returns <c>null</c> when the token cannot be
    ///     queried. A UAC-disabled machine, a built-in Administrator and a standard user all
    ///     report TokenElevationTypeDefault and therefore <c>false</c>: there is no limited
    ///     token for Task Scheduler to hand out on any of them, which is exactly the
    ///     condition the de-elevation fail-open exists to serve.
    /// </summary>
    /// <returns>True for a full split token, false for other token types, or null on query failure.</returns>
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
            Win32Common.CloseHandle(token);
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

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static partial int GetLinkedTokenInformation(
        nint tokenHandle, int tokenInformationClass, out nint tokenInformation,
        int tokenInformationLength, out int returnLength);
}
