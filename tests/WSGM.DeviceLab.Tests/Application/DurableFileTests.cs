using WSGM.DeviceLab.Application;
using WSGM.Testing;

namespace WSGM.DeviceLab.Tests.Application;

public sealed class DurableFileTests
{
    [Fact]
    public void StagingPathIsAUniqueHiddenSiblingOfTheTarget()
    {
        using TemporaryDirectory directory = new();
        var target = directory.GetPath("capture.wsgmcap");

        var first = DurableFile.StagingPath(target);
        var second = DurableFile.StagingPath(target);

        Assert.Equal(directory.Root, Path.GetDirectoryName(first));
        Assert.Matches("^\\.capture\\.wsgmcap\\.[0-9a-f]{32}\\.tmp$", Path.GetFileName(first));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryDeleteFileReturnsCleanupFailureWithoutThrowing()
    {
        using TemporaryDirectory directory = new();
        var path = directory.GetPath("staging.tmp");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var error = DurableFile.TryDeleteFile(path);

            Assert.IsAssignableFrom<IOException>(error);
            Assert.True(File.Exists(path));
        }

        Assert.Null(DurableFile.TryDeleteFile(path));
        Assert.False(File.Exists(path));
        Assert.Null(DurableFile.TryDeleteFile(path));
    }
}
