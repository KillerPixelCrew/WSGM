using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Tests.Builders;

/// <summary>Controller targets, running applications and controller manager states for tests.</summary>
internal static class ControllerBuilders
{
    internal static DeviceApplicationTargetOverride Override(
        string applicationId,
        ManagedControllerTarget target) =>
        new() { ApplicationId = applicationId, Target = target };

    internal static RunningApplicationTargetSnapshot Running(
        string executable = @"C:\Games\game.exe",
        long generation = 1,
        string applicationId = "steam:70") => new(
        generation,
        1,
        RunningApplicationTargetState.Active,
        applicationId,
        70,
        executable,
        "game",
        null);

    internal static ControllerManagerStatus Status(
        ControllerManagementState state,
        ManagedControllerTarget? target,
        string detail = "",
        string? applicationId = null,
        UiInputSource source = UiInputSource.ManagedCanonical) =>
        new(
            state,
            target,
            ControllerTargetSource.GlobalDefault,
            applicationId,
            source,
            detail);
}
