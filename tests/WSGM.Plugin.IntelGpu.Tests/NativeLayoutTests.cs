using WSGM.Plugin.IntelGpu.Display;
using WSGM.Plugin.IntelGpu.Igcl;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

/// <summary>
///     The managed IGCL mirrors against the sizes and offsets of <c>igcl_api.h</c> on x64.
/// </summary>
/// <remarks>
///     Every IGCL call carries the caller's own sizeof in a Size field and the driver refuses a mismatch,
///     and that refusal looks exactly like "this machine has no such feature". The 3D feature sizes and
///     offsets were measured against the driver on 2026-09-29 (driver 32.0.101.7088, Intel UHD 8086:4688):
///     caps 24 bytes, details 72 with the value union at 8, getset 56 with the value at 32. The rest are
///     computed from the header's field order and C alignment.
/// </remarks>
public sealed class NativeLayoutTests
{
    [Theory]
    [InlineData("ctl_init_args_t", 36)]
    [InlineData("ctl_device_adapter_properties_t", 320)]
    [InlineData("ctl_property_info_t", 24)]
    [InlineData("ctl_property_t", 8)]
    [InlineData("ctl_3d_feature_details_t", 72)]
    [InlineData("ctl_3d_feature_caps_t", 24)]
    [InlineData("ctl_3d_feature_getset_t", 56)]
    [InlineData("ctl_endurance_gaming_t", 8)]
    [InlineData("ctl_endurance_gaming_caps_t", 32)]
    [InlineData("ctl_adaptivesync_caps_t", 24)]
    [InlineData("ctl_adaptivesync_getset_t", 8)]
    [InlineData("ctl_3d_app_profiles_caps_t", 16)]
    [InlineData("ctl_3d_app_profiles_t", 32)]
    [InlineData("ctl_3d_live_state_t", 28)]
    [InlineData("ctl_retro_scaling_caps_t", 12)]
    [InlineData("ctl_retro_scaling_settings_t", 12)]
    [InlineData("ctl_display_timing_t", 64)]
    [InlineData("ctl_display_properties_t", 200)]
    [InlineData("ctl_adapter_display_encoder_properties_t", 112)]
    [InlineData("ctl_intel_arc_sync_monitor_params_t", 24)]
    [InlineData("ctl_intel_arc_sync_profile_params_t", 28)]
    [InlineData("ctl_scaling_caps_t", 12)]
    [InlineData("ctl_scaling_settings_t", 28)]
    [InlineData("ctl_sharpness_filter_properties_t", 20)]
    [InlineData("ctl_sharpness_caps_t", 24)]
    [InlineData("ctl_sharpness_settings_t", 16)]
    [InlineData("ctl_power_optimization_caps_t", 12)]
    [InlineData("ctl_power_optimization_lrr_t", 20)]
    [InlineData("ctl_power_optimization_psr_t", 8)]
    [InlineData("ctl_power_optimization_dpst_t", 16)]
    [InlineData("ctl_power_optimization_settings_t", 44)]
    [InlineData("ctl_lace_config_t", 40)]
    [InlineData("ctl_wire_format_t", 16)]
    [InlineData("ctl_get_set_wire_format_config_t", 92)]
    [InlineData("ctl_display_settings_t", 148)]
    [InlineData("ctl_pixtx_color_primaries_t", 72)]
    [InlineData("ctl_pixtx_pixel_format_t", 120)]
    [InlineData("ctl_pixtx_1dlut_config_t", 40)]
    [InlineData("ctl_pixtx_matrix_config_t", 128)]
    [InlineData("ctl_pixtx_3dlut_config_t", 24)]
    [InlineData("ctl_pixtx_block_config_t", 144)]
    [InlineData("ctl_pixtx_pipe_get_config_t", 272)]
    [InlineData("ctl_pixtx_pipe_set_config_t", 32)]
    public void EveryMirrorHasTheHeaderSize(string native, int expected)
    {
        Assert.Equal(expected, IgclLayout.Sizes[native]);
    }

    [Theory]
    [InlineData("caps.NumSupportedFeatures", 8)]
    [InlineData("caps.FeatureDetails", 16)]
    [InlineData("details.Value", 8)]
    [InlineData("details.CustomValueSize", 32)]
    [InlineData("details.CustomValue", 40)]
    [InlineData("details.PerAppSupport", 48)]
    [InlineData("details.ConflictingFeatures", 56)]
    [InlineData("details.FeatureMiscSupport", 64)]
    [InlineData("getset.FeatureType", 8)]
    [InlineData("getset.ApplicationName", 16)]
    [InlineData("getset.ApplicationNameLength", 24)]
    [InlineData("getset.Set", 25)]
    [InlineData("getset.ValueType", 28)]
    [InlineData("getset.Value", 32)]
    [InlineData("getset.CustomValueSize", 40)]
    [InlineData("getset.CustomValue", 48)]
    [InlineData("adapter.PciDeviceId", 68)]
    [InlineData("adapter.Bus", 200)]
    [InlineData("display.Timing", 72)]
    [InlineData("encoder.EncoderConfigFlags", 36)]
    [InlineData("power.PowerSource", 40)]
    [InlineData("appProfilesCaps.Reserved", 8)]
    [InlineData("appProfiles.EnabledTierProfiles", 16)]
    [InlineData("appProfiles.Reserved", 24)]
    [InlineData("liveState.FramePacingStatus", 8)]
    [InlineData("arcSyncProfile.MaximumHz", 12)]
    [InlineData("arcSyncProfile.MaxFrameTimeDecreaseUs", 24)]
    [InlineData("pixtx.BlockConfigs", 264)]
    public void TheMeasuredOffsetsHold(string field, int expected)
    {
        Assert.Equal(expected, IgclLayout.Offsets[field]);
    }

    [Fact]
    public void AnEnumValueIsTheFirstFourBytesOfTheUnion()
    {
        // Intel's sample reads Value.EnumType.EnableType, a uint32 at the start of the union. The Claw's
        // early reading of the enum as a bool and an int32 at +4 is what made feature 9 look unwritable.
        CtlPropertyValue value = default;
        value.EnumValue = 4;

        Assert.Equal(4, value.Enable);
        Assert.Equal(0, value.IntValue);
    }

    [Fact]
    public void TheTargetNameMatchesWindows()
    {
        Assert.Equal(420, DisplayIdentityResolver.TargetNameSize);
    }
}
