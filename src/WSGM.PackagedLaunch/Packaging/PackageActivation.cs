using System;
using System.Runtime.InteropServices;

namespace WSGM.PackagedLaunch;

/// <summary>What activation produced.</summary>
/// <param name="Succeeded">Whether the application was activated.</param>
/// <param name="SeedProcessId">The process Windows created, or zero.</param>
/// <param name="Detail">What happened, fit for the log.</param>
internal sealed record ActivationResult(bool Succeeded, int SeedProcessId, string Detail);

/// <summary>Activates a packaged application through the Application Activation Manager.</summary>
/// <remarks>
///     <para>
///         AAM only. The spike also tried Explorer's <c>shell:AppsFolder</c> path, a PowerShell hop
///         and a direct executable start; all three are recorded failures. The shell paths hand the
///         request to Explorer and return no process at all, and starting the executable directly
///         made the title replace its own process and drop out of Steam's tracking.
///     </para>
///     <para>
///         The process id AAM returns is a seed, not the game. A packaged title can hand off between
///         its own binaries, and for the GDK shape the seed is the launch helper.
///     </para>
/// </remarks>
internal static class PackageActivation
{
    /// <summary>Activates one application.</summary>
    /// <param name="aumid">The application user model id.</param>
    /// <param name="arguments">Arguments for the application, or null.</param>
    /// <returns>What happened.</returns>
    internal static ActivationResult Activate(string aumid, string? arguments)
    {
        object? manager = null;
        try
        {
            manager = new NativeMethods.ApplicationActivationManager();
            var activator = (NativeMethods.IApplicationActivationManager)manager;

            // Without this the activated app is denied the foreground, which from Big Picture looks
            // exactly like a launch that did nothing.
            var allowed = NativeMethods.CoAllowSetForegroundWindow(manager, IntPtr.Zero);
            if (allowed < 0)
            {
                PackagedLaunchLog.Warn(
                    $"The game may not be allowed to take the foreground: 0x{allowed:X8}");
            }

            var result = activator.ActivateApplication(
                aumid,
                string.IsNullOrEmpty(arguments) ? null : arguments,
                NativeMethods.AoNoErrorUi | NativeMethods.AoNoSplashScreen,
                out var processId);

            if (result < 0)
            {
                return new ActivationResult(false, 0,
                    $"Windows refused to activate {aumid}: 0x{result:X8} "
                    + $"({new COMException(string.Empty, result).Message.Trim()})");
            }

            return new ActivationResult(true, (int)processId, $"Activation returned process {processId}.");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return new ActivationResult(false, 0, $"Activation failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager))
            {
                Marshal.FinalReleaseComObject(manager);
            }
        }
    }
}
