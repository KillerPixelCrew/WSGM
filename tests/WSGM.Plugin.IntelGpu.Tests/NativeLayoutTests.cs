using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private static readonly Dictionary<string, int> Sizes = new()
    {
        ["ctl_init_args_t"] = Size<CtlInitArgs>(),
        ["ctl_device_adapter_properties_t"] = Size<CtlDeviceAdapterProperties>(),
        ["ctl_property_info_t"] = Size<CtlPropertyInfo>(),
        ["ctl_property_t"] = Size<CtlPropertyValue>(),
        ["ctl_3d_feature_details_t"] = Size<Ctl3dFeatureDetails>(),
        ["ctl_3d_feature_caps_t"] = Size<Ctl3dFeatureCaps>(),
        ["ctl_3d_feature_getset_t"] = Size<Ctl3dFeatureGetSet>(),
        ["ctl_endurance_gaming_t"] = Size<CtlEnduranceGaming>(),
        ["ctl_endurance_gaming_caps_t"] = Size<CtlEnduranceGamingCaps>(),
        ["ctl_adaptivesync_caps_t"] = Size<CtlAdaptiveSyncCaps>(),
        ["ctl_adaptivesync_getset_t"] = Size<CtlAdaptiveSyncGetSet>(),
        ["ctl_3d_app_profiles_caps_t"] = Size<Ctl3dAppProfilesCaps>(),
        ["ctl_3d_app_profiles_t"] = Size<Ctl3dAppProfiles>(),
        ["ctl_3d_live_state_t"] = Size<Ctl3dLiveState>(),
        ["ctl_retro_scaling_caps_t"] = Size<CtlRetroScalingCaps>(),
        ["ctl_retro_scaling_settings_t"] = Size<CtlRetroScalingSettings>(),
        ["ctl_display_timing_t"] = Size<CtlDisplayTiming>(),
        ["ctl_display_properties_t"] = Size<CtlDisplayProperties>(),
        ["ctl_adapter_display_encoder_properties_t"] = Size<CtlDisplayEncoderProperties>(),
        ["ctl_intel_arc_sync_monitor_params_t"] = Size<CtlArcSyncMonitorParams>(),
        ["ctl_intel_arc_sync_profile_params_t"] = Size<CtlArcSyncProfileParams>(),
        ["ctl_scaling_caps_t"] = Size<CtlScalingCaps>(),
        ["ctl_scaling_settings_t"] = Size<CtlScalingSettings>(),
        ["ctl_sharpness_filter_properties_t"] = Size<CtlSharpnessFilterProperties>(),
        ["ctl_sharpness_caps_t"] = Size<CtlSharpnessCaps>(),
        ["ctl_sharpness_settings_t"] = Size<CtlSharpnessSettings>(),
        ["ctl_power_optimization_caps_t"] = Size<CtlPowerOptimizationCaps>(),
        ["ctl_power_optimization_lrr_t"] = Size<CtlPowerOptimizationLrr>(),
        ["ctl_power_optimization_psr_t"] = Size<CtlPowerOptimizationPsr>(),
        ["ctl_power_optimization_dpst_t"] = Size<CtlPowerOptimizationDpst>(),
        ["ctl_power_optimization_settings_t"] = Size<CtlPowerOptimizationSettings>(),
        ["ctl_lace_config_t"] = Size<CtlLaceConfig>(),
        ["ctl_wire_format_t"] = Size<CtlWireFormat>(),
        ["ctl_get_set_wire_format_config_t"] = Size<CtlWireFormatConfig>(),
        ["ctl_display_settings_t"] = Size<CtlDisplaySettings>(),
        ["ctl_pixtx_color_primaries_t"] = Size<CtlPixTxColorPrimaries>(),
        ["ctl_pixtx_pixel_format_t"] = Size<CtlPixTxPixelFormat>(),
        ["ctl_pixtx_1dlut_config_t"] = Size<CtlPixTx1dLutConfig>(),
        ["ctl_pixtx_matrix_config_t"] = Size<CtlPixTxMatrixConfig>(),
        ["ctl_pixtx_3dlut_config_t"] = Size<CtlPixTx3dLutConfig>(),
        ["ctl_pixtx_block_config_t"] = Size<CtlPixTxBlockConfig>(),
        ["ctl_pixtx_pipe_get_config_t"] = Size<CtlPixTxPipeGetConfig>(),
        ["ctl_pixtx_pipe_set_config_t"] = Size<CtlPixTxPipeSetConfig>()
    };

    private static readonly Dictionary<string, int> Offsets = new()
    {
        ["caps.NumSupportedFeatures"] = Offset<Ctl3dFeatureCaps>(nameof(Ctl3dFeatureCaps.NumSupportedFeatures)),
        ["caps.FeatureDetails"] = Offset<Ctl3dFeatureCaps>(nameof(Ctl3dFeatureCaps.FeatureDetails)),
        ["details.Value"] = Offset<Ctl3dFeatureDetails>(nameof(Ctl3dFeatureDetails.Value)),
        ["details.CustomValueSize"] = Offset<Ctl3dFeatureDetails>(nameof(Ctl3dFeatureDetails.CustomValueSize)),
        ["details.CustomValue"] = Offset<Ctl3dFeatureDetails>(nameof(Ctl3dFeatureDetails.CustomValue)),
        ["details.PerAppSupport"] = Offset<Ctl3dFeatureDetails>(nameof(Ctl3dFeatureDetails.PerAppSupport)),
        ["details.ConflictingFeatures"] =
            Offset<Ctl3dFeatureDetails>(nameof(Ctl3dFeatureDetails.ConflictingFeatures)),
        ["details.FeatureMiscSupport"] = Offset<Ctl3dFeatureDetails>(nameof(Ctl3dFeatureDetails.FeatureMiscSupport)),
        ["getset.FeatureType"] = Offset<Ctl3dFeatureGetSet>(nameof(Ctl3dFeatureGetSet.FeatureType)),
        ["getset.ApplicationName"] = Offset<Ctl3dFeatureGetSet>(nameof(Ctl3dFeatureGetSet.ApplicationName)),
        ["getset.ApplicationNameLength"] =
            Offset<Ctl3dFeatureGetSet>(nameof(Ctl3dFeatureGetSet.ApplicationNameLength)),
        ["getset.Set"] = Offset<Ctl3dFeatureGetSet>(nameof(Ctl3dFeatureGetSet.Set)),
        ["getset.ValueType"] = Offset<Ctl3dFeatureGetSet>(nameof(Ctl3dFeatureGetSet.ValueType)),
        ["getset.Value"] = Offset<Ctl3dFeatureGetSet>(nameof(Ctl3dFeatureGetSet.Value)),
        ["getset.CustomValueSize"] = Offset<Ctl3dFeatureGetSet>(nameof(Ctl3dFeatureGetSet.CustomValueSize)),
        ["getset.CustomValue"] = Offset<Ctl3dFeatureGetSet>(nameof(Ctl3dFeatureGetSet.CustomValue)),
        ["adapter.PciDeviceId"] = Offset<CtlDeviceAdapterProperties>(nameof(CtlDeviceAdapterProperties.PciDeviceId)),
        ["adapter.Bus"] = Offset<CtlDeviceAdapterProperties>(nameof(CtlDeviceAdapterProperties.Bus)),
        ["display.Timing"] = Offset<CtlDisplayProperties>(nameof(CtlDisplayProperties.Timing)),
        ["encoder.EncoderConfigFlags"] =
            Offset<CtlDisplayEncoderProperties>(nameof(CtlDisplayEncoderProperties.EncoderConfigFlags)),
        ["power.PowerSource"] = Offset<CtlPowerOptimizationSettings>(nameof(CtlPowerOptimizationSettings.PowerSource)),
        ["appProfilesCaps.Reserved"] = Offset<Ctl3dAppProfilesCaps>(nameof(Ctl3dAppProfilesCaps.Reserved)),
        ["appProfiles.EnabledTierProfiles"] = Offset<Ctl3dAppProfiles>(nameof(Ctl3dAppProfiles.EnabledTierProfiles)),
        ["appProfiles.Reserved"] = Offset<Ctl3dAppProfiles>(nameof(Ctl3dAppProfiles.Reserved)),
        ["liveState.FramePacingStatus"] = Offset<Ctl3dLiveState>(nameof(Ctl3dLiveState.FramePacingStatus)),
        ["arcSyncProfile.MaximumHz"] = Offset<CtlArcSyncProfileParams>(nameof(CtlArcSyncProfileParams.MaximumHz)),
        ["arcSyncProfile.MaxFrameTimeDecreaseUs"] =
            Offset<CtlArcSyncProfileParams>(nameof(CtlArcSyncProfileParams.MaxFrameTimeDecreaseUs)),
        ["pixtx.BlockConfigs"] = Offset<CtlPixTxPipeGetConfig>(nameof(CtlPixTxPipeGetConfig.BlockConfigs))
    };

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
        Assert.Equal(expected, Sizes[native]);
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
        Assert.Equal(expected, Offsets[field]);
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
        Assert.Equal(420, Size<DisplayIdentityResolver.TargetDeviceName>());
    }

    private static int Size<T>()
        where T : unmanaged
    {
        return Unsafe.SizeOf<T>();
    }

    private static int Offset<T>(string field)
    {
        return (int)Marshal.OffsetOf<T>(field);
    }
}
