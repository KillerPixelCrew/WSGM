using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Overlay;

namespace WSGM.UiTests;

public sealed class VisualTests
{
    [AvaloniaTheory]
    [InlineData("overlay-quick-access-1280", "quick-access", 1280, 800)]
    [InlineData("overlay-quick-access-1920", "quick-access", 1920, 1080)]
    [InlineData("overlay-device-core-1280", "core", 1280, 800)]
    [InlineData("overlay-device-core-1920", "core", 1920, 1080)]
    [InlineData("overlay-device-plugin-1280", "plugin", 1280, 800)]
    [InlineData("overlay-device-plugin-1920", "plugin", 1920, 1080)]
    [InlineData("overlay-steam-1280", "steam", 1280, 800)]
    [InlineData("overlay-tools-1280", "tools", 1280, 800)]
    [InlineData("overlay-power-1280", "power", 1280, 800)]
    public async Task Overlay(string name, string page, int width, int height)
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        using PowerSchemeSelection schemes = new(new PowerSchemes(new OverlayInteractionTests.FakePower()), _ => throw new InvalidOperationException("Unexpected power write"));
        await schemes.RefreshAsync();
        OverlayWindow window = fixture.Overlay(width, height);
        // The category menus each destination root now shows. No power schemes and no device
        // bridge, so the Device tab stays hidden and these are the tab indexes without it.
        if (page is "steam" or "tools" or "power")
        {
            UiFixture.Click(window, UiFixture.Tab(window, page switch
            {
                "steam" => 2,
                "tools" => 3,
                _ => 4,
            }));
        }
        else if (page != "quick-access")
        {
            window.AttachPowerSchemes(schemes);
            if (page == "plugin") { window.AttachDeviceBridge(device); }
            UiFixture.Click(window, UiFixture.Tab(window, 3));
            if (page == "core")
            {
                UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
                    .Single(card => card.IsEffectivelyVisible && card.Title == "Power"));
            }
            if (page == "plugin")
            {
                UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
                    .Single(card => card.IsEffectivelyVisible && card.Title == "Overview"));
            }
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(Math.Round(height * OverlayWindow.SheetHeightFraction), window.ClientSize.Height);
        VisualBaseline.Verify(window, name);
    }

    [AvaloniaTheory]
    [InlineData("settings-system-1024", 0, 1024, 700)]
    [InlineData("settings-system-1280", 0, 1280, 800)]
    [InlineData("settings-quick-access-1024", 5, 1024, 700)]
    [InlineData("settings-quick-access-1280", 5, 1280, 800)]
    [InlineData("settings-appearance-1024", 7, 1024, 700)]
    [InlineData("settings-appearance-1280", 7, 1280, 800)]
    public void Settings(string name, int page, int width, int height)
    {
        using UiFixture fixture = new();
        Window window = fixture.Settings(width, height);
        UiFixture.Click(window, UiFixture.Tab(window, page));
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(height, window.ClientSize.Height);
        VisualBaseline.Verify(window, name);
    }
}
