extern alias launch;
using WSGM.Core;

namespace WSGM.Tests.Launch;

public sealed class WrapperInputLeaseMarkerTests
{
    [Fact]
    public void ReadingAnAbsentMarkerDoesNotCreateIt()
    {
        var name = @"Local\WSGM.Tests.WrapperLease." + Guid.NewGuid().ToString("N");

        Assert.False(WrapperInputLeaseMarker.IsActive(name));
        Assert.False(launch::WSGM.Core.WrapperInputLeaseMarker.IsActive(name));
        Assert.False(Mutex.TryOpenExisting(name, out var mutex));
        mutex?.Dispose();
    }

    [Fact]
    public void WrapperAndResidentSeeTheSameLifetimeAndTheLastOwnerReleasesIt()
    {
        var name = @"Local\WSGM.Tests.WrapperLease." + Guid.NewGuid().ToString("N");
        using var first = launch::WSGM.Core.WrapperInputLeaseMarker.Hold(name);
        using var second = launch::WSGM.Core.WrapperInputLeaseMarker.Hold(name);

        Assert.True(WrapperInputLeaseMarker.IsActive(name));
        first.Dispose();
        Assert.True(WrapperInputLeaseMarker.IsActive(name));
        second.Dispose();
        Assert.False(WrapperInputLeaseMarker.IsActive(name));
    }
}
