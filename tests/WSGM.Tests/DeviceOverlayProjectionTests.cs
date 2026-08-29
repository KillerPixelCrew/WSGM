using WSGM.Shell;

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
        Assert.NotNull(snapshot.GlyphSelection);
        Assert.DoesNotContain(snapshot.Capabilities,
            capability => capability.CapabilityId == "wsgm.glyph.selection");
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

    [Fact]
    public async Task SimulatedGlyphSelectionUsesItsDedicatedCommandPath()
    {
        using SimulatedDeviceOverlaySource source = new();
        int changes = 0;
        source.Changed += () => changes++;

        DeviceOverlayGlyphSelection before = Assert.IsType<DeviceOverlayGlyphSelection>(
            source.Snapshot().GlyphSelection);
        await source.CyclePhysicalGlyphSelectionAsync();
        DeviceOverlayGlyphSelection after = Assert.IsType<DeviceOverlayGlyphSelection>(
            source.Snapshot().GlyphSelection);

        Assert.Equal("AUTO", before.TrailingText);
        Assert.Equal("STEAM", after.TrailingText);
        Assert.Equal(1, changes);
    }
}
