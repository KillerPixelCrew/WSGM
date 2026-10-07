using System;
using System.Collections.Generic;
using System.Threading;

namespace WSGM.Setup.Engine;

/// <summary>
///     The machine operations setup performs to stop the running WSGM, its sign-in service and Steam before
///     it changes anything, and to remove WSGM 1.0. <see cref="SetupEngine" /> owns their order; the tests
///     substitute this to observe it.
/// </summary>
internal interface IRuntimeShutdown
{
    /// <inheritdoc cref="WindowsSetup.InspectService" />
    ServiceState? InspectService();

    /// <inheritdoc cref="WindowsSetup.StopService" />
    bool StopService();

    /// <inheritdoc cref="WindowsSetup.ShellRunning" />
    bool ShellRunning();

    /// <inheritdoc cref="WindowsSetup.RunningWsgmPath" />
    string? RunningWsgmPath();

    /// <inheritdoc cref="WindowsSetup.RequestExit" />
    ShutdownHandoff RequestExit(string eventName, int graceIterations);

    /// <inheritdoc cref="WindowsSetup.ForceStopCurrentSession" />
    void ForceStopCurrentSession(string image);

    /// <inheritdoc cref="WindowsSetup.ShellAnchorRecoverySettled" />
    bool ShellAnchorRecoverySettled();

    /// <inheritdoc cref="WindowsSetup.CloseSteam" />
    bool CloseSteam(TimeSpan budget);

    /// <inheritdoc cref="WindowsSetup.Blockers" />
    IReadOnlyList<string> Blockers(bool includeSteam);

    /// <inheritdoc cref="WindowsSetup.Run" />
    int Run(string file, string arguments);

    /// <inheritdoc cref="WindowsSetup.Start" />
    void Start(string file, string arguments);

    /// <inheritdoc cref="WindowsSetup.ReserveDeviceOwner" />
    Mutex? ReserveDeviceOwner(TimeSpan wait);

    /// <inheritdoc cref="Registration.RunInnoUninstaller" />
    bool RunInnoUninstaller(string command, Func<bool> stillInstalled);

    /// <summary>The registration version captured by a file transaction.</summary>
    /// <returns>The registered version text, or null when absent.</returns>
    string? InstalledVersion();

    /// <summary>Restores the registration captured before file replacement.</summary>
    /// <param name="version">Previous version text, or null to remove the newly created registration.</param>
    void RestoreVersion(string? version);
}

/// <summary>The real machine, which also logs how WSGM answered the exit request.</summary>
internal sealed class WindowsRuntimeShutdown : IRuntimeShutdown
{
    /// <inheritdoc />
    public string? InstalledVersion()
    {
        return Registration.InstalledVersion()?.ToString();
    }

    /// <inheritdoc />
    public void RestoreVersion(string? version)
    {
        Registration.RestoreVersion(version);
    }

    /// <inheritdoc />
    public ServiceState? InspectService()
    {
        return WindowsSetup.InspectService();
    }

    /// <inheritdoc />
    public bool StopService()
    {
        return WindowsSetup.StopService();
    }

    /// <inheritdoc />
    public bool ShellRunning()
    {
        return WindowsSetup.ShellRunning();
    }

    /// <inheritdoc />
    public string? RunningWsgmPath()
    {
        return WindowsSetup.RunningWsgmPath();
    }

    /// <inheritdoc />
    public ShutdownHandoff RequestExit(string eventName, int graceIterations)
    {
        var handoff = WindowsSetup.RequestExit(eventName, graceIterations);
        SetupLog.Info($"Shutdown handoff on {eventName}: {handoff}");
        return handoff;
    }

    /// <inheritdoc />
    public void ForceStopCurrentSession(string image)
    {
        WindowsSetup.ForceStopCurrentSession(image);
    }

    /// <inheritdoc />
    public bool ShellAnchorRecoverySettled()
    {
        return WindowsSetup.ShellAnchorRecoverySettled();
    }

    /// <inheritdoc />
    public bool CloseSteam(TimeSpan budget)
    {
        return WindowsSetup.CloseSteam(budget);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Blockers(bool includeSteam)
    {
        return WindowsSetup.Blockers(includeSteam);
    }

    /// <inheritdoc />
    public int Run(string file, string arguments)
    {
        return WindowsSetup.Run(file, arguments);
    }

    /// <inheritdoc />
    public void Start(string file, string arguments)
    {
        WindowsSetup.Start(file, arguments);
    }

    /// <inheritdoc />
    public Mutex? ReserveDeviceOwner(TimeSpan wait)
    {
        return WindowsSetup.ReserveDeviceOwner(wait);
    }

    /// <inheritdoc />
    public bool RunInnoUninstaller(string command, Func<bool> stillInstalled)
    {
        return Registration.RunInnoUninstaller(command, stillInstalled);
    }
}
