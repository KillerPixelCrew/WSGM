using System;
using System.ComponentModel;
using System.Diagnostics;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Wizard;

/// <summary>How an elevation request ended.</summary>
internal enum WizardElevationOutcome
{
    /// <summary>This process is already elevated.</summary>
    AlreadyElevated,

    /// <summary>An elevated copy started; this process should exit.</summary>
    Relaunched,

    /// <summary>The tester declined the prompt; continue with reduced checks.</summary>
    Declined,

    /// <summary>The relaunch failed for another reason.</summary>
    Failed
}

/// <summary>
///     Starts the wizard elevated. HidHide, PawnIO, ACPI tables and the later hardware stages need an
///     administrator token, and a worker started from an elevated wizard inherits it.
/// </summary>
/// <remarks>
///     The relaunched copy gets <see cref="RelaunchedArgument" />, so a failed elevation can never loop.
///     The CLI and the developer tabs stay as-invoker.
/// </remarks>
internal static class WizardElevation
{
    /// <summary>Marks a copy that was already relaunched for elevation.</summary>
    public const string RelaunchedArgument = "--elevated-relaunch";

    private const int ErrorCancelled = 1223;

    /// <summary>Relaunches this executable elevated with the given arguments unless it already is.</summary>
    /// <param name="arguments">Arguments for the elevated copy, without the marker.</param>
    /// <param name="alreadyRelaunched">Whether this process is itself a relaunched copy.</param>
    /// <returns>What happened.</returns>
    public static WizardElevationOutcome EnsureElevated(string[] arguments, bool alreadyRelaunched)
    {
        if (DeviceLabEnvironment.IsElevated())
        {
            return WizardElevationOutcome.AlreadyElevated;
        }

        if (alreadyRelaunched)
        {
            return WizardElevationOutcome.Failed;
        }

        var start = new ProcessStartInfo(DeviceLabExecutable.CurrentPath)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(RelaunchedArgument);
        try
        {
            using var process = Process.Start(start);
            return process is null ? WizardElevationOutcome.Failed : WizardElevationOutcome.Relaunched;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return WizardElevationOutcome.Declined;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return WizardElevationOutcome.Failed;
        }
    }
}
