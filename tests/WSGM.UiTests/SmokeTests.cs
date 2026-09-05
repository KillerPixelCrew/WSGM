using Avalonia.Headless.XUnit;

namespace WSGM.UiTests;

public sealed class SmokeTests
{
    [AvaloniaFact]
    public void OverlayLoadsProductionResources()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        Assert.True(window.IsVisible);
    }
}
