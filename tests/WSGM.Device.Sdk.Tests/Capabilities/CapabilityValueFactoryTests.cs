using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Sdk.Tests.Capabilities;

public sealed class CapabilityValueFactoryTests
{
    [Fact]
    public void CurveKeepsTheCallersListInstance()
    {
        CurvePoint[] points = [new(0, 0)];

        Assert.Same(points, CapabilityValue.Curve(points).CurveValue);
    }
}
