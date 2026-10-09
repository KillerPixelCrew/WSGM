using WSGM.Core;
using WSGM.Install;

namespace WSGM.Tests.Core;

public sealed class DevicePrerequisitesTests
{
    private static DevicePrerequisiteState State(
        bool package = true,
        bool integration = true,
        bool library = true,
        bool hidHide = true)
    {
        return new DevicePrerequisiteState(package, integration, library, hidHide, [SetupComponent.ControllerStack]);
    }

    [Fact]
    public void AnInstallWithNoDevicePackageIsNotMissingAnything()
    {
        // Every desktop PC is in this state on purpose. Saying anything here would be noise.
        var advice = DevicePrerequisites.Describe(
            State(false, false, false, false));

        Assert.False(advice.HasAdvice);
        Assert.False(advice.CanEnableIntegration);
        Assert.False(advice.NeedsSetup);
    }

    [Fact]
    public void AFullyEquippedInstallSaysNothing()
    {
        Assert.False(DevicePrerequisites.Describe(State()).HasAdvice);
    }

    [Fact]
    public void DecliningIntegrationDoesNotNagAboutMissingDrivers()
    {
        var advice = DevicePrerequisites.Describe(State(integration: false, library: false, hidHide: false));
        Assert.False(advice.HasAdvice);
        Assert.False(advice.CanEnableIntegration);
        Assert.False(advice.NeedsSetup);
    }

    [Fact]
    public void TheDriverHalfAlwaysPointsAtSetupRatherThanOfferingToInstallIt()
    {
        // INV-020: the runtime never installs a driver. The USB/IP install restarts every USB 3.0
        // hub, which under a running Game Mode would leave the user with no input.
        var advice = DevicePrerequisites.Describe(State(library: false));

        Assert.True(advice.NeedsSetup);
        Assert.Contains("Run Repair", advice.Detail, StringComparison.Ordinal);
        Assert.Contains("needs a reboot", advice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationOffOnAnOtherwiseCompleteInstallStaysQuiet()
    {
        var advice = DevicePrerequisites.Describe(State(integration: false));

        Assert.False(advice.CanEnableIntegration);
        Assert.False(advice.NeedsSetup);
        Assert.DoesNotContain("Run Repair", advice.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "neither the virtual controller library nor the HidHide driver")]
    [InlineData(true, false, "the virtual controller library but not the HidHide driver")]
    [InlineData(false, true, "does not have the virtual controller library")]
    public void EachMissingHalfIsNamedExactly(bool library, bool hidHide, string expected)
    {
        var advice = DevicePrerequisites.Describe(
            State(library: library, hidHide: hidHide));

        Assert.Contains(expected, advice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDriverAloneStillReportsAndDoesNotClaimIntegrationIsOff()
    {
        var advice = DevicePrerequisites.Describe(State(hidHide: false));

        Assert.True(advice.HasAdvice);
        Assert.False(advice.CanEnableIntegration);
        Assert.DoesNotContain("switched off", advice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void APowerOnlyDefinitionNeedsNoController()
    {
        var advice = DevicePrerequisites.Describe(
            new DevicePrerequisiteState(true, true, false, false, []));

        Assert.False(advice.HasAdvice);
    }

    [Theory]
    [InlineData(SetupComponent.PawnIo, false, true, "PawnIO")]
    [InlineData(SetupComponent.InpOut, true, false, "InpOut")]
    public void ExactNativeDriverRequirementsAreNamed(SetupComponent component, bool pawnIo, bool inpOut,
        string expected)
    {
        var advice = DevicePrerequisites.Describe(new DevicePrerequisiteState(true, true, true, true,
            [component], pawnIo, inpOut));
        Assert.True(advice.NeedsSetup);
        Assert.Contains(expected, advice.Detail);
    }
}
