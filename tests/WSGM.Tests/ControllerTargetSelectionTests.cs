using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ControllerTargetSelectionTests
{
    [Fact]
    public void GlobalDefaultAppliesWhenNoApplicationIsRunning()
    {
        ResolvedControllerTarget resolved = ControllerTargetSelection.Resolve(
            ManagedControllerTarget.Xbox360,
            [],
            applicationId: null);

        Assert.Equal(ManagedControllerTarget.Xbox360, resolved.Target);
        Assert.Equal(ControllerTargetSource.GlobalDefault, resolved.Source);
        Assert.Null(resolved.ApplicationId);
    }

    [Fact]
    public void ApplicationOverrideBeatsTheGlobalDefaultForItsOwnApplication()
    {
        ResolvedControllerTarget resolved = ControllerTargetSelection.Resolve(
            ManagedControllerTarget.SteamDeckComposite,
            [Override("steam:70", ManagedControllerTarget.DualShock4)],
            "steam:70");

        Assert.Equal(ManagedControllerTarget.DualShock4, resolved.Target);
        Assert.Equal(ControllerTargetSource.ApplicationOverride, resolved.Source);
        Assert.Equal("steam:70", resolved.ApplicationId);
    }

    [Fact]
    public void AnOverrideForAnotherApplicationDoesNotLeakIntoTheRunningOne()
    {
        ResolvedControllerTarget resolved = ControllerTargetSelection.Resolve(
            ManagedControllerTarget.SteamDeckComposite,
            [Override("steam:70", ManagedControllerTarget.DualShock4)],
            "steam:220");

        Assert.Equal(ManagedControllerTarget.SteamDeckComposite, resolved.Target);
        Assert.Equal(ControllerTargetSource.GlobalDefault, resolved.Source);
    }

    [Fact]
    public void ApplicationIdentityIsMatchedExactly()
    {
        ResolvedControllerTarget resolved = ControllerTargetSelection.Resolve(
            ManagedControllerTarget.SteamDeckComposite,
            [Override("Steam:70", ManagedControllerTarget.DualShock4)],
            "steam:70");

        Assert.Equal(ControllerTargetSource.GlobalDefault, resolved.Source);
    }

    [Theory]
    [InlineData(ManagedControllerTarget.SteamDeckComposite, VirtualTargetKind.SteamDeckComposite)]
    [InlineData(ManagedControllerTarget.Xbox360, VirtualTargetKind.Xbox360)]
    [InlineData(ManagedControllerTarget.DualShock4, VirtualTargetKind.DualShock4)]
    public void EveryStoredTargetMapsOntoItsBackendKind(
        ManagedControllerTarget stored,
        VirtualTargetKind expected) =>
        Assert.Equal(expected, ControllerTargetSelection.ToVirtualTarget(stored));

    [Fact]
    public void AskingForManagementEnablesItAndCarriesNoDisabledReason()
    {
        // Deliberately gate-agnostic: the projection has to be right in a build that ships the
        // component and in one that excludes it, and the two must not be confusable.
        ControllerSelection selection = ControllerSelection.From(new DeviceIntegrationConfig
        {
            Enabled = true,
            ControllerManagementEnabled = true,
            ControllerTarget = ManagedControllerTarget.Xbox360,
        });

        Assert.Equal(DeviceFeatureAvailability.ControllerManagement, selection.Enabled);
        Assert.Equal(ManagedControllerTarget.Xbox360, selection.GlobalDefault);

        // Enabled means there is nothing to explain: leaving the build-exclusion text here would put
        // "not installed in this build" in front of a user whose controller works. Expressed as an
        // expression rather than an if/else because the gate is a compile-time constant, and the
        // branch this build does not take would be unreachable code.
        Assert.Equal(
            DeviceFeatureAvailability.ControllerManagement
                ? string.Empty
                : DeviceFeatureAvailability.ControllerManagementDetail,
            selection.DisabledDetail);
    }

    [Fact]
    public void TurningManagementOffIsNeverReportedAsAMissingComponent()
    {
        // The two reasons must stay distinguishable: a user who switched it off is told exactly
        // that, and never sent looking for a component that is present.
        ControllerSelection selection = ControllerSelection.From(new DeviceIntegrationConfig
        {
            Enabled = true,
            ControllerManagementEnabled = false,
        });

        Assert.False(selection.Enabled);
        Assert.Equal("Controller management is off.", selection.DisabledDetail);
    }

    [Fact]
    public void SelectionIsDisabledWhenDeviceIntegrationItselfIsOff()
    {
        ControllerSelection selection = ControllerSelection.From(new DeviceIntegrationConfig
        {
            Enabled = false,
            ControllerManagementEnabled = true,
        });

        Assert.False(selection.Enabled);
        Assert.Equal("Controller management is off.", selection.DisabledDetail);
    }

    [Fact]
    public void SelectionCarriesTheStoredOverridesWithoutCopyingThem()
    {
        DeviceIntegrationConfig config = new()
        {
            ControllerTargets = [Override("steam:70", ManagedControllerTarget.DualShock4)],
        };

        Assert.Same(config.ControllerTargets, ControllerSelection.From(config).Overrides);
    }

    private static DeviceApplicationTargetOverride Override(
        string applicationId,
        ManagedControllerTarget target) =>
        new() { ApplicationId = applicationId, Target = target };
}
