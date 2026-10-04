using WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

public sealed class InspectionBufferTests
{
    [Fact]
    public void ReplacementAllocationFailureDoesNotFreeTheOldAddressAgain()
    {
        List<nint> freed = [];
        var allocations = 0;
        using InspectionBuffer buffer = new(_ =>
        {
            if (++allocations == 2)
            {
                throw new OutOfMemoryException("fake allocation failure");
            }

            return 42;
        }, freed.Add);
        buffer.Resize(64);
        Assert.Throws<OutOfMemoryException>(() => buffer.Resize(8192));
        Assert.Equal(0, buffer.Pointer);
        buffer.Dispose();
        buffer.Dispose();
        Assert.Equal(new nint[] { 42 }, freed);
    }

    [Fact]
    public void ApiRequestedSizesAboveFormerCapsReachTheAllocatorAndReleaseEachBuffer()
    {
        List<int> sizes = [];
        List<nint> freed = [];
        using InspectionBuffer buffer = new(size =>
        {
            sizes.Add(size);
            return sizes.Count;
        }, freed.Add);
        buffer.Resize(4096);
        buffer.Resize(8192);
        buffer.Resize(2 * 1024 * 1024);
        buffer.Dispose();
        Assert.Equal(new[] { 4096, 8192, 2 * 1024 * 1024 }, sizes);
        Assert.Equal(new nint[] { 1, 2, 3 }, freed);
    }
}
