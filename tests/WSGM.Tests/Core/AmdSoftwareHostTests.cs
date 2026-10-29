using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class AmdSoftwareHostTests
{
    [Theory]
    [InlineData("graphics.afmf", true)]
    [InlineData("graphics.afmf-algorithm", true)]
    [InlineData("graphics.rsr", true)]
    [InlineData("graphics.rsr-sharpness", true)]
    [InlineData("graphics.anti-lag", false)]
    [InlineData("graphics.image-sharpening", false)]
    [InlineData("display.variable-refresh-rate", false)]
    public void OnlyAfmfAndRsrAreServiceWide(string capabilityId, bool expected)
    {
        Assert.Equal(expected, AmdSoftwareHost.IsServiceWide(capabilityId));
    }

    [Fact]
    public void DescriptionNamesEveryHostProcessAndTheLauncher()
    {
        var description = AmdSoftwareHost.Describe(name => name == "AMDRSServ", false);

        Assert.Equal(
            "AMD Software host: RadeonSoftware.exe absent, AMDRSServ.exe running, cncmd.exe absent; " +
            $"launcher absent at {AmdSoftwareHost.LauncherPath}.", description);
        Assert.EndsWith(@"\AMD\CNext\CNext\cncmd.exe", AmdSoftwareHost.LauncherPath);
    }
}
