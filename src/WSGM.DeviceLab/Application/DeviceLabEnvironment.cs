using System;
using System.Security.Principal;

namespace WSGM.DeviceLab.Application;

/// <summary>Process facts the safety preflight reads.</summary>
internal static class DeviceLabEnvironment
{
    /// <summary>Whether the current process token is in the Administrators role.</summary>
    /// <returns>True when elevated.</returns>
    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Whether CI or GITHUB_ACTIONS is set to 1, true or yes, in any letter case.</summary>
    /// <returns>True inside continuous integration.</returns>
    internal static bool IsContinuousIntegration() =>
        IsTruthy(Environment.GetEnvironmentVariable("CI"))
        || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
