using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Graphics;

/// <summary>One documented member of an enum-typed 3D feature.</summary>
/// <param name="Value">The driver's value, as <c>EnumType.EnableType</c> carries it.</param>
/// <param name="Id">The stable choice value WSGM stores.</param>
/// <param name="Label">The plain label.</param>
internal readonly record struct EnumMember(uint Value, string Id, string Label);

/// <summary>How an enum feature's values relate to its <c>SupportedTypes</c> mask.</summary>
internal enum EnumMaskKind
{
    /// <summary>The mask bit for value <c>v</c> is <c>1 &lt;&lt; v</c>.</summary>
    Ordinal,

    /// <summary>The values are flags themselves, so the mask bit for a value is the value.</summary>
    Flag
}

/// <summary>What this package knows about one Intel 3D feature id.</summary>
/// <param name="Id">The <c>ctl_3d_feature_t</c> value.</param>
/// <param name="Slug">The stable capability id suffix.</param>
/// <param name="Label">The plain label.</param>
/// <param name="MaskKind">How enum members map to mask bits.</param>
/// <param name="Members">The header's documented members, for an enum feature.</param>
internal sealed record ThreeDFeatureInfo(
    int Id,
    string Slug,
    string Label,
    EnumMaskKind MaskKind,
    IReadOnlyList<EnumMember> Members);

/// <summary>
///     Intel's 3D features, with the members and labels <c>igcl_api.h</c> documents for each.
/// </summary>
/// <remarks>
///     A feature is published by the value type the driver reports for it, never by what this table
///     assumes: reading a custom-typed feature with a scalar value type crashed the driver process on
///     2026-09-29. The table only supplies names and, for enums, the members to offer when the driver's
///     supported mask is zero, which the legacy laptop driver reported for every enum.
/// </remarks>
internal static class ThreeDFeatureCatalog
{
    public const int FramePacing = 0;
    public const int EnduranceGaming = 1;
    public const int FrameLimit = 2;
    public const int Anisotropic = 3;
    public const int Cmaa = 4;
    public const int TextureFilteringQuality = 5;
    public const int AdaptiveTessellation = 6;
    public const int SharpeningFilter = 7;
    public const int Msaa = 8;
    public const int GamingFlipModes = 9;
    public const int AdaptiveSyncPlus = 10;
    public const int AppProfiles = 11;
    public const int AppProfileDetails = 12;
    public const int EmulatedTyped64BitAtomics = 13;
    public const int VrrWindowedBlt = 14;
    public const int GlobalOrPerApp = 15;
    public const int LowLatency = 16;
    public const int FrameGeneration = 17;
    public const int PrebuiltShaderDownload = 18;
    public const int LiveState = 19;
    public const int FrameGenerationControl = 20;

    /// <summary><c>CTL_3D_FEATURE_MISC_FLAG_LIVE_CHANGE</c>: the change reaches a running game.</summary>
    public const short MiscLiveChange = 1 << 4;

    private static readonly Dictionary<int, ThreeDFeatureInfo> Features = new()
    {
        [FramePacing] = new ThreeDFeatureInfo(
            FramePacing,
            "frame-pacing",
            "Frame pacing",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "off", "Off"),
                new EnumMember(1, "no-smoothing", "On, no smoothing"),
                new EnumMember(2, "max-smoothing", "On, maximum smoothing"),
                new EnumMember(3, "competitive", "Competitive")
            ]),
        [EnduranceGaming] = new ThreeDFeatureInfo(EnduranceGaming, "endurance-gaming", "Endurance Gaming",
            EnumMaskKind.Ordinal, []),
        [FrameLimit] = new ThreeDFeatureInfo(FrameLimit, "frame-limit", "Frame rate limit", EnumMaskKind.Ordinal, []),
        [Anisotropic] = new ThreeDFeatureInfo(
            Anisotropic,
            "anisotropic-filtering",
            "Anisotropic filtering",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "application", "Application choice"),
                new EnumMember(2, "2x", "2x"),
                new EnumMember(4, "4x", "4x"),
                new EnumMember(8, "8x", "8x"),
                new EnumMember(16, "16x", "16x")
            ]),
        [Cmaa] = new ThreeDFeatureInfo(
            Cmaa,
            "cmaa",
            "CMAA",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "off", "Off"),
                new EnumMember(1, "override-msaa", "Override MSAA"),
                new EnumMember(2, "enhance", "Enhance application")
            ]),
        [TextureFilteringQuality] = new ThreeDFeatureInfo(
            TextureFilteringQuality,
            "texture-filtering",
            "Texture filtering quality",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "performance", "Performance"),
                new EnumMember(1, "balanced", "Balanced"),
                new EnumMember(2, "quality", "Quality")
            ]),
        [AdaptiveTessellation] = new ThreeDFeatureInfo(
            AdaptiveTessellation,
            "adaptive-tessellation",
            "Adaptive tessellation",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "off", "Off"),
                new EnumMember(1, "on", "On")
            ]),
        [SharpeningFilter] = new ThreeDFeatureInfo(
            SharpeningFilter,
            "sharpening",
            "Sharpening",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "off", "Off"),
                new EnumMember(1, "on", "On")
            ]),
        [Msaa] = new ThreeDFeatureInfo(
            Msaa,
            "msaa",
            "Anti-aliasing (MSAA)",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "application", "Application choice"),
                new EnumMember(1, "off", "Disabled"),
                new EnumMember(2, "2x", "2x"),
                new EnumMember(4, "4x", "4x"),
                new EnumMember(8, "8x", "8x"),
                new EnumMember(16, "16x", "16x")
            ]),
        [GamingFlipModes] = new ThreeDFeatureInfo(
            GamingFlipModes,
            "frame-sync",
            "Frame synchronization",
            EnumMaskKind.Flag,
            [
                new EnumMember(1 << 0, "application", "Application choice"),
                new EnumMember(1 << 1, "vsync-off", "VSync off (verified games)"),
                new EnumMember(1 << 2, "vsync-on", "VSync on"),
                new EnumMember(1 << 3, "smooth-sync", "Smooth Sync"),
                new EnumMember(1 << 4, "speed-frame", "Speed Frame"),
                new EnumMember(1 << 5, "capped", "Capped at refresh rate"),
                new EnumMember(1 << 6, "vsync-off-all", "VSync off (all games)")
            ]),
        [AdaptiveSyncPlus] = new ThreeDFeatureInfo(AdaptiveSyncPlus, "adaptive-sync-plus", "Adaptive Sync Plus",
            EnumMaskKind.Ordinal, []),
        [EmulatedTyped64BitAtomics] = new ThreeDFeatureInfo(
            EmulatedTyped64BitAtomics,
            "emulated-64bit-atomics",
            "Emulated 64-bit atomics",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "default", "Driver default"),
                new EnumMember(1, "on", "On"),
                new EnumMember(2, "off", "Off")
            ]),
        [LowLatency] = new ThreeDFeatureInfo(
            LowLatency,
            "low-latency",
            "Low latency",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "off", "Off"),
                new EnumMember(1, "on", "On"),
                new EnumMember(2, "boost", "On + Boost")
            ]),
        [FrameGeneration] = new ThreeDFeatureInfo(
            FrameGeneration,
            "frame-generation",
            "Frame generation override",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "application", "Application choice"),
                new EnumMember(1, "2x", "2x"),
                new EnumMember(2, "3x", "3x"),
                new EnumMember(3, "4x", "4x")
            ]),
        [PrebuiltShaderDownload] = new ThreeDFeatureInfo(PrebuiltShaderDownload, "shader-download",
            "Download prebuilt shaders", EnumMaskKind.Ordinal, [])
    };

    /// <summary>
    ///     Why a feature is deliberately not published, or null when it may be.
    /// </summary>
    /// <param name="featureId">The <c>ctl_3d_feature_t</c> value.</param>
    /// <returns>A plain reason for the trace line, or null.</returns>
    public static string? SkipReason(int featureId)
    {
        return featureId switch
        {
            AppProfiles => "game compatibility tiers have no documented mapping to a single choice",
            AppProfileDetails => "tier customisation is reserved for future use in the header",
            VrrWindowedBlt => "the header marks it reserved",
            GlobalOrPerApp => "it selects between global and per-application values, which WSGM owns",
            LiveState => "it is a read-only live status, not a setting",
            FrameGenerationControl => "the header does not document its values",
            _ => null
        };
    }

    /// <summary>The known feature, or a generic description of an unknown one.</summary>
    /// <param name="featureId">The <c>ctl_3d_feature_t</c> value.</param>
    /// <returns>What to call it.</returns>
    public static ThreeDFeatureInfo Describe(int featureId)
    {
        return Features.TryGetValue(featureId, out var info)
            ? info
            : new ThreeDFeatureInfo(featureId, $"feature-{featureId}", $"Intel 3D feature {featureId}",
                EnumMaskKind.Ordinal, []);
    }

    /// <summary>The mask bit a member occupies in <c>SupportedTypes</c>.</summary>
    /// <param name="kind">How the feature maps values to bits.</param>
    /// <param name="value">The member's value.</param>
    /// <returns>The bit, or zero when it cannot be represented in 64 bits.</returns>
    public static ulong MaskBit(EnumMaskKind kind, uint value)
    {
        return kind switch
        {
            EnumMaskKind.Flag => value,
            _ => value < 64 ? 1UL << (int)value : 0
        };
    }

    /// <summary>The members to offer for an enum feature.</summary>
    /// <param name="info">The feature.</param>
    /// <param name="supportedTypes">The driver's supported mask.</param>
    /// <returns>
    ///     The documented members the mask allows. A zero mask, as legacy drivers report, offers every
    ///     documented member and lets a refused write say so. An unknown feature offers one member per
    ///     set bit.
    /// </returns>
    public static IReadOnlyList<EnumMember> SupportedMembers(ThreeDFeatureInfo info, ulong supportedTypes)
    {
        if (info.Members.Count == 0)
        {
            List<EnumMember> generic = [];
            for (var bit = 0; bit < 64 && supportedTypes != 0; bit++)
            {
                if ((supportedTypes & (1UL << bit)) == 0)
                {
                    continue;
                }

                var value = info.MaskKind == EnumMaskKind.Flag ? 1u << Math.Min(bit, 31) : (uint)bit;
                generic.Add(new EnumMember(value, $"value-{value}", $"Value {value}"));
            }

            return generic;
        }

        if (supportedTypes == 0)
        {
            return info.Members;
        }

        return
        [
            .. info.Members.Where(member =>
                MaskBit(info.MaskKind, member.Value) is var bit && bit != 0 && (supportedTypes & bit) == bit)
        ];
    }

    /// <summary>The capability id for one 3D feature.</summary>
    /// <param name="info">The feature.</param>
    /// <returns>The id.</returns>
    public static string CapabilityId(ThreeDFeatureInfo info)
    {
        return $"graphics.{info.Slug}";
    }

    /// <summary>Whether a value type is one this package can carry for a scalar feature.</summary>
    /// <param name="valueType">The reported value type.</param>
    /// <returns><see langword="true" /> for bool, enum and the three numeric types.</returns>
    public static bool IsScalar(int valueType)
    {
        return valueType is (int)IgclValueType.Bool
            or (int)IgclValueType.Enum
            or (int)IgclValueType.Int32
            or (int)IgclValueType.UInt32
            or (int)IgclValueType.Float;
    }
}
