using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Tests;

public sealed class CapabilityValueFactoryTests
{
    [Fact]
    public void EachFactoryBuildsTheSameRecordAsItsObjectInitializer()
    {
        CurvePoint[] points = [new(0, 10), new(90, 100)];

        Assert.Equal(new CapabilityValue { Kind = CapabilityValueKind.None }, CapabilityValue.None());
        Assert.Equal(
            new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = true },
            CapabilityValue.Boolean(true));
        Assert.Equal(
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 15 },
            CapabilityValue.Integer(15));
        Assert.Equal(
            new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = "quiet" },
            CapabilityValue.Choice("quiet"));
        Assert.Equal(
            new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = 0x00A0FF },
            CapabilityValue.Color(0x00A0FF));
        Assert.Equal(
            new CapabilityValue { Kind = CapabilityValueKind.Curve, CurveValue = points },
            CapabilityValue.Curve(points));
        Assert.Equal(
            new CapabilityValue { Kind = CapabilityValueKind.Text, TextValue = "Dock" },
            CapabilityValue.Text("Dock"));
    }

    [Fact]
    public void CurveKeepsTheCallersListInstance()
    {
        CurvePoint[] points = [new(0, 0)];

        Assert.Same(points, CapabilityValue.Curve(points).CurveValue);
    }
}
