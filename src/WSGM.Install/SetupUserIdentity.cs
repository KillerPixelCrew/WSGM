using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WSGM.Install;

/// <summary>Refuses per-user setup work under another account's elevation credentials.</summary>
public static partial class SetupUserIdentity
{
    /// <summary>Compares complete Windows account names without changing their domain identity.</summary>
    /// <param name="interactiveAccount">The account signed into the setup session.</param>
    /// <param name="processAccount">The account running setup.</param>
    /// <returns>Whether both names identify the same account.</returns>
    public static bool Matches(string? interactiveAccount, string? processAccount)
    {
        return !string.IsNullOrWhiteSpace(interactiveAccount)
               && !string.IsNullOrWhiteSpace(processAccount)
               && string.Equals(interactiveAccount, processAccount, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks the setup session before setup modifies another account's settings.</summary>
    /// <exception cref="InvalidOperationException">The session account is unknown or differs.</exception>
    public static void RequireCurrentSessionUser()
    {
        using var process = Process.GetCurrentProcess();
        var user = ReadSessionText(process.SessionId, 5);
        var domain = ReadSessionText(process.SessionId, 7);
        using var identity = WindowsIdentity.GetCurrent();
        var account = string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(domain)
            ? null : domain + "\\" + user;
        if (!Matches(account, identity.Name))
        {
            throw new InvalidOperationException(
                "Run setup from the Windows account that uses WSGM, using that account's administrator credentials.");
        }
    }

    private static string? ReadSessionText(int sessionId, int informationClass)
    {
        if (!WTSQuerySessionInformationW(0, sessionId, informationClass, out var buffer, out var bytes))
        {
            return null;
        }

        try
        {
            return buffer != 0 && bytes >= sizeof(char) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, int sessionId, int informationClass,
        out nint buffer, out int bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
