using System;
using System.Security;
using System.Security.Principal;

namespace WSGM.DeviceLab.Application;

/// <summary>Process facts every Device Lab role reads: elevation and continuous integration.</summary>
internal static class DeviceLabEnvironment
{
    /// <summary>Whether the current process token is in the Administrators role.</summary>
    /// <returns>True when elevated; false when the token cannot be queried.</returns>
    internal static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether CI or GITHUB_ACTIONS is set to 1, true or yes, in any letter case.</summary>
    /// <returns>True inside continuous integration.</returns>
    internal static bool IsContinuousIntegration()
    {
        return IsContinuousIntegration(Environment.GetEnvironmentVariable);
    }

    /// <summary>The same check over a given set of variables, so it can be tested without the process's.</summary>
    /// <param name="variable">Reads one variable by name.</param>
    /// <returns>True inside continuous integration.</returns>
    internal static bool IsContinuousIntegration(Func<string, string?> variable)
    {
        return IsTruthy(variable("CI")) || IsTruthy(variable("GITHUB_ACTIONS"));
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
