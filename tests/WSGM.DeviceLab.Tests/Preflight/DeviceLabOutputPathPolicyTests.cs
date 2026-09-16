using WSGM.DeviceLab.Preflight;

namespace WSGM.Device.Tests;

public sealed class DeviceLabOutputPathPolicyTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingPathIsRejectedAsMalformed(string? path)
    {
        var decision = DeviceLabOutputPathPolicy.Evaluate(
            path,
            DeviceLabOutputTargetKind.Directory,
            Boundaries());

        Assert.False(decision.IsAllowed);
        Assert.Equal(DeviceLabOutputPathRisk.Malformed, decision.Risk);
    }

    [Fact]
    public void DriveRootAndBroadDirectoriesAreRejected()
    {
        var driveRoot = Path.GetPathRoot(_temporary.Root)!;
        var boundaries = Boundaries();

        Assert.Equal(
            DeviceLabOutputPathRisk.DriveRoot,
            DeviceLabOutputPathPolicy.Evaluate(
                driveRoot,
                DeviceLabOutputTargetKind.Directory,
                boundaries).Risk);
        Assert.Equal(
            DeviceLabOutputPathRisk.BroadHomeDirectory,
            DeviceLabOutputPathPolicy.Evaluate(
                boundaries.BroadHomeDirectories[0],
                DeviceLabOutputTargetKind.Directory,
                boundaries).Risk);
        Assert.Equal(
            DeviceLabOutputPathRisk.RepositoryRoot,
            DeviceLabOutputPathPolicy.Evaluate(
                boundaries.RepositoryRoot,
                DeviceLabOutputTargetKind.Directory,
                boundaries).Risk);
    }

    [Fact]
    public void LiveDataDirectoryAndEveryChildAreRejectedBeforeCreation()
    {
        var boundaries = Boundaries();

        foreach (var path in new[]
        {
            boundaries.LiveDataDirectory,
            Path.Combine(boundaries.LiveDataDirectory, "capture", "bundle.wsgmcap")
        })
        {
            var decision = DeviceLabOutputPathPolicy.Evaluate(
                path,
                DeviceLabOutputTargetKind.NewFile,
                boundaries);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeviceLabOutputPathRisk.LiveDataDirectory, decision.Risk);
        }
    }

    [Fact]
    public void ExistingTargetsAreNotOverwrittenOrTreatedAsDirectories()
    {
        var file = Path.Combine(_temporary.Root, "existing.bin");
        File.WriteAllText(file, "owned");

        Assert.Equal(
            DeviceLabOutputPathRisk.ExistingTarget,
            DeviceLabOutputPathPolicy.Evaluate(
                file,
                DeviceLabOutputTargetKind.NewFile,
                Boundaries()).Risk);
        Assert.Equal(
            DeviceLabOutputPathRisk.NotDirectory,
            DeviceLabOutputPathPolicy.Evaluate(
                file,
                DeviceLabOutputTargetKind.Directory,
                Boundaries()).Risk);
    }

    [Fact]
    public void DedicatedNewTargetsAreAllowedAndNormalized()
    {
        var requested = Path.Combine(_temporary.Root, "capture", "..", "capture", "result.wsgmcap");

        var decision = DeviceLabOutputPathPolicy.Evaluate(
            requested,
            DeviceLabOutputTargetKind.NewFile,
            Boundaries());

        Assert.True(decision.IsAllowed);
        Assert.Equal(DeviceLabOutputPathRisk.None, decision.Risk);
        Assert.Equal(Path.GetFullPath(requested), decision.FullPath);
    }

    public void Dispose() => _temporary.Dispose();

    private DeviceLabPathBoundaries Boundaries() => new()
    {
        LiveDataDirectory = Path.Combine(_temporary.Root, "live"),
        RepositoryRoot = Path.Combine(_temporary.Root, "repo"),
        BroadHomeDirectories = [Path.Combine(_temporary.Root, "home")]
    };
}
