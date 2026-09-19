using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Tests.Application;

public sealed class DurableFileTests
{
    [Fact]
    public void StagingPathIsHiddenUniqueAndBesideTarget()
    {
        var target = Path.Combine("output", "capture.wsgmcap");

        var first = DurableFile.StagingPath(target);
        var second = DurableFile.StagingPath(target);

        Assert.Equal("output", Path.GetDirectoryName(first));
        Assert.Matches("^\\.capture\\.wsgmcap\\.[0-9a-f]{32}\\.tmp$", Path.GetFileName(first));
        Assert.NotEqual(first, second);
    }
}
