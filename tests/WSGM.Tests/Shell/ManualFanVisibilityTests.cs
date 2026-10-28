using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using WSGM.Tests.Builders;

namespace WSGM.Tests.Shell;

public sealed class ManualFanVisibilityTests
{
    private static readonly CapabilityDescriptor Mode = new()
    {
        CapabilityId = "fan.mode",
        InstanceId = "fan0",
        Role = CapabilityRole.FanMode,
        ValueKind = CapabilityValueKind.Choice,
        Display = new CapabilityDisplay { Key = DisplayKey.FanMode },
        SupportsRead = true,
        SupportsWrite = true,
        Persistence = CapabilityPersistence.DevicePersistent,
        Choices =
        [
            new CapabilityChoice("automatic", new CapabilityDisplay { Key = DisplayKey.FanMode }),
            new CapabilityChoice("custom", new CapabilityDisplay { Key = DisplayKey.FanMode })
        ]
    };

    private static readonly CapabilityDescriptor Duty = new()
    {
        CapabilityId = "fan.duty",
        InstanceId = "fan0",
        Role = CapabilityRole.FanDuty,
        ValueKind = CapabilityValueKind.Integer,
        Display = new CapabilityDisplay { Key = DisplayKey.FanSpeed },
        SupportsRead = true,
        SupportsWrite = true,
        Minimum = 0,
        Maximum = 100,
        Step = 1,
        Persistence = CapabilityPersistence.DevicePersistent
    };

    [Theory]
    [InlineData("automatic", false)]
    [InlineData("custom", true)]
    public void ShowsManualSpeedOnlyInManualMode(string mode, bool shown)
    {
        var views = ManualFanVisibility.Filter([ModeView(mode), CapabilityBuilders.View(Duty, null, null)]);

        Assert.Equal(shown, views.Any(view => view.Descriptor.Role == CapabilityRole.FanDuty));
        Assert.Contains(views, view => view.Descriptor.Role == CapabilityRole.FanMode);
    }

    [Fact]
    public void KeepsManualSpeedWhenModeIsUnknown()
    {
        IReadOnlyList<DeviceCapabilityView> views = [ModeView(null), CapabilityBuilders.View(Duty, null, null)];

        Assert.Same(views, ManualFanVisibility.Filter(views));
    }

    [Fact]
    public void KeepsAnotherFansSpeedInItsOwnManualMode()
    {
        var views = ManualFanVisibility.Filter([
            ModeView("automatic"),
            ModeView("custom") with { Descriptor = Mode with { InstanceId = "fan1" } },
            CapabilityBuilders.View(Duty, null, null),
            CapabilityBuilders.View(Duty with { InstanceId = "fan1" }, null, null)
        ]);

        Assert.Equal("fan1", Assert.Single(views, view => view.Descriptor.Role == CapabilityRole.FanDuty)
            .Descriptor.InstanceId);
    }

    private static DeviceCapabilityView ModeView(string? choice)
    {
        return CapabilityBuilders.View(Mode,
            choice is null ? null : new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = choice },
            null);
    }
}
