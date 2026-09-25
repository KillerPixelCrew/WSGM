using WSGM.DeviceLab.Transports;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabClawLightingTests
{
    [Fact]
    public void Colour_UsesAllPluginZonesAndPreservesOriginal()
    {
        var original = new byte[32];
        original[0] = 7;
        original[1] = 1;
        original[2] = 9;
        original[3] = 3;
        original[4] = 40;
        var result = LabClawLighting.Colour(original, 255, 0, 0);
        Assert.Equal(original.AsSpan(0, 4).ToArray(), result.AsSpan(0, 4).ToArray());
        Assert.Equal(40, original[4]);
        Assert.Equal(100, result[4]);
        for (var offset = 5; offset <= 29; offset += 3)
        {
            Assert.Equal(new byte[] { 255, 0, 0 }, result.AsSpan(offset, 3).ToArray());
        }
    }
}
