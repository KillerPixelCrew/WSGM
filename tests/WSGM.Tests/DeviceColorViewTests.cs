using WSGM.Overlay;

namespace WSGM.Tests;

public sealed class DeviceColorViewTests
{
    [Theory]
    [InlineData("#FF8000", 0xFF8000)]
    [InlineData("00ff7f", 0x00FF7F)]
    [InlineData("  000000  ", 0x000000)]
    public void ExactColorAcceptsSixRgbHexDigits(string text, int expected)
    {
        Assert.True(DeviceColorView.TryParseColor(text, out int color));
        Assert.Equal(expected, color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#123")]
    [InlineData("#GG0000")]
    [InlineData("#FFFFFFFF")]
    public void ExactColorRejectsAnythingThatIsNotRgbHex(string text) =>
        Assert.False(DeviceColorView.TryParseColor(text, out _));

    [Theory]
    [InlineData(0, 17)]
    [InlineData(238, 255)]
    [InlineData(255, 0)]
    public void ControllerChannelStepVisitsEndpointsAndWraps(int current, int expected) =>
        Assert.Equal(expected, DeviceColorView.CycleChannel(current));
}
