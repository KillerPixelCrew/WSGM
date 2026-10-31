using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceFanTemperatureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T20:00:00Z");

    [Fact]
    public void NativeEcTemperatureDrivesFansWhenRtssHasNoSensors()
    {
        var sampledAt = Now.AddSeconds(-1);
        var result = DeviceCoordinator.SelectFanTemperature(Native(63, sampledAt), RtssOsdMetrics.Empty, Now);
        Assert.Equal(63, result.Celsius);
        Assert.Equal(sampledAt, result.CapturedAt);
    }

    [Fact]
    public void HottestFreshSourceWinsWithoutLaunderingItsTimestamp()
    {
        var sampledAt = Now.AddSeconds(-2);
        var metrics = RtssOsdMetrics.Empty with { CpuTemperatureC = 50, GpuTemperatureC = 60, CapturedAt = Now };
        var result = DeviceCoordinator.SelectFanTemperature(Native(70, sampledAt), metrics, Now);
        Assert.Equal(70, result.Celsius);
        Assert.Equal(sampledAt, result.CapturedAt);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(1)]
    public void StaleOrFutureNativeSamplesCannotOverrideFreshRtss(int seconds)
    {
        var metrics = RtssOsdMetrics.Empty with { GpuTemperatureC = 55, CapturedAt = Now };
        var result = DeviceCoordinator.SelectFanTemperature(Native(100, Now.AddSeconds(seconds)), metrics, Now);
        Assert.Equal(55, result.Celsius);
    }

    [Fact]
    public void UnavailableOrExpiredSourcesStayUnavailable()
    {
        var native = Native(90, Now) with { Available = false };
        var metrics = RtssOsdMetrics.Empty with { CpuTemperatureC = 80, CapturedAt = Now.AddSeconds(-4) };
        Assert.Null(DeviceCoordinator.SelectFanTemperature(native, metrics, Now).Celsius);
    }

    private static CapabilityState Native(int celsius, DateTimeOffset at) => new()
    {
        CapabilityId = "telemetry.temperature", Available = true, Quality = HardwareStateQuality.Observed,
        ObservedValue = CapabilityValue.Integer(celsius), ObservedAt = at
    };
}
