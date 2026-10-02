using System.Runtime.InteropServices;

namespace WSGM.Plugin.IntelGpu.Igcl;

/// <summary>IGCL result codes this package tells apart, from <c>ctl_result_t</c>.</summary>
internal static class IgclResult
{
    public const int Success = 0;
    public const int DeviceLost = 0x40000003;
    public const int InsufficientPermissions = 0x40000006;
    public const int Uninitialized = 0x40000008;
    public const int UnsupportedVersion = 0x40000009;
    public const int UnsupportedFeature = 0x4000000a;
    public const int InvalidArgument = 0x4000000b;
    public const int InvalidSize = 0x4000000f;
    public const int UnsupportedSize = 0x40000010;
    public const int DataNotFound = 0x40000014;
    public const int NotImplemented = 0x40000015;
    public const int InvalidOperationType = 0x4000001a;
    public const int PlatformNotSupported = 0x40000020;
    public const int InvalidEnumeration = 0x40000022;
    public const int SetFbcNotSupported = 0x48000020;

    /// <summary>Whether the driver explicitly says the feature cannot be used.</summary>
    public static bool IsUnsupportedFeature(int result)
    {
        return result is UnsupportedFeature or NotImplemented or PlatformNotSupported or SetFbcNotSupported;
    }

    /// <summary>
    ///     Whether a failed write was refused by validation, before the driver changed anything.
    /// </summary>
    /// <param name="result">The code the set call returned.</param>
    /// <returns><see langword="true" /> for a refusal; anything else is an uncertain write.</returns>
    public static bool IsRefusal(int result)
    {
        return result is UnsupportedFeature
            or InvalidArgument
            or InvalidEnumeration
            or UnsupportedVersion
            or InvalidSize
            or UnsupportedSize
            or InsufficientPermissions
            or NotImplemented
            or InvalidOperationType
            or PlatformNotSupported
            or SetFbcNotSupported;
    }

    /// <summary>Whether the session is gone and the next observation must reinitialise it.</summary>
    /// <param name="result">Any IGCL result.</param>
    /// <returns><see langword="true" /> after a driver update, reset or unload.</returns>
    /// <remarks>
    ///     Only <c>CTL_RESULT_ERROR_DEVICE_LOST</c> ("device hung, reset, was removed, or driver update
    ///     occurred") and <c>CTL_RESULT_ERROR_UNINITIALIZED</c> ("library not initialized") say the session
    ///     itself is gone. <c>CTL_RESULT_ERROR_NOT_INITIALIZED</c> ("result not initialized") and
    ///     <c>CTL_RESULT_ERROR_NOT_AVAILABLE</c> ("resource was removed") answer for one call or one
    ///     resource, so they fail that control and leave the rest of the pass alone.
    /// </remarks>
    public static bool IsSessionLost(int result)
    {
        return result is DeviceLost or Uninitialized;
    }

    /// <summary>Renders a code for a trace line.</summary>
    /// <param name="result">Any IGCL result.</param>
    /// <returns>The code in hexadecimal.</returns>
    public static string Describe(int result)
    {
        return $"0x{result:x8}";
    }
}

/// <summary><c>ctl_property_value_type_t</c>.</summary>
internal enum IgclValueType
{
    Bool = 0,
    Float = 1,
    Int32 = 2,
    UInt32 = 3,
    Enum = 4,
    Custom = 5
}

/// <summary><c>ctl_init_args_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CtlInitArgs
{
    public uint Size;
    public byte Version;
    public uint AppVersion;
    public uint Flags;
    public uint SupportedVersion;
    public fixed byte ApplicationUid[16];
}

/// <summary><c>ctl_device_adapter_properties_t</c>, version 2 for the PCI bus address.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CtlDeviceAdapterProperties
{
    public uint Size;
    public byte Version;
    public nint DeviceId;
    public uint DeviceIdSize;
    public int DeviceType;
    public uint SupportedSubfunctionFlags;
    public ulong DriverVersion;
    public ulong FirmwareMajor;
    public ulong FirmwareMinor;
    public ulong FirmwareBuild;
    public uint PciVendorId;
    public uint PciDeviceId;
    public uint RevisionId;
    public uint EusPerSubSlice;
    public uint SubSlicesPerSlice;
    public uint Slices;
    public fixed byte Name[100];
    public uint GraphicsAdapterProperties;
    public uint Frequency;
    public ushort PciSubsystemId;
    public ushort PciSubsystemVendorId;
    public byte Bus;
    public byte Device;
    public byte Function;
    public uint XeCores;
    public fixed byte Reserved[108];
}

/// <summary>
///     <c>ctl_property_info_t</c>: the union of the bool, float, int, enum and uint property details.
///     24 bytes at eight-byte alignment, because the enum member carries a 64-bit mask.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct CtlPropertyInfo
{
    /// <summary><c>BoolType.DefaultState</c> or the float/int/uint <c>DefaultEnable</c>.</summary>
    [FieldOffset(0)] public byte DefaultEnable;

    /// <summary><c>EnumType.SupportedTypes</c>.</summary>
    [FieldOffset(0)] public ulong EnumSupportedTypes;

    /// <summary><c>EnumType.DefaultType</c>.</summary>
    [FieldOffset(8)] public uint EnumDefaultType;

    [FieldOffset(4)] public float FloatMinimum;

    [FieldOffset(8)] public float FloatMaximum;

    [FieldOffset(12)] public float FloatStep;

    [FieldOffset(16)] public float FloatDefault;

    [FieldOffset(4)] public int IntMinimum;

    [FieldOffset(8)] public int IntMaximum;

    [FieldOffset(12)] public int IntStep;

    [FieldOffset(16)] public int IntDefault;

    [FieldOffset(4)] public uint UIntMinimum;

    [FieldOffset(8)] public uint UIntMaximum;

    [FieldOffset(12)] public uint UIntStep;

    [FieldOffset(16)] public uint UIntDefault;
}

/// <summary>
///     <c>ctl_property_t</c>: the union of the bool, float, int, enum and uint get/set values. Eight
///     bytes at four-byte alignment. An enum value is <c>EnumType.EnableType</c>, a uint32 at offset 0.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct CtlPropertyValue
{
    /// <summary>The <c>Enable</c> byte of the bool, float, int and uint members.</summary>
    [FieldOffset(0)] public byte Enable;

    /// <summary><c>EnumType.EnableType</c>.</summary>
    [FieldOffset(0)] public uint EnumValue;

    [FieldOffset(4)] public float FloatValue;

    [FieldOffset(4)] public int IntValue;

    [FieldOffset(4)] public uint UIntValue;
}

/// <summary><c>ctl_3d_feature_details_t</c>, 72 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Ctl3dFeatureDetails
{
    public int FeatureType;
    public int ValueType;
    public CtlPropertyInfo Value;
    public int CustomValueSize;
    public nint CustomValue;
    public byte PerAppSupport;
    public long ConflictingFeatures;
    public short FeatureMiscSupport;
    public short Reserved;
    public short Reserved1;
    public short Reserved2;
}

/// <summary><c>ctl_3d_feature_caps_t</c>, 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Ctl3dFeatureCaps
{
    public uint Size;
    public byte Version;
    public uint NumSupportedFeatures;
    public nint FeatureDetails;
}

/// <summary><c>ctl_3d_feature_getset_t</c>, 56 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Ctl3dFeatureGetSet
{
    public uint Size;
    public byte Version;
    public int FeatureType;
    public nint ApplicationName;
    public sbyte ApplicationNameLength;
    public byte Set;
    public int ValueType;
    public CtlPropertyValue Value;
    public int CustomValueSize;
    public nint CustomValue;
}

/// <summary><c>ctl_endurance_gaming_t</c>: two four-byte enums.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlEnduranceGaming
{
    public uint Control;
    public uint Mode;
}

/// <summary><c>ctl_property_info_enum_t</c>, 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPropertyInfoEnum
{
    public ulong SupportedTypes;
    public uint DefaultType;
}

/// <summary><c>ctl_endurance_gaming_caps_t</c>, 32 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlEnduranceGamingCaps
{
    public CtlPropertyInfoEnum ControlCaps;
    public CtlPropertyInfoEnum ModeCaps;
}

/// <summary>
///     <c>ctl_adaptivesync_caps_t</c>, 24 bytes: a C bool, then <c>ctl_property_info_float_t</c> at
///     four-byte alignment, whose own bool precedes the four range floats.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct CtlAdaptiveSyncCaps
{
    [FieldOffset(0)] public byte AdaptiveBalanceSupported;

    [FieldOffset(4)] public byte StrengthDefaultEnable;

    [FieldOffset(8)] public float StrengthMinimum;

    [FieldOffset(12)] public float StrengthMaximum;

    [FieldOffset(16)] public float StrengthStep;

    [FieldOffset(20)] public float StrengthDefault;
}

/// <summary><c>ctl_adaptivesync_getset_t</c>: three C bools and a float, 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlAdaptiveSyncGetSet
{
    public byte AdaptiveSync;
    public byte AdaptiveBalance;
    public byte AllowAsyncForHighFps;
    public float AdaptiveBalanceStrength;
}

/// <summary><c>ctl_3d_app_profiles_caps_t</c>, 16 bytes: the tier type mask, then a 64-bit reserved field.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Ctl3dAppProfilesCaps
{
    public uint SupportedTierTypes;
    public ulong Reserved;
}

/// <summary>
///     <c>ctl_3d_app_profiles_t</c>, 32 bytes: six four-byte tier fields, then a 64-bit reserved field.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Ctl3dAppProfiles
{
    public uint TierType;
    public uint SupportedTierProfiles;
    public uint DefaultEnabledTierProfiles;
    public uint CustomizationSupportedTierProfiles;
    public uint EnabledTierProfiles;
    public uint CustomizationEnabledTierProfiles;
    public ulong Reserved;
}

/// <summary><c>ctl_3d_live_state_t</c>, 28 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Ctl3dLiveState
{
    public uint GraphicsApi;
    public uint TargetFps;
    public uint FramePacingStatus;
    public fixed uint Reserved[4];
}

/// <summary><c>ctl_retro_scaling_caps_t</c>, 12 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlRetroScalingCaps
{
    public uint Size;
    public byte Version;
    public uint SupportedRetroScaling;
}

/// <summary><c>ctl_retro_scaling_settings_t</c>, 12 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlRetroScalingSettings
{
    public uint Size;
    public byte Version;
    public byte Get;
    public byte Enable;
    public uint RetroScalingType;
}

/// <summary><c>ctl_revision_datatype_t</c>, three bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlRevision
{
    public byte Major;
    public byte Minor;
    public byte Revision;
}

/// <summary><c>ctl_display_timing_t</c>, 64 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlDisplayTiming
{
    public uint Size;
    public byte Version;
    public ulong PixelClock;
    public uint HActive;
    public uint VActive;
    public uint HTotal;
    public uint VTotal;
    public uint HBlank;
    public uint VBlank;
    public uint HSync;
    public uint VSync;
    public float RefreshRate;
    public int SignalStandard;
    public byte VicId;
}

/// <summary>
///     <c>ctl_os_display_encoder_identifier_t</c>: the Windows target id, or a pointer and size on other
///     systems. 16 bytes at eight-byte alignment.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct CtlDisplayEncoderId
{
    /// <summary>The Windows display target id, as <c>DISPLAYCONFIG_PATH_TARGET_INFO.id</c> carries it.</summary>
    [FieldOffset(0)] public uint WindowsTargetId;

    [FieldOffset(0)] public nint Data;

    [FieldOffset(8)] public uint DataSize;
}

/// <summary><c>ctl_display_properties_t</c>, 200 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CtlDisplayProperties
{
    public uint Size;
    public byte Version;
    public CtlDisplayEncoderId EncoderId;
    public int Type;
    public int AttachedDisplayMuxType;
    public int ProtocolConverterOutput;
    public CtlRevision SupportedSpec;
    public uint SupportedOutputBpcFlags;
    public uint ProtocolConverterType;
    public uint DisplayConfigFlags;
    public uint FeatureEnabledFlags;
    public uint FeatureSupportedFlags;
    public uint AdvancedFeatureEnabledFlags;
    public uint AdvancedFeatureSupportedFlags;
    public CtlDisplayTiming Timing;
    public fixed uint ReservedFields[16];
}

/// <summary><c>ctl_intel_arc_sync_monitor_params_t</c>, 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlArcSyncMonitorParams
{
    public uint Size;
    public byte Version;
    public byte IsSupported;
    public float MinimumHz;
    public float MaximumHz;
    public uint MaxFrameTimeIncreaseUs;
    public uint MaxFrameTimeDecreaseUs;
}

/// <summary><c>ctl_intel_arc_sync_profile_params_t</c>, 28 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlArcSyncProfileParams
{
    public uint Size;
    public byte Version;
    public int Profile;
    public float MaximumHz;
    public float MinimumHz;
    public uint MaxFrameTimeIncreaseUs;
    public uint MaxFrameTimeDecreaseUs;
}

/// <summary><c>ctl_scaling_caps_t</c>, 12 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlScalingCaps
{
    public uint Size;
    public byte Version;
    public uint SupportedScaling;
}

/// <summary><c>ctl_scaling_settings_t</c>, 28 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlScalingSettings
{
    public uint Size;
    public byte Version;
    public byte Enable;
    public uint ScalingType;
    public uint CustomScalingX;
    public uint CustomScalingY;
    public byte HardwareModeSet;
    public uint PreferredScalingType;
}

/// <summary><c>ctl_sharpness_filter_properties_t</c>, 20 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlSharpnessFilterProperties
{
    public uint FilterType;
    public float Minimum;
    public float Maximum;
    public float Step;
    public float Default;
}

/// <summary><c>ctl_sharpness_caps_t</c>, 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlSharpnessCaps
{
    public uint Size;
    public byte Version;
    public uint SupportedFilterFlags;
    public byte NumFilterTypes;
    public nint FilterProperties;
}

/// <summary><c>ctl_sharpness_settings_t</c>, 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlSharpnessSettings
{
    public uint Size;
    public byte Version;
    public byte Enable;
    public uint FilterType;
    public float Intensity;
}

/// <summary><c>ctl_power_optimization_caps_t</c>, 12 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPowerOptimizationCaps
{
    public uint Size;
    public byte Version;
    public uint SupportedFeatures;
}

/// <summary><c>ctl_power_optimization_lrr_t</c>, 20 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPowerOptimizationLrr
{
    public uint Size;
    public byte Version;
    public uint SupportedTypes;
    public uint CurrentTypes;
    public byte RequirePsrDisable;
    public ushort LowRefreshRate;
}

/// <summary><c>ctl_power_optimization_psr_t</c>, 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPowerOptimizationPsr
{
    public uint Size;
    public byte Version;
    public byte PsrVersion;
    public byte FullFetchUpdate;
}

/// <summary><c>ctl_power_optimization_dpst_t</c>, 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPowerOptimizationDpst
{
    public uint Size;
    public byte Version;
    public byte MinimumLevel;
    public byte MaximumLevel;
    public byte Level;
    public uint SupportedFeatures;
    public uint EnabledFeatures;
}

/// <summary><c>ctl_power_optimization_feature_specific_info_t</c>, 20 bytes at four-byte alignment.</summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct CtlPowerOptimizationFeatureData
{
    [FieldOffset(0)] public CtlPowerOptimizationLrr Lrr;

    [FieldOffset(0)] public CtlPowerOptimizationPsr Psr;

    [FieldOffset(0)] public CtlPowerOptimizationDpst Dpst;
}

/// <summary><c>ctl_power_optimization_settings_t</c>, 44 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPowerOptimizationSettings
{
    public uint Size;
    public byte Version;
    public int Plan;
    public uint Feature;
    public byte Enable;
    public CtlPowerOptimizationFeatureData Data;
    public int PowerSource;
}

/// <summary><c>ctl_lace_aggr_config_t</c>: a fixed level byte or a lux map, 16 bytes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct CtlLaceAggressiveness
{
    [FieldOffset(0)] public byte FixedLevelPercent;

    [FieldOffset(0)] public uint MaxEntries;

    [FieldOffset(4)] public uint Entries;

    [FieldOffset(8)] public nint Table;
}

/// <summary><c>ctl_lace_config_t</c>, 40 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlLaceConfig
{
    public uint Size;
    public byte Version;
    public byte Enabled;
    public uint OperationGet;
    public int OperationSet;
    public uint Trigger;
    public CtlLaceAggressiveness Aggressiveness;
}

/// <summary><c>ctl_wire_format_t</c>, 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlWireFormat
{
    public uint Size;
    public byte Version;
    public int ColorModel;
    public uint ColorDepth;
}

/// <summary><c>ctl_get_set_wire_format_config_t</c>, 92 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlWireFormatConfig
{
    public uint Size;
    public byte Version;
    public int Operation;
    public CtlWireFormat Supported0;
    public CtlWireFormat Supported1;
    public CtlWireFormat Supported2;
    public CtlWireFormat Supported3;
    public CtlWireFormat Current;

    /// <summary>The supported entry at one index, 0 to 3.</summary>
    /// <param name="index">The entry index.</param>
    /// <returns>That entry.</returns>
    public readonly CtlWireFormat SupportedAt(int index)
    {
        return index switch
        {
            0 => Supported0,
            1 => Supported1,
            2 => Supported2,
            _ => Supported3
        };
    }
}

/// <summary><c>ctl_display_settings_t</c>, 148 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CtlDisplaySettings
{
    public uint Size;
    public byte Version;
    public byte Set;
    public uint SupportedFlags;
    public uint ControllableFlags;
    public uint ValidFlags;
    public int LowLatency;
    public int SourceToneMapping;
    public int ContentType;
    public int QuantizationRange;
    public uint SupportedPictureAspectRatio;
    public uint PictureAspectRatio;
    public int AudioSettings;
    public fixed uint Reserved[25];
}

/// <summary><c>ctl_pixtx_color_primaries_t</c>, 72 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPixTxColorPrimaries
{
    public uint Size;
    public byte Version;
    public double RedX;
    public double RedY;
    public double GreenX;
    public double GreenY;
    public double BlueX;
    public double BlueY;
    public double WhiteX;
    public double WhiteY;
}

/// <summary><c>ctl_pixtx_pixel_format_t</c>, 120 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPixTxPixelFormat
{
    public uint Size;
    public byte Version;
    public uint BitsPerColor;
    public byte IsFloat;
    public int EncodingType;
    public int ColorSpace;
    public int ColorModel;
    public CtlPixTxColorPrimaries Primaries;
    public double MaxBrightness;
    public double MinBrightness;
}

/// <summary><c>ctl_pixtx_1dlut_config_t</c>, 40 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPixTx1dLutConfig
{
    public uint Size;
    public byte Version;
    public int SamplingType;
    public uint SamplesPerChannel;
    public uint Channels;
    public nint SampleValues;
    public nint SamplePositions;
}

/// <summary><c>ctl_pixtx_matrix_config_t</c>, 128 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CtlPixTxMatrixConfig
{
    public uint Size;
    public byte Version;
    public fixed double PreOffsets[3];
    public fixed double PostOffsets[3];
    public fixed double Matrix[9];
}

/// <summary><c>ctl_pixtx_3dlut_config_t</c>, 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPixTx3dLutConfig
{
    public uint Size;
    public byte Version;
    public uint SamplesPerChannel;
    public nint SampleValues;
}

/// <summary><c>ctl_pixtx_config_t</c>, the largest member being the 128-byte matrix.</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct CtlPixTxConfig
{
    [FieldOffset(0)] public CtlPixTx1dLutConfig OneDLut;

    [FieldOffset(0)] public CtlPixTx3dLutConfig ThreeDLut;

    [FieldOffset(0)] public CtlPixTxMatrixConfig Matrix;
}

/// <summary><c>ctl_pixtx_block_config_t</c>, 144 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPixTxBlockConfig
{
    public uint Size;
    public byte Version;
    public uint BlockId;
    public int BlockType;
    public CtlPixTxConfig Config;
}

/// <summary><c>ctl_pixtx_pipe_get_config_t</c>, 272 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPixTxPipeGetConfig
{
    public uint Size;
    public byte Version;
    public int QueryType;
    public CtlPixTxPixelFormat InputFormat;
    public CtlPixTxPixelFormat OutputFormat;
    public uint NumBlocks;
    public nint BlockConfigs;
}

/// <summary><c>ctl_pixtx_pipe_set_config_t</c>, 32 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlPixTxPipeSetConfig
{
    public uint Size;
    public byte Version;
    public int OperationType;
    public uint Flags;
    public uint NumBlocks;
    public nint BlockConfigs;
}

/// <summary><c>ctl_adapter_display_encoder_properties_t</c>, 112 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CtlDisplayEncoderProperties
{
    public uint Size;
    public byte Version;
    public CtlDisplayEncoderId EncoderId;
    public int Type;
    public byte IsOnBoardProtocolConverterOutputPresent;
    public CtlRevision SupportedSpec;
    public uint SupportedOutputBpcFlags;
    public uint EncoderConfigFlags;
    public uint FeatureSupportedFlags;
    public uint AdvancedFeatureSupportedFlags;
    public fixed uint ReservedFields[16];
}
