using System.Linq;
using System.Threading.Tasks;
using WSGM.Shell;
using Xunit;

namespace WSGM.Tests;

/// <summary>Pure final-overlay projection coverage; no device package or host is started.</summary>
public sealed class DeviceOverlayProjectionTests
{
    [Fact]
    public void SimulatedDeviceGroupsEveryCapabilityIntoAStableSemanticSection()
    {
        using SimulatedDeviceOverlaySource source = new();

        DeviceOverlaySnapshot snapshot = source.Snapshot();

        Assert.True(snapshot.Visible);
        Assert.Contains(snapshot.Capabilities,
            capability => capability.Section == DeviceOverlaySection.PowerAndThermals);
        Assert.Contains(snapshot.Capabilities,
            capability => capability.Section == DeviceOverlaySection.ControllerAndMotion);
        Assert.Contains(snapshot.Capabilities,
            capability => capability.Section == DeviceOverlaySection.OemAndLighting);
        Assert.All(snapshot.Capabilities,
            capability => Assert.NotEqual(DeviceOverlayStatus.None, capability.Status));
    }

    [Fact]
    public async Task SimulatedDeviceMutationRaisesOneSharedChangeAndUpdatesReadback()
    {
        using SimulatedDeviceOverlaySource source = new();
        int changes = 0;
        source.Changed += () => changes++;
        DeviceOverlayCapability tdp = source.Snapshot().Capabilities.Single(
            capability => capability.CapabilityId == "preview.power.tdp");

        await source.InvokeAsync(tdp);

        Assert.Equal(1, changes);
        Assert.Equal("16 W", source.Snapshot().Capabilities.Single(
            capability => capability.CapabilityId == "preview.power.tdp").TrailingText);
    }
}
