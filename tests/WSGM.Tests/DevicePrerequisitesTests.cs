using WSGM.Core;

namespace WSGM.Tests;

public sealed class DevicePrerequisitesTests
{
    private static DevicePrerequisiteState State(
        bool package = true,
        bool integration = true,
        bool library = true,
        bool hidHide = true) => new(package, integration, library, hidHide);

    [Fact]
    public void AnInstallWithNoDevicePackageIsNotMissingAnything()
    {
        // Every desktop PC is in this state on purpose. Saying anything here would be noise.
        DevicePrerequisiteAdvice advice = DevicePrerequisites.Describe(
            State(package: false, integration: false, library: false, hidHide: false));

        Assert.False(advice.HasAdvice);
        Assert.False(advice.CanEnableIntegration);
        Assert.False(advice.NeedsSetup);
    }

    [Fact]
    public void AFullyEquippedInstallSaysNothing()
        => Assert.False(DevicePrerequisites.Describe(State()).HasAdvice);

    [Fact]
    public void APackageDroppedOntoAMinimalInstallReportsBothHalves()
    {
        // The case setup's Minimal mode creates: no controller bytes, integration seeded off, and
        // then someone copies a package into the protected slot.
        DevicePrerequisiteAdvice advice = DevicePrerequisites.Describe(
            State(integration: false, library: false, hidHide: false));

        Assert.True(advice.HasAdvice);
        Assert.Contains("Device Integration is switched off", advice.Detail, StringComparison.Ordinal);
        Assert.Contains("neither the virtual controller library nor the HidHide driver",
            advice.Detail, StringComparison.Ordinal);
        Assert.True(advice.CanEnableIntegration);
        Assert.True(advice.NeedsSetup);
    }

    [Fact]
    public void TheDriverHalfAlwaysPointsAtSetupRatherThanOfferingToInstallIt()
    {
        // INV-020: the runtime never installs a driver. The USB/IP install restarts every USB 3.0
        // hub, which under a running Game Mode would leave the user with no input.
        DevicePrerequisiteAdvice advice = DevicePrerequisites.Describe(State(library: false));

        Assert.True(advice.NeedsSetup);
        Assert.Contains("Re-run the WSGM setup", advice.Detail, StringComparison.Ordinal);
        Assert.Contains("needs a reboot", advice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationOffOnAnOtherwiseCompleteInstallOffersOnlyTheSwitch()
    {
        DevicePrerequisiteAdvice advice = DevicePrerequisites.Describe(State(integration: false));

        Assert.True(advice.CanEnableIntegration);
        Assert.False(advice.NeedsSetup);
        Assert.DoesNotContain("Re-run the WSGM setup", advice.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "neither the virtual controller library nor the HidHide driver")]
    [InlineData(true, false, "the virtual controller library but not the HidHide driver")]
    [InlineData(false, true, "does not have the virtual controller library")]
    public void EachMissingHalfIsNamedExactly(bool library, bool hidHide, string expected)
    {
        DevicePrerequisiteAdvice advice = DevicePrerequisites.Describe(
            State(library: library, hidHide: hidHide));

        Assert.Contains(expected, advice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDriverAloneStillReportsAndDoesNotClaimIntegrationIsOff()
    {
        DevicePrerequisiteAdvice advice = DevicePrerequisites.Describe(State(hidHide: false));

        Assert.True(advice.HasAdvice);
        Assert.False(advice.CanEnableIntegration);
        Assert.DoesNotContain("switched off", advice.Detail, StringComparison.Ordinal);
    }
}
