using System.Text.Json;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceCoordinatorDiagnosticsTests
{
    [Fact]
    public void Snapshot_RoundTripsOneOptionalInstalledPackageWithoutASecondSchema()
    {
        var original = Snapshot() with
        {
            InstalledPackage = new DeviceInstalledPackageDiagnostic(
                "wsgm.device.synthetic.dock-x1",
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
    public void Snapshot_NoInstalledPackage_RoundTripsAsNull()
    {
        var original = Snapshot();

        var json = JsonSerializer.Serialize(
            original,
            DeviceCoordinatorDiagnosticsJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);
        var restored = JsonSerializer.Deserialize(
            json,
            DeviceCoordinatorDiagnosticsJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);

        Assert.NotNull(restored);
        Assert.Null(restored.InstalledPackage);
    }

    private static DeviceCoordinatorDiagnosticsSnapshot Snapshot()
    {
        return new DeviceCoordinatorDiagnosticsSnapshot
        {
            State = DeviceCycleState.Active,
            CycleGeneration = 9,
            CapabilityCount = 3,
            HealthyCapabilityCount = 2,
            FaultedCapabilityCount = 1,
            CapturedAt = DateTimeOffset.UnixEpoch
        };
    }
}
