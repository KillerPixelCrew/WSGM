using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.IntelGpu.Igcl;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

/// <summary>How reported 3D features become controls and how their values travel.</summary>
/// <remarks>Encoding and decoding never touch the session, so these run without a driver.</remarks>
public sealed class ThreeDFeatureTests
{
    private const string Adapter = "pci-8086-4688-00-02-0";

    private static readonly IgclAdapter Laptop =
        new(0, 0, 0x8086, 0x4688, 0, 2, 0, true, 0, "Intel(R) UHD Graphics", true, 0);

    private static readonly Placement Placement = new("graphics", "frames", 0);

    [Fact]
    public void AnOrdinalMaskOffersOnlyTheMembersItSets()
    {
        var info = ThreeDFeatureCatalog.Describe(ThreeDFeatureCatalog.Cmaa);

        var members = ThreeDFeatureCatalog.SupportedMembers(info, (1UL << 0) | (1UL << 2));

        Assert.Equal(["off", "enhance"], members.Select(member => member.Id));
    }

    [Fact]
    public void AFlagMaskUsesTheValuesThemselves()
    {
        // The Claw (driver 101.8992) reported feature 9 with 0x2d: application choice, VSync on, Smooth
        // Sync and capped at refresh rate.
        var info = ThreeDFeatureCatalog.Describe(ThreeDFeatureCatalog.GamingFlipModes);

        var members = ThreeDFeatureCatalog.SupportedMembers(info, 0x2d);

        Assert.Equal(["application", "vsync-on", "smooth-sync", "capped"], members.Select(member => member.Id));
        Assert.Equal([1u, 4u, 8u, 32u], members.Select(member => member.Value));
    }

    [Fact]
    public void AZeroMaskOffersEveryDocumentedMember()
    {
        // The laptop's legacy driver reported SupportedTypes 0 for every enum on 2026-09-29.
        var info = ThreeDFeatureCatalog.Describe(ThreeDFeatureCatalog.GamingFlipModes);

        Assert.Equal(7, ThreeDFeatureCatalog.SupportedMembers(info, 0).Count);
    }

    [Fact]
    public void AnUnknownEnumFeatureOffersOneMemberPerSetBit()
    {
        var info = ThreeDFeatureCatalog.Describe(42);

        var members = ThreeDFeatureCatalog.SupportedMembers(info, 0b101);

        Assert.Equal("graphics.feature-42", ThreeDFeatureCatalog.CapabilityId(info));
        Assert.Equal([0u, 2u], members.Select(member => member.Value));
    }

    [Theory]
    [InlineData(ThreeDFeatureCatalog.AppProfiles)]
    [InlineData(ThreeDFeatureCatalog.VrrWindowedBlt)]
    [InlineData(ThreeDFeatureCatalog.GlobalOrPerApp)]
    [InlineData(ThreeDFeatureCatalog.LiveState)]
    [InlineData(ThreeDFeatureCatalog.FrameGenerationControl)]
    public void TheFeaturesThatAreNotSettingsAreSkipped(int feature)
    {
        Assert.NotNull(ThreeDFeatureCatalog.SkipReason(feature));
    }

    [Fact]
    public void FrameSyncWritesTheFlagValueAtTheStartOfTheUnion()
    {
        var control = Single(Enum(ThreeDFeatureCatalog.GamingFlipModes, 0x2d, true, 0x16));

        var raw = control.Encode(default, CapabilityValue.Choice("vsync-on"));

        Assert.Equal(4u, raw.Scalar.EnumValue);
        Assert.Equal("vsync-on", control.Decode(raw)!.ChoiceValue);
    }

    [Fact]
    public void AValueNoOfferedMemberNamesDecodesAsUnknown()
    {
        var control = Single(Enum(ThreeDFeatureCatalog.GamingFlipModes, 0x2d));
        RawFeatureValue raw = default;
        raw.Scalar.EnumValue = 2;

        Assert.Null(control.Decode(raw));
    }

    [Fact]
    public void PerApplicationSupportAndLiveChangeDecideScopeAndTiming()
    {
        var live = Single(Enum(ThreeDFeatureCatalog.GamingFlipModes, 0, true, 0x16));
        var restart = Single(Enum(ThreeDFeatureCatalog.Cmaa, 0, false, 0x06));

        Assert.Equal(CapabilityProfileScope.NativePerApplication, live.Descriptor.ProfileScope);
        Assert.Equal(CapabilityApplyTiming.Immediate, live.Descriptor.ApplyTiming);
        Assert.Equal(CapabilityProfileScope.Switched, restart.Descriptor.ProfileScope);
        Assert.Equal(CapabilityApplyTiming.NextApplicationStart, restart.Descriptor.ApplyTiming);
    }

    [Fact]
    public void ABoolFeatureTravelsInTheEnableByte()
    {
        Ctl3dFeatureDetails details = default;
        details.FeatureType = ThreeDFeatureCatalog.PrebuiltShaderDownload;
        details.ValueType = (int)IgclValueType.Bool;
        var control = Single(details);

        var raw = control.Encode(default, CapabilityValue.Boolean(true));

        Assert.Equal(1, raw.Scalar.Enable);
        Assert.True(control.Decode(raw)!.BooleanValue);
        Assert.Equal("Download prebuilt shaders", control.Descriptor.Display.CustomLabel);
    }

    [Fact]
    public void AnIntegerRangeAboveZeroUsesZeroForOff()
    {
        Ctl3dFeatureDetails details = default;
        details.FeatureType = ThreeDFeatureCatalog.FrameLimit;
        details.ValueType = (int)IgclValueType.Int32;
        details.Value.IntMinimum = 30;
        details.Value.IntMaximum = 144;
        details.Value.IntStep = 1;
        var control = Single(details);

        var off = control.Encode(default, CapabilityValue.Integer(0));
        var sixty = control.Encode(default, CapabilityValue.Integer(60));

        Assert.Equal(0, control.Descriptor.Minimum);
        Assert.Equal(144, control.Descriptor.Maximum);
        Assert.Equal(0, off.Scalar.Enable);
        Assert.Equal((1, 60), (sixty.Scalar.Enable, sixty.Scalar.IntValue));
        Assert.Equal(0, control.Decode(off)!.IntegerValue);
        Assert.Equal(60, control.Decode(sixty)!.IntegerValue);
        Assert.False(control.Validate(CapabilityValue.Integer(10), out _));
    }

    [Fact]
    public void EnduranceGamingPublishesControlAndTargetAndKeepsTheOtherField()
    {
        Ctl3dFeatureDetails details = default;
        details.FeatureType = ThreeDFeatureCatalog.EnduranceGaming;
        details.ValueType = (int)IgclValueType.Custom;
        ThreeDFeature feature = new(null!, Laptop, details,
            ThreeDFeatureCatalog.Describe(ThreeDFeatureCatalog.EnduranceGaming), FeatureShape.Endurance);
        var controls = ThreeDFeatureControl.Build(feature, Adapter, Placement, IntelLog.None);
        RawFeatureValue current = default;
        current.Endurance.Mode = 2;

        var raw = controls[0].Encode(current, CapabilityValue.Choice("auto"));

        Assert.Equal(["graphics.endurance-gaming", "graphics.endurance-gaming-target"],
            controls.Select(control => control.CapabilityId));
        Assert.Equal((2u, 2u), (raw.Endurance.Control, raw.Endurance.Mode));
        Assert.Equal("battery", controls[1].Decode(raw)!.ChoiceValue);
    }

    [Fact]
    public void AnEnumWithOneOfferedValueIsNotAControl()
    {
        var controls = ThreeDFeatureControl.Build(
            Feature(Enum(ThreeDFeatureCatalog.Cmaa, 1UL << 0)), Adapter, Placement, IntelLog.None);

        Assert.Empty(controls);
    }

    [Fact]
    public void AnUnsetValueReadsAsTheDefaultTheTableReports()
    {
        var details = Enum(ThreeDFeatureCatalog.LowLatency, 0b111);
        details.Value.EnumDefaultType = 1;

        Assert.Equal(1u, Feature(details).DefaultValue().Scalar.EnumValue);
    }

    private static Ctl3dFeatureDetails Enum(int feature, ulong mask, bool perApp = false, short misc = 0)
    {
        Ctl3dFeatureDetails details = default;
        details.FeatureType = feature;
        details.ValueType = (int)IgclValueType.Enum;
        details.Value.EnumSupportedTypes = mask;
        details.PerAppSupport = perApp ? (byte)1 : (byte)0;
        details.FeatureMiscSupport = misc;
        return details;
    }

    private static ThreeDFeature Feature(Ctl3dFeatureDetails details)
    {
        return new ThreeDFeature(null!, Laptop, details, ThreeDFeatureCatalog.Describe(details.FeatureType),
            FeatureShape.Scalar);
    }

    private static ThreeDFeatureControl Single(Ctl3dFeatureDetails details)
    {
        return Assert.Single(ThreeDFeatureControl.Build(Feature(details), Adapter, Placement, IntelLog.None));
    }
}
