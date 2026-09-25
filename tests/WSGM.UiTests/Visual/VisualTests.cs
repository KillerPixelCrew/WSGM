using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Settings;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

public sealed class VisualTests
{
    [AvaloniaTheory]
    [InlineData("overlay-keyboard-720p", true, 1280, 720, 1.0)]
    [InlineData("overlay-keyboard-4k-scaled", true, 3840, 2160, 2.0)]
    [InlineData("overlay-power-menu-720p", false, 1280, 720, 1.0)]
    [InlineData("overlay-power-menu-4k-scaled", false, 3840, 2160, 2.0)]
    public void Surface(string name, bool keyboard, int width, int height, double uiScale)
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay(width, height, uiScale);
        if (keyboard)
        {
            window.ShowKeyboardSurface(new KeyboardPanel("Enter text", "Full keyboard", 256));
        }
        else
        {
            window.ShowPowerMenu();
        }

        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, name);
    }

    [AvaloniaTheory]
    [InlineData("overlay-quick-access-980", "quick-access", 980, 640)]
    [InlineData("overlay-quick-access-1280", "quick-access", 1280, 800)]
    [InlineData("overlay-quick-access-1920", "quick-access", 1920, 1080)]
    [InlineData("overlay-device-core-1280", "core", 1280, 800)]
    [InlineData("overlay-device-core-1920", "core", 1920, 1080)]
    [InlineData("overlay-device-plugin-1280", "plugin", 1280, 800)]
    [InlineData("overlay-device-plugin-1920", "plugin", 1920, 1080)]
    [InlineData("overlay-steam-1280", "steam", 1280, 800)]
    [InlineData("overlay-tools-1280", "tools", 1280, 800)]
    [InlineData("overlay-power-1280", "power", 1280, 800)]
    [InlineData("overlay-steam-980", "steam", 980, 640)]
    [InlineData("overlay-tools-980", "tools", 980, 640)]
    [InlineData("overlay-power-980", "power", 980, 640)]
    [InlineData("overlay-quick-access-720p", "quick-access", 1280, 720)]
    [InlineData("overlay-quick-access-720p-scaled", "quick-access", 1280, 720, 1.5)]
    [InlineData("overlay-quick-access-4k", "quick-access", 3840, 2160)]
    [InlineData("overlay-quick-access-4k-scaled", "quick-access", 3840, 2160, 2.0)]
    [InlineData("overlay-steam-720p", "steam", 1280, 720)]
    [InlineData("overlay-steam-4k-scaled", "steam", 3840, 2160, 2.0)]
    [InlineData("overlay-tools-720p", "tools", 1280, 720)]
    [InlineData("overlay-tools-4k-scaled", "tools", 3840, 2160, 2.0)]
    [InlineData("overlay-power-720p", "power", 1280, 720)]
    [InlineData("overlay-power-4k-scaled", "power", 3840, 2160, 2.0)]
    public async Task Overlay(string name, string page, int width, int height, double uiScale = 1.0)
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        using PowerSchemeSelection schemes = new(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Unexpected power write"));
        await schemes.RefreshAsync();
        var window = fixture.Overlay(width, height, uiScale);
        // Every destination opens its selected section beside a persistent rail. With no device
        // source attached, Device is hidden and these are the remaining destination indexes.
        if (page is "steam" or "tools" or "power")
        {
            UiFixture.Click(window, UiFixture.Tab(window, page switch
            {
                "steam" => 1,
                "tools" => 2,
                _ => 3
            }));
        }
        else if (page != "quick-access")
        {
            window.AttachPowerSchemes(schemes);
            if (page == "plugin")
            {
                window.AttachDeviceBridge(device);
            }

            UiFixture.Click(window, UiFixture.Tab(window, 2));
            if (page == "plugin")
            {
                UiFixture.Click(window, UiFixture.Rail(window, "device.section.overview"));
            }
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(height, window.ClientSize.Height);
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

    // The Plugins tab with one card for every badge tone: an installed hardware-tested device plugin, an
    // installed local build, a blind community plugin to install, another device's plugin and a community
    // plugin this release could not build.
    [AvaloniaTheory]
    [InlineData("settings-plugins-1024", 1024, 700)]
    [InlineData("settings-plugins-1280", 1280, 800)]
    public void SettingsPlugins(string name, int width, int height)
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(width, height);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        foreach (var state in PluginRows())
        {
            PluginPackageRow row = new(state, _ => Task.FromResult(""));
            (state.Section switch
            {
                PluginPackageSection.Installed => model.InstalledPackages,
                PluginPackageSection.Available => model.AvailablePackages,
                _ => model.UnavailablePackages
            }).Add(row);
        }

        UiFixture.Click(window, UiFixture.Tab(window, 8));
        VisualBaseline.Verify(window, name);
    }

    private static PluginPackageRowState[] PluginRows()
    {
        PluginBadge Badge(string text, PluginBadgeTone tone)
        {
            return new PluginBadge(text, tone);
        }

        return
        [
            new PluginPackageRowState("wsgm.device.msi.claw-8-a2vm", "MSI Claw 8 AI+ A2VM",
                PluginPackageSection.Installed, true,
                [
                    Badge("Installed", PluginBadgeTone.Good), Badge("v1.2.0", PluginBadgeTone.Neutral),
                    Badge("Device", PluginBadgeTone.Neutral), Badge("First-party", PluginBadgeTone.Accent),
                    Badge("Hardware-tested", PluginBadgeTone.Good)
                ], "", PluginPackageAction.Remove, "claw.wsgmpkg"),
            new PluginPackageRowState("wsgm.ir", "IR Blaster", PluginPackageSection.Installed, false,
            [
                Badge("Removing", PluginBadgeTone.Warn), Badge("v0.2.0", PluginBadgeTone.Neutral),
                Badge("Integration", PluginBadgeTone.Neutral), Badge("Local build", PluginBadgeTone.Neutral)
            ], "Removed at the next start.", PluginPackageAction.None, "ir.wsgmpkg"),
            new PluginPackageRowState("example.rgb", "RGB Sync", PluginPackageSection.Available, false,
            [
                Badge("Available", PluginBadgeTone.Info), Badge("v0.4.1", PluginBadgeTone.Neutral),
                Badge("Integration", PluginBadgeTone.Neutral), Badge("Community", PluginBadgeTone.Community),
                Badge("Blind", PluginBadgeTone.Warn)
            ], "Developer: rgb-dev@example.com", PluginPackageAction.Install, "rgb.wsgmpkg"),
            new PluginPackageRowState("wsgm.device.asus.rog-ally", "ASUS ROG Ally X", PluginPackageSection.Unavailable,
                true,
                [
                    Badge("Not for this device", PluginBadgeTone.Neutral), Badge("v0.1.0", PluginBadgeTone.Neutral),
                    Badge("Device", PluginBadgeTone.Neutral), Badge("First-party", PluginBadgeTone.Accent),
                    Badge("Blind", PluginBadgeTone.Warn)
                ], "", PluginPackageAction.None, ""),
            new PluginPackageRowState("example.fans", "example.fans", PluginPackageSection.Unavailable, false,
                [Badge("Outdated", PluginBadgeTone.Bad), Badge("Community", PluginBadgeTone.Community)],
                "No build for WSGM 2.0.0. Developer: fans-dev@example.com", PluginPackageAction.None, "")
        ];
    }
}
