using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.Device.Sdk.Glyphs;

/// <summary>Version and resource bounds for a plugin-supplied physical glyph profile.</summary>
public static class GlyphProfileLimits
{
    /// <summary>Schema version understood by this runtime.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Largest accepted profile document.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    /// <summary>Largest accepted source asset.</summary>
    public const int MaxAssetBytes = 512 * 1024;

    /// <summary>Largest aggregate source payload for one profile.</summary>
    public const int MaxProfileBytes = 4 * 1024 * 1024;

    /// <summary>Largest accepted license or attribution notice.</summary>
    public const int MaxNoticeBytes = 256 * 1024;

    /// <summary>Largest accepted raster dimension or SVG view-box extent.</summary>
    public const int MaxDimension = 4096;

    /// <summary>Largest accepted decoded raster area.</summary>
    public const int MaxRasterPixels = 4 * 1024 * 1024;

    /// <summary>Maximum assets in one profile.</summary>
    public const int MaxAssets = 128;

    /// <summary>Maximum control-map entries in one profile.</summary>
    public const int MaxControls = 64;

    /// <summary>Maximum aliases in one profile.</summary>
    public const int MaxAliases = 64;

    /// <summary>Maximum exact device identities assigned to one profile.</summary>
    public const int MaxExactDevices = 32;

    /// <summary>Maximum stable identifier length.</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>Maximum display-name length.</summary>
    public const int MaxDisplayNameLength = 128;

    /// <summary>Maximum physical control-label length.</summary>
    public const int MaxPhysicalLabelLength = 32;

    /// <summary>Maximum normalized paths in one SVG.</summary>
    public const int MaxSvgPaths = 256;

    /// <summary>Maximum commands in one normalized SVG.</summary>
    public const int MaxSvgCommands = 4096;

    /// <summary>Maximum characters in one SVG path-data value.</summary>
    public const int MaxPathDataLength = 64 * 1024;
}

/// <summary>A plugin-owned, schema-versioned physical-controller presentation profile.</summary>
/// <remarks>
/// Artwork is addressed only by lowercase SHA-256 content hash. The sole package-relative path is
/// the attribution notice; the loader confines and validates it before it reaches a package source.
/// </remarks>
public sealed record GlyphProfileManifest
{
    /// <summary>Version of this profile schema.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable package-scoped profile identifier.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Human-readable physical-device presentation name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Package-authored profile revision.</summary>
    public required int Revision { get; init; }

    /// <summary>Exact device-definition IDs to which this profile applies.</summary>
    public IReadOnlyList<string> ExactDeviceIds { get; init; } = [];

    /// <summary>Immutable upstream source revision retained for attribution and reproducibility.</summary>
    public required string SourceRevision { get; init; }

    /// <summary>Confined package-relative path to the required license or attribution notice.</summary>
    public required string NoticePath { get; init; }

    /// <summary>Hash-pinned inventory of every source asset used by the profile.</summary>
    public IReadOnlyList<GlyphAssetLockEntry> Assets { get; init; } = [];

    /// <summary>Optional full, left, and right physical-controller images.</summary>
    public GlyphControllerImages ControllerImages { get; init; } = new();

    /// <summary>Explicit physical presence and artwork mapping for semantic controls.</summary>
    public IReadOnlyList<GlyphControlMapping> Controls { get; init; } = [];

    /// <summary>Logical-to-physical presentation aliases.</summary>
    public IReadOnlyList<GlyphControlAlias> Aliases { get; init; } = [];
}

/// <summary>One immutable source asset and the dimensions the loader must verify.</summary>
public sealed record GlyphAssetLockEntry
{
    /// <summary>SHA-256 of the exact source bytes, and the sole runtime asset address.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Accepted source media type.</summary>
    public required GlyphAssetFormat Format { get; init; }

    /// <summary>Exact source byte count.</summary>
    public required int ByteCount { get; init; }

    /// <summary>Semantic role of the asset.</summary>
    public required GlyphAssetRole Role { get; init; }

    /// <summary>Expected raster width; required only for PNG.</summary>
    public int? PixelWidth { get; init; }

    /// <summary>Expected raster height; required only for PNG.</summary>
    public int? PixelHeight { get; init; }

    /// <summary>Expected SVG view box; required only for SVG.</summary>
    public GlyphViewBox? ViewBox { get; init; }

}

/// <summary>Supported input media type.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlyphAssetFormat>))]
public enum GlyphAssetFormat
{
    /// <summary>SVG normalized into WSGM-owned path geometry.</summary>
    Svg,

    /// <summary>Static PNG retained after bounded header and hash validation.</summary>
    Png,
}

/// <summary>Intended use of an asset.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlyphAssetRole>))]
public enum GlyphAssetRole
{
    /// <summary>Individual semantic control glyph.</summary>
    Control,

    /// <summary>Full physical-controller image.</summary>
    FullController,

    /// <summary>Left-side physical-controller image.</summary>
    LeftController,

    /// <summary>Right-side physical-controller image.</summary>
    RightController,
}

/// <summary>SVG coordinate bounds represented without culture-sensitive text.</summary>
/// <param name="X">Minimum X coordinate.</param>
/// <param name="Y">Minimum Y coordinate.</param>
/// <param name="Width">Positive coordinate width.</param>
/// <param name="Height">Positive coordinate height.</param>
public readonly record struct GlyphViewBox(decimal X, decimal Y, decimal Width, decimal Height);

/// <summary>Optional controller-level imagery addressed by source hash.</summary>
public sealed record GlyphControllerImages
{
    /// <summary>Full-controller image hash.</summary>
    public string? FullSha256 { get; init; }

    /// <summary>Left-controller image hash.</summary>
    public string? LeftSha256 { get; init; }

    /// <summary>Right-controller image hash.</summary>
    public string? RightSha256 { get; init; }
}

/// <summary>Canonical physical controls, independent of virtual targets and Steam selectors.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlyphControlId>))]
public enum GlyphControlId
{
    /// <summary>South face control.</summary>
    FaceSouth,
    /// <summary>East face control.</summary>
    FaceEast,
    /// <summary>West face control.</summary>
    FaceWest,
    /// <summary>North face control.</summary>
    FaceNorth,
    /// <summary>D-pad up.</summary>
    DpadUp,
    /// <summary>D-pad down.</summary>
    DpadDown,
    /// <summary>D-pad left.</summary>
    DpadLeft,
    /// <summary>D-pad right.</summary>
    DpadRight,
    /// <summary>Left stick press.</summary>
    LeftStick,
    /// <summary>Right stick press.</summary>
    RightStick,
    /// <summary>Left stick touch sensor.</summary>
    LeftStickTouch,
    /// <summary>Right stick touch sensor.</summary>
    RightStickTouch,
    /// <summary>Left shoulder.</summary>
    LeftShoulder,
    /// <summary>Right shoulder.</summary>
    RightShoulder,
    /// <summary>Left trigger.</summary>
    LeftTrigger,
    /// <summary>Right trigger.</summary>
    RightTrigger,
    /// <summary>Guide control.</summary>
    Guide,
    /// <summary>View control.</summary>
    View,
    /// <summary>Menu control.</summary>
    Menu,
    /// <summary>Quick-access control.</summary>
    QuickAccess,
    /// <summary>Left rear control M1.</summary>
    RearM1,
    /// <summary>Right rear control M2.</summary>
    RearM2,
    /// <summary>Additional left rear control.</summary>
    RearLeft2,
    /// <summary>Additional right rear control.</summary>
    RearRight2,
    /// <summary>First OEM control.</summary>
    Oem1,
    /// <summary>Second OEM control.</summary>
    Oem2,
    /// <summary>Touchscreen.</summary>
    Touchscreen,
    /// <summary>Left trackpad.</summary>
    LeftTrackpad,
    /// <summary>Right trackpad.</summary>
    RightTrackpad,
}

/// <summary>Explicit physical presence of a semantic control.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlyphControlPresence>))]
public enum GlyphControlPresence
{
    /// <summary>The exact device has this control.</summary>
    Present,

    /// <summary>The exact device does not have this control.</summary>
    Absent,
}

/// <summary>Physical side used for diagrams and rear-control labeling.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlyphControlSide>))]
public enum GlyphControlSide
{
    /// <summary>No physical side applies.</summary>
    None,

    /// <summary>Left side.</summary>
    Left,

    /// <summary>Right side.</summary>
    Right,
}

/// <summary>One explicit semantic control mapping.</summary>
public sealed record GlyphControlMapping
{
    /// <summary>Canonical semantic control.</summary>
    public required GlyphControlId Control { get; init; }

    /// <summary>Whether that control physically exists.</summary>
    public required GlyphControlPresence Presence { get; init; }

    /// <summary>Physical side, when meaningful.</summary>
    public GlyphControlSide Side { get; init; }

    /// <summary>Bounded plain-text label printed on the device.</summary>
    public string? PhysicalLabel { get; init; }

    /// <summary>Hash of control artwork, or null for the generic fallback.</summary>
    public string? AssetSha256 { get; init; }
}

/// <summary>One logical control presented with another physical control's artwork.</summary>
public sealed record GlyphControlAlias
{
    /// <summary>Logical control requested by a surface.</summary>
    public required GlyphControlId LogicalControl { get; init; }

    /// <summary>Physical control whose presentation is used.</summary>
    public required GlyphControlId PhysicalControl { get; init; }
}

/// <summary>One normalized path whose strings were produced by the loader's allowlisted parser.</summary>
public sealed record NormalizedGlyphPath
{
    /// <summary>Canonical SVG path data.</summary>
    public required string Data { get; init; }

    /// <summary>Canonical fill token: currentColor, none, or a hexadecimal color.</summary>
    public required string Fill { get; init; }

    /// <summary>Canonical stroke token: currentColor, none, or a hexadecimal color.</summary>
    public required string Stroke { get; init; }

    /// <summary>Stroke width in SVG coordinates.</summary>
    public decimal StrokeWidth { get; init; }

    /// <summary>Canonical fill rule.</summary>
    public required string FillRule { get; init; }

    /// <summary>Canonical stroke-line cap.</summary>
    public required string StrokeLineCap { get; init; }

    /// <summary>Canonical stroke-line join.</summary>
    public required string StrokeLineJoin { get; init; }
}

/// <summary>Safe vector output re-emitted entirely from loader-owned data.</summary>
public sealed record NormalizedGlyphSvg
{
    /// <summary>Validated coordinate bounds.</summary>
    public required GlyphViewBox ViewBox { get; init; }

    /// <summary>Allowlisted normalized paths.</summary>
    public required IReadOnlyList<NormalizedGlyphPath> Paths { get; init; }

    /// <summary>Canonical SVG bytes generated from the normalized model, never the plugin input.</summary>
    public required ReadOnlyMemory<byte> CanonicalSvgUtf8 { get; init; }
}

/// <summary>One imported, hash-linked asset safe for first-party consumers.</summary>
public sealed record ImportedGlyphAsset
{
    /// <summary>Validated source asset declaration.</summary>
    public required GlyphAssetLockEntry Lock { get; init; }

    /// <summary>Normalized vector output for SVG.</summary>
    public NormalizedGlyphSvg? Vector { get; init; }

    /// <summary>Bounded exact bytes for static PNG.</summary>
    public ReadOnlyMemory<byte> RasterPng { get; init; }

    /// <summary>Approximate retained payload size used by bounded caches.</summary>
    public int RetainedBytes => Vector?.CanonicalSvgUtf8.Length ?? RasterPng.Length;
}

/// <summary>Validated profile plus every imported hash-addressed asset.</summary>
public sealed record ImportedGlyphProfile
{
    /// <summary>Validated, deterministically ordered profile metadata.</summary>
    public required GlyphProfileManifest Manifest { get; init; }

    /// <summary>Assets keyed only by lowercase SHA-256.</summary>
    public required IReadOnlyDictionary<string, ImportedGlyphAsset> Assets { get; init; }
}
