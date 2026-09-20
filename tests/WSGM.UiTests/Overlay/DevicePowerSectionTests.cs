using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class DevicePowerSectionTests
{
    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void HostPowerControlsSurviveMissingLeadRowsAndExistingConfigurationPins(int capabilityCount, bool pinned)
    {
        using var fixture = new UiFixture();
        using var device = new FakeDevice();
        var choices = new[]
        {
            new CapabilityChoice("off", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Off" }),
            new CapabilityChoice("on", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "On" })
        };
        var capabilities = new[]
        {
            new DeviceOverlayCapability("fan.mode", null, DeviceOverlaySection.PowerAndThermals,
                DescriptorStatus.Available, "Fan mode", "", "Off", true, CapabilityValue.Choice("off"))
            {
                PluginSectionId = "power", CategoryId = "fans", Role = CapabilityRole.FanMode,
                ValueKind = CapabilityValueKind.Choice, Writable = true, Choices = choices
            },
            new DeviceOverlayCapability("power.limit", null, DeviceOverlaySection.PowerAndThermals,
                DescriptorStatus.Available, "Power limit", "", "15 W", true, CapabilityValue.Integer(15))
            {
                PluginSectionId = "power", Role = CapabilityRole.PowerSustainedLimit,
                ValueKind = CapabilityValueKind.Integer, Writable = true, Minimum = 8, Maximum = 37
            }
        };
        var keys = new[] { "device.auto-tdp", "device.hardware-profile", "device.authored-profile" };
        device.State = device.State with
        {
            Capabilities = capabilities.Take(capabilityCount).ToArray(),
            PluginSections =
            [
                new DeviceOverlayPluginSection("power", "Power", "", SectionIcon.Power,
                    [new DeviceOverlayCategory("fans", "Fans")]) { Key = SettingSectionKey.Power }
            ],
            AutoTdp = new DescriptorRow(keys[0], "AutoTDP", "", "Off", true),
            Profile = new DescriptorRow(keys[1], "Hardware profile", "", "Off", true),
            AuthoredProfile = new DescriptorRow(keys[2], "Fan profile", "", "Off", true),
            HostSelections = keys.ToDictionary(key => key, _ => new DeviceHostSelection("off", choices))
        };
        var window = fixture.Overlay(1280, 720);
        window.AttachDeviceBridge(device);
        if (pinned)
        {
            window.SetPins(["section.device.plugin.power.configuration"]);
        }
        else
        {
            UiFixture.Click(window, UiFixture.Tab(window, 2));
            UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin.power"));
        }

        Dispatcher.UIThread.RunJobs();
        var host = UiFixture.Named<Panel>(window, pinned ? "PinnedSectionsGrid" : "DeviceCapabilityList");
        var group = Assert.Single(host.GetVisualDescendants().OfType<Border>(), border =>
            Equals(border.Tag, (pinned ? "pin:" : "") + "section.device.plugin.power.configuration"));
        var editors = keys.Select(key => Assert.Single(host.GetVisualDescendants().OfType<ComboBox>(), combo =>
            Equals(combo.Tag, (pinned ? "pin:" : "") + key))).ToArray();
        Assert.All(editors, editor => Assert.Contains(editor, group.GetVisualDescendants()));
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.All(editors, editor => Assert.Contains(editor, host.GetVisualDescendants()));
        if (pinned)
        {
            Assert.Same(group, Assert.Single(host.Children));
        }

        window.Close();
    }
}
