using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>
///     With Device Integration off, WSGM must be invisible to the hardware.
/// </summary>
/// <remarks>
///     This is the promise that lets someone run another manager — MSI Center, HandheldCompanion — beside
///     WSGM. It is not enough that WSGM stops writing: nothing may be created, claimed, hidden or
///     reconfigured either, because anything left behind is something the other manager then fights.
///     <para>
///         These pin the decisions that are pure. Observing on a real Claw that nothing moves while another
///         manager drives it is the attended half and stays in its own item.
///     </para>
/// </remarks>
public sealed class DeviceIntegrationOffTests
{
    [Fact]
    public void TheMasterSwitchOffMeansNoControllerManagementWhateverElseIsStored()
    {
        // The child preference is deliberately remembered rather than erased, so it has to be the
        // master that decides — otherwise turning integration off would leave WSGM still creating a
        // virtual controller and hiding the physical one.
        var selection = ControllerSelection.From(new DeviceIntegrationConfig
        {
            Enabled = false,
            ControllerManagementEnabled = true
        }, new ProfileConfig());

        Assert.False(selection.Enabled);
    }

    [Fact]
    public void ADisabledSelectionCarriesNoTargetForAnythingToCreate()
    {
        var selection = ControllerSelection.From(new DeviceIntegrationConfig
        {
            Enabled = false,
            ControllerManagementEnabled = true
        }, new ProfileConfig());

        // Nothing downstream may read a target out of a disabled selection and act on it.
        Assert.False(selection.Enabled);
        Assert.Equal("Controller management is off.", selection.DisabledDetail);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void AutoTdpStartupAndReloadShareTheMasterSwitchPolicy(
        bool integrationEnabled,
        bool autoTdpEnabled,
        bool expected)
    {
        DeviceIntegrationConfig config = new()
        {
            Enabled = integrationEnabled,
            AutoTdpEnabled = autoTdpEnabled
        };

        Assert.Equal(expected, ShellSession.ShouldRunAutoTdp(config));
    }

    [Fact]
    public void OldConfigurationDefaultsToDeviceIntegrationDisabled()
    {
        var config = AppConfigRules.Normalize(new AppConfig { DeviceIntegration = null! }).Value;

        Assert.False(config.DeviceIntegration.Enabled);
        // Nothing sets a target, so the default applies without being written into Global.
        Assert.Null(config.Profiles.Global.ControllerTarget);
    }

    [Fact]
    public void DisablingTheMasterDoesNotEraseTheControllerPreference()
    {
        var config = AppConfigRules.Normalize(new AppConfig
        {
            DeviceIntegration = new DeviceIntegrationConfig
            {
                Enabled = false,
                ControllerManagementEnabled = true
            }
        }).Value;

        Assert.False(config.DeviceIntegration.Enabled);
        Assert.True(config.DeviceIntegration.ControllerManagementEnabled);
    }
}
