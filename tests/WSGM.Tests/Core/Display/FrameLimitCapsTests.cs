using WSGM.Core;

namespace WSGM.Tests.Core.Display;

public sealed class FrameLimitCapsTests
{
    [Fact]
    public void TheRangeRunsFromThirtyToTheFastestListedRate()
    {
        Assert.Equal((30, 120), FrameLimitCaps.Range([48, 60, 75, 100, 120]));
    }

    [Theory]
    [InlineData]
    [InlineData(24)]
    public void NoRateFastEnoughToCarryACapHasNoRange(params int[] rates)
    {
        Assert.Null(FrameLimitCaps.Range(rates));
        Assert.Equal([0], FrameLimitCaps.Options(rates));
    }

    [Fact]
    public void TheOptionsAreOffThenEveryWholeCap()
    {
        var options = FrameLimitCaps.Options([60]);

        Assert.Equal(0, options[0]);
        Assert.Equal(Enumerable.Range(30, 31), options.Skip(1));
    }
}
