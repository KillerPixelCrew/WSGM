using LibHandheld;
using WSGM.Core;
using WSGM.Shell;
using P = LibHandheld.Contracts;

namespace WSGM.Tests.Shell;

public sealed class HandheldUiProjectionTests
{
    private const string AtlasId = "acer-predator-atlas-8-pa08-i51";

    /// A definition's measured duties become its table with their smoothing; every other definition, and
    /// none at all, keeps the default shapes.
    [Fact]
    public void AMatchedDefinitionsMeasuredDutiesBecomeItsTableAndEveryOtherKeepsTheDefault()
    {
        var atlas = HandheldDevice.Definitions.Single(definition => definition.Id == AtlasId);
        var table = HandheldUiProjection.FanPresets(atlas);
        Assert.Equal(atlas.FanCurvePresets!.Default, table.Default);
        Assert.Equal(atlas.FanCurvePresets.Quiet, table.Quiet);
        Assert.Equal(atlas.FanCurvePresets.Aggressive, table.Aggressive);
        Assert.Equal(new DeviceFanCurveSmoothing(3, 5, 2, 10), table.Smoothing);
        // Default is 45 at 70 °C and 65 at 80 °C, so the midpoint is 55.
        Assert.Equal(45, FanCurvePresets.DutyAt(table, FanCurvePreset.Default, 70));
        Assert.Equal(55, FanCurvePresets.DutyAt(table, FanCurvePreset.Default, 75));

        Assert.All(HandheldDevice.Definitions.Where(definition => definition.Id != AtlasId), definition =>
            Assert.Same(FanCurvePresetTable.HandheldCompanion, HandheldUiProjection.FanPresets(definition)));
        Assert.Same(FanCurvePresetTable.HandheldCompanion, HandheldUiProjection.FanPresets(null));
    }

    /// A native description reaches the host descriptor unchanged and passes its plain-text rule.
    [Fact]
    public void ANativeDescriptionIsCarriedIntoTheHostDisplay()
    {
        P.CapabilityDescriptor native = new()
        {
            CapabilityId = "vendor.reset", Role = P.CapabilityRole.GenericAction,
            ValueKind = P.CapabilityValueKind.None, SupportsAction = true,
            Display = new P.CapabilityDisplay
            {
                Key = P.DisplayKey.Custom, CustomText = "Reset tuning", Description = "Clears every tuning value."
            },
            Persistence = P.CapabilityPersistence.Volatile
        };
        var descriptor = Assert.Single(HandheldUiProjection.Descriptors(
            new P.CapabilityDescriptorSet { Descriptors = [native] }).Descriptors);
        Assert.Equal("Clears every tuning value.", descriptor.Display.Description);
        Assert.True(descriptor.Display.TryValidate(out _));
    }
}
