extern alias LogonService;
using ServiceInstaller = LogonService::WSGM.LogonService.ServiceInstaller;

namespace WSGM.Tests.LogonService;

public sealed class ServiceInstallerTests
{
    [Theory]
    [InlineData(1060, 0)]
    [InlineData(5, 1)]
    [InlineData(6, 1)]
    [InlineData(123, 1)]
    public void FailedOpenReportsSuccessOnlyForAMissingService(int error, int expected)
    {
        List<string> information = [], failures = [];
        var result = ServiceInstaller.ReportUninstallOpenFailure(error, information.Add, failures.Add);
        Assert.Equal(expected, result);
        if (expected == 0)
        {
            Assert.Single(information);
            Assert.Empty(failures);
        }
        else
        {
            Assert.Empty(information);
            Assert.Contains($"error {error}", Assert.Single(failures), StringComparison.Ordinal);
        }
    }
}
