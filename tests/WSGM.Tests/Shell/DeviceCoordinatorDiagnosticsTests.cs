using System.Text.Json;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceCoordinatorDiagnosticsTests
{
    [Fact]
    public void Snapshot_RoundTripsOneOptionalHandheldWithoutASecondSchema()
    {
        var original = Snapshot() with
        {
            Handheld = new HandheldDiagnostic(
                "fixture-family",
                "1.0.0")
        };

        var json = JsonSerializer.Serialize(
            original,
            DeviceCoordinatorDiagnosticsJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);
        var restored = JsonSerializer.Deserialize(
            json,
            DeviceCoordinatorDiagnosticsJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);

        Assert.DoesNotContain("schemaVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"packages\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void Snapshot_NoHandheld_RoundTripsAsNull()
    {
        var original = Snapshot();

        var json = JsonSerializer.Serialize(
            original,
            DeviceCoordinatorDiagnosticsJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);
        var restored = JsonSerializer.Deserialize(
            json,
            DeviceCoordinatorDiagnosticsJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);

        Assert.NotNull(restored);
        Assert.Null(restored.Handheld);
    }

    private static DeviceCoordinatorDiagnosticsSnapshot Snapshot()
    {
        return new DeviceCoordinatorDiagnosticsSnapshot
        {
            State = DeviceCycleState.Active,
            CapabilityCount = 3,
            HealthyCapabilityCount = 2,
            FaultedCapabilityCount = 1,
            CapturedAt = DateTimeOffset.UnixEpoch
        };
    }
}
