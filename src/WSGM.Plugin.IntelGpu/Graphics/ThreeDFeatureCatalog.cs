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

    /// <summary><c>CTL_3D_TIER_TYPE_FLAG_COMPATIBILITY</c>.</summary>
    public const uint TierTypeCompatibility = 1 << 0;

    /// <summary><c>CTL_3D_TIER_TYPE_FLAG_PERFORMANCE</c>.</summary>
    public const uint TierTypePerformance = 1 << 1;

    /// <summary><c>CTL_3D_GLOBAL_OR_PER_APP_TYPES_PER_APP</c>.</summary>
    public const uint PerApplicationSettings = 1;

    /// <summary>
    ///     The members of a game profile tier choice, from <c>ctl_3d_tier_profile_flag_t</c>. Off, no tier
    ///     enabled, is a plain zero in <c>EnabledTierProfiles</c>.
    /// </summary>
    public static IReadOnlyList<EnumMember> TierProfiles { get; } =
    [
        new(0, "off", "Off"),
        new(1u << 0, "tier-1", "Tier 1"),
        new(1u << 1, "tier-2", "Tier 2"),
        new(1u << 30, "recommended", "Recommended")
    ];

    /// <summary>The graphics APIs <c>ctl_3d_live_state_t.GfxApi</c> reports, from the misc flags.</summary>
    public static IReadOnlyList<EnumMember> LiveApis { get; } =
    [
        new(0, "none", "None"),
        new(1u << 0, "dx9", "DirectX 9"),
        new(1u << 1, "dx11", "DirectX 11"),
        new(1u << 2, "dx12", "DirectX 12"),
        new(1u << 3, "vulkan", "Vulkan"),
        new(uint.MaxValue, "several", "Several")
    ];

    /// <summary><c>ctl_3d_live_state_frame_pacing_types_t</c>.</summary>
    public static IReadOnlyList<EnumMember> LiveFramePacing { get; } =
    [
        new(0, "disabled", "Off"),
        new(1, "active", "On and active"),
        new(2, "inactive", "On, not active")
    ];

    /// <summary>The tier types a <c>ctl_3d_app_profiles_caps_t</c> mask offers.</summary>
    /// <param name="supportedTierTypes">The driver's mask, or zero when it did not answer.</param>
    /// <returns>
    ///     Each documented tier type the mask sets, with its capability id and label. A zero mask offers
    ///     both documented types and lets the probe of each decide.
    /// </returns>
    public static IReadOnlyList<(uint TierType, string Id, string Label)> TierTypes(uint supportedTierTypes)
    {
        (uint TierType, string Id, string Label)[] documented =
        [
            (TierTypeCompatibility, "graphics.compatibility-profile", "Game compatibility profile"),
            (TierTypePerformance, "graphics.performance-profile", "Game performance profile")
        ];
        return supportedTierTypes == 0
            ? documented
            : [.. documented.Where(type => (supportedTierTypes & type.TierType) != 0)];
    }

    /// <summary>The tier choice's members for one tier type.</summary>
    /// <param name="supportedTierProfiles">
    ///     <c>SupportedTierProfiles</c> as the driver reported it for that type, or zero.
    /// </param>
    /// <returns>Off, and every documented tier the mask sets; every documented tier for a zero mask.</returns>
    public static IReadOnlyList<EnumMember> SupportedTiers(uint supportedTierProfiles)
    {
        return supportedTierProfiles == 0
            ? TierProfiles
            : [.. TierProfiles.Where(member => member.Value == 0 || (supportedTierProfiles & member.Value) != 0)];
    }

    /// <summary>Reduces the live state's API mask to one member value.</summary>
    /// <param name="graphicsApi">The <c>GfxApi</c> flags.</param>
    /// <returns>Zero, the one API flag set, or <see cref="uint.MaxValue" /> for several.</returns>
    public static uint LiveApi(uint graphicsApi)
    {
        // Only the four API bits name an API; the live-change bit shares the type but is not one.
        var apis = graphicsApi & 0xf;
        return apis == 0 || (apis & (apis - 1)) == 0 ? apis : uint.MaxValue;
    }

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
        [AppProfiles] = new ThreeDFeatureInfo(AppProfiles, "app-profiles", "Game profiles", EnumMaskKind.Flag, []),
        [VrrWindowedBlt] = new ThreeDFeatureInfo(
            VrrWindowedBlt,
            "vrr-windowed",
            "Variable refresh in windowed games",
            EnumMaskKind.Ordinal,
            [
                new EnumMember(0, "auto", "Auto"),
                new EnumMember(1, "on", "On"),
                new EnumMember(2, "off", "Off")
            ]),
        [GlobalOrPerApp] = new ThreeDFeatureInfo(GlobalOrPerApp, "per-application", "Per-application settings",
            EnumMaskKind.Ordinal, []),
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
            "Download prebuilt shaders", EnumMaskKind.Ordinal, []),
        [LiveState] = new ThreeDFeatureInfo(LiveState, "live", "Live state", EnumMaskKind.Ordinal, [])
    };

    /// <summary>
    ///     Why a feature is deliberately not published as a control, or null when it may be.
    /// </summary>
    /// <param name="featureId">The <c>ctl_3d_feature_t</c> value.</param>
    /// <returns>A plain reason for the trace line, or null.</returns>
    /// <remarks>
    ///     <c>igcl_api.h</c> line 1812: <c>ctl_3d_tier_details_t</c> holds only its two input fields and
    ///     reserved space, and line 1795 marks tier customisation reserved for future use, so feature 12
    ///     has nothing to read or set. Line 1536 declares feature 20 with no value type or structure.
    ///     Feature 15 is written by the per-application sync instead of being a row of its own.
    /// </remarks>
    public static string? SkipReason(int featureId)
    {
        return featureId switch
        {
            AppProfileDetails => "ctl_3d_tier_details_t carries no settable field; customisation is reserved",
            GlobalOrPerApp => "it is the per-application switch the per-application sync sets for each game",
            FrameGenerationControl => "the header declares it without a value type or structure",
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
