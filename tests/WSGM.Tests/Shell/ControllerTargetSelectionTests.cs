using WSGM.Core;
using WSGM.Shell;
using static WSGM.Tests.Builders.ControllerBuilders;

namespace WSGM.Tests.Shell;

public sealed class ControllerTargetSelectionTests
{
    private static ProfileConfig Store(ManagedControllerTarget? global, params GameProfile[] games)
    {
        return new ProfileConfig { Global = new ProfileValues { ControllerTarget = global }, Games = [.. games] };
    }

    [Fact]
    public void GlobalAppliesWhenNoApplicationIsRunning()
    {
        var resolved = ControllerTargetSelection.Resolve(Store(ManagedControllerTarget.Xbox360), null, null);

        Assert.Equal(ManagedControllerTarget.Xbox360, resolved.Target);
        Assert.Equal(ProfileSource.Global, resolved.Source);
        Assert.Null(resolved.ApplicationId);
    }

    [Fact]
    public void WithNothingSetTheDefaultTargetApplies()
    {
        var resolved = ControllerTargetSelection.Resolve(new ProfileConfig(), "steam:70", null);

        Assert.Equal(ControllerTargetSelection.Default, resolved.Target);
        Assert.Equal(ProfileSource.None, resolved.Source);
    }

    [Fact]
    public void AGameOverrideBeatsGlobalForItsOwnApplication()
    {
        var resolved = ControllerTargetSelection.Resolve(
            Store(ManagedControllerTarget.SteamDeckComposite, Override("steam:70", ManagedControllerTarget.DualShock4)),
            "steam:70",
            null);

        Assert.Equal(ManagedControllerTarget.DualShock4, resolved.Target);
        Assert.Equal(ProfileSource.Game, resolved.Source);
        Assert.Equal("steam:70", resolved.ApplicationId);
    }

    [Fact]
    public void ADisabledGameProfileUsesGlobal()
    {
        var game = Override("steam:70", ManagedControllerTarget.DualShock4);
        game.Enabled = false;

        var resolved = ControllerTargetSelection.Resolve(Store(ManagedControllerTarget.Xbox360, game), "steam:70",
            null);

        Assert.Equal(ManagedControllerTarget.Xbox360, resolved.Target);
        Assert.Equal(ProfileSource.Global, resolved.Source);
    }

    [Fact]
    public void AGameProfileThatDoesNotSetTheTargetUsesGlobal()
    {
        GameProfile game = new() { Id = "steam:70", Enabled = true, Values = new ProfileValues { FrameLimit = 40 } };

        var resolved = ControllerTargetSelection.Resolve(Store(ManagedControllerTarget.Xbox360, game), "steam:70",
            null);

        Assert.Equal(ManagedControllerTarget.Xbox360, resolved.Target);
        Assert.Equal(ProfileSource.Global, resolved.Source);
    }

    [Fact]
    public void AnOverrideForAnotherApplicationDoesNotLeakIntoTheRunningOne()
    {
        var resolved = ControllerTargetSelection.Resolve(
            Store(ManagedControllerTarget.SteamDeckComposite, Override("steam:70", ManagedControllerTarget.DualShock4)),
            "steam:220",
            null);

        Assert.Equal(ManagedControllerTarget.SteamDeckComposite, resolved.Target);
        Assert.Equal(ProfileSource.Global, resolved.Source);
    }

    [Fact]
    public void AProfileBoundByExecutableMatchesThroughTheSameRuleAsEveryOtherValue()
    {
        var game = Override("profile:named", ManagedControllerTarget.DualShock4);
        game.ProcessNames = ["game.exe"];

        var resolved = ControllerTargetSelection.Resolve(Store(null, game), "process:game.exe", "GAME.exe");

        Assert.Equal(ManagedControllerTarget.DualShock4, resolved.Target);
    }

    [Fact]
    public void AskingForManagementEnablesItAndCarriesNoDisabledReason()
    {
        var selection = ControllerSelection.From(
            new DeviceIntegrationConfig { Enabled = true, ControllerManagementEnabled = true },
            new ProfileConfig());

        Assert.True(selection.Enabled);

        // Enabled means there is nothing to explain, so the detail stays empty rather than
        // offering the user a reason for a feature that is working.
        Assert.Equal(string.Empty, selection.DisabledDetail);
    }

    [Fact]
    public void TurningManagementOffIsNeverReportedAsAMissingComponent()
    {
        // The two reasons must stay distinguishable: a user who switched it off is told exactly
        // that, and never sent looking for a component that is present.
        var selection = ControllerSelection.From(
            new DeviceIntegrationConfig { Enabled = true, ControllerManagementEnabled = false },
            new ProfileConfig());

        Assert.False(selection.Enabled);
        Assert.Equal("Controller management is off.", selection.DisabledDetail);
    }
}
