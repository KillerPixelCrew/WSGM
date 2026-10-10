using WSGM.Security;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class PawnIoInstallerTests
{
    [Theory]
    [InlineData("1.9.9")]
    [InlineData("unknown version")]
    public void IncompatibleExistingPawnIoIsPreservedAndRepairExplainsTheRequiredAction(string installedVersion)
    {
        var attempts = 0;
        for (var repair = 0; repair < 2; repair++)
        {
            var result = PawnIoInstaller.Install(installedVersion, false, () =>
            {
                attempts++;
                return new DriverInstallResult(true, true, false);
            });
            Assert.False(result.Succeeded);
            Assert.False(result.Installed);
            Assert.False(result.RestartRequired);
            Assert.Contains(installedVersion, result.Error);
            Assert.Contains("Windows Settings > Apps > Installed apps", result.Error);
            Assert.Contains("Repair", result.Error);
            Assert.Contains("left unchanged", result.Error);
        }

        Assert.Equal(0, attempts);
    }

    [Fact]
    public void SupportedExternalPawnIoIsNotReinstalledOrClaimed()
    {
        var result = PawnIoInstaller.Install("2.2.0", true,
            () => throw new InvalidOperationException("An existing driver must not run the installer."));
        Assert.True(result.Succeeded);
        Assert.False(result.Installed);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingPawnIoRunsTheInstallerOnceAndPreservesItsActualOutcome(bool succeeded)
    {
        var attempts = 0;
        var outcome = new DriverInstallResult(succeeded, succeeded, succeeded,
            succeeded ? null : "signed installer refused");
        var result = PawnIoInstaller.Install(null, false, () =>
        {
            attempts++;
            return outcome;
        });
        Assert.Same(outcome, result);
        Assert.Equal(1, attempts);
    }
}
