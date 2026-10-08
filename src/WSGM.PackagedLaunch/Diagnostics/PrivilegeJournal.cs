using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using WSGM.Interop;

namespace WSGM.PackagedLaunch;

/// <summary>Records what each privileged step asked for and whether it got it.</summary>
/// <remarks>
///     Records only the first denied step, requested access, error, and process elevation.
///     It does not elevate the launcher or retry the operation.
/// </remarks>
internal sealed class PrivilegeJournal
{
    private bool _reported;

    /// <summary>Records one privileged step's outcome.</summary>
    /// <param name="step">What was being attempted.</param>
    /// <param name="requestedAccess">The access mask or right that was asked for.</param>
    /// <param name="granted">Whether it was granted.</param>
    /// <param name="error">The Win32 error when it was not.</param>
    internal void Record(string step, string requestedAccess, bool granted, int error = 0)
    {
        if (granted || _reported)
        {
            return;
        }

        _reported = true;
        PackagedLaunchLog.Warn(
            $"Refused: {step} asked for {requestedAccess} and was denied (error {error}). "
            + $"This launcher is running {Elevation()}. If the overlay did not work, this is why.");
    }

    /// <summary>Reports the identified game process's injector and query access for an attended diagnostic.</summary>
    /// <param name="processId">The game process.</param>
    internal static void ReportAccess(int processId)
    {
        var injector = NativeMethods.OpenProcess(NativeMethods.InjectorAccess, false, (uint)processId);
        var injectorError = injector == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (injector != IntPtr.Zero)
        {
            Win32Common.CloseHandle(injector);
        }

        var query = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        var queryError = query == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (query != IntPtr.Zero)
        {
            Win32Common.CloseHandle(query);
        }

        PackagedLaunchLog.Info(
            $"Access to process {processId}: injector rights "
            + $"{(injectorError == 0 ? "granted" : $"denied (error {injectorError})")}, "
            + $"query rights {(queryError == 0 ? "granted" : $"denied (error {queryError})")}. "
            + $"This launcher is running {Elevation()}.");
    }

    /// <summary>How this process is running, in the words a log reader needs.</summary>
    /// <returns>
    ///     Elevated or unelevated according to the current principal's administrator membership, or an
    ///     unreadable-elevation message.
    /// </returns>
    internal static string Elevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator) ? "elevated" : "unelevated";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return "at an unreadable elevation";
        }
    }
}
