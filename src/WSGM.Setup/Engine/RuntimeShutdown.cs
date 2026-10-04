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
    string? InstalledVersion();

    /// <summary>Restores the registration captured before file replacement.</summary>
    void RestoreVersion(string? version);
}

/// <summary>The real machine, which also logs how WSGM answered the exit request.</summary>
internal sealed class WindowsRuntimeShutdown : IRuntimeShutdown
{
    public string? InstalledVersion()
    {
        return Registration.InstalledVersion()?.ToString();
    }

    public void RestoreVersion(string? version)
    {
        Registration.RestoreVersion(version);
    }

    public ServiceState? InspectService()
    {
        return WindowsSetup.InspectService();
    }

    public bool StopService()
    {
        return WindowsSetup.StopService();
    }

    public bool ShellRunning()
    {
        return WindowsSetup.ShellRunning();
    }

    public string? RunningWsgmPath()
    {
        return WindowsSetup.RunningWsgmPath();
    }

    public ShutdownHandoff RequestExit(string eventName, int graceIterations)
    {
        var handoff = WindowsSetup.RequestExit(eventName, graceIterations);
        SetupLog.Info($"Shutdown handoff on {eventName}: {handoff}");
        return handoff;
    }

    public void ForceStopCurrentSession(string image)
    {
        WindowsSetup.ForceStopCurrentSession(image);
    }

    public bool ShellAnchorRecoverySettled()
    {
        return WindowsSetup.ShellAnchorRecoverySettled();
    }

    public bool CloseSteam(TimeSpan budget)
    {
        return WindowsSetup.CloseSteam(budget);
    }

    public IReadOnlyList<string> Blockers(bool includeSteam)
    {
        return WindowsSetup.Blockers(includeSteam);
    }

    public int Run(string file, string arguments)
    {
        return WindowsSetup.Run(file, arguments);
    }

    public void Start(string file, string arguments)
    {
        WindowsSetup.Start(file, arguments);
    }

    public Mutex? ReserveDeviceOwner(TimeSpan wait)
    {
        return WindowsSetup.ReserveDeviceOwner(wait);
    }

    public bool RunInnoUninstaller(string command, Func<bool> stillInstalled)
    {
        return Registration.RunInnoUninstaller(command, stillInstalled);
    }
}
