using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Tests.Builders;

/// <summary>Controller targets, running applications and controller manager states for tests.</summary>
internal static class ControllerBuilders
{
    /// <summary>An enabled game profile that overrides only the controller target.</summary>
    internal static GameProfile Override(
        string applicationId,
        ManagedControllerTarget target)
    {
        return new GameProfile
        {
            Id = applicationId,
            Enabled = true,
            Values = new ProfileValues { ControllerTarget = target }
        };
    }

    internal static RunningApplicationTargetSnapshot Running(
        string executable = @"C:\Games\game.exe",
        long generation = 1,
        string applicationId = "steam:70")
    {
        return new RunningApplicationTargetSnapshot(
            generation,
            1,
            RunningApplicationTargetState.Active,
            applicationId,
            70,
            executable,
            "game",
            null);
    }

    internal static ControllerManagerStatus Status(
        ControllerManagementState state,
        ManagedControllerTarget? target,
        string detail = "",
        string? applicationId = null,
        UiInputSource source = UiInputSource.ManagedCanonical)
    {
        return new ControllerManagerStatus(
            state,
            target,
            ProfileSource.Global,
            applicationId,
            source,
            detail);
    }
}
