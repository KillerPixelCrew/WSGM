using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class GestureDefaultTests
{
    [Fact]
    public void MissingBottomBindingIsDisabledWithoutChangingTopBinding()
    {
        var gestures = JsonSerializer.Deserialize<GestureConfig>("{}")!;
        Assert.False(gestures.BottomEdge);
        Assert.True(gestures.TopEdge);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitBottomChoiceSurvivesConfigurationRoundTrip(bool enabled)
    {
        var json = JsonSerializer.Serialize(new GestureConfig { BottomEdge = enabled });
        Assert.Equal(enabled, JsonSerializer.Deserialize<GestureConfig>(json)!.BottomEdge);
    }
}
