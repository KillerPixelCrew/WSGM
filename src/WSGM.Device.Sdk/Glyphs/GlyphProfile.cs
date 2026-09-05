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

    /// <summary>Maximum glyph profiles in one package.</summary>
    /// <remarks>
    /// A package source enumerates at most one identifier beyond this, so the importer can tell a
    /// conforming package from one that exceeds the limit. A source that truncated at exactly this
    /// number made the importer's over-limit check unreachable, and an oversized package validated
    /// as conforming after silently dropping the extra profiles.
    /// </remarks>
    public const int MaxProfiles = 32;

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

/// <summary>One path WSGM's renderer can draw, with the presentation that applies to it.</summary>
public sealed record NormalizedGlyphPath
{
    /// <summary>SVG path data, as authored.</summary>
    public required string Data { get; init; }

    /// <summary>Fill, resolved through any enclosing groups.</summary>
    public required string Fill { get; init; }

    /// <summary>Stroke, resolved through any enclosing groups.</summary>
    public required string Stroke { get; init; }

    /// <summary>Stroke width in SVG coordinates.</summary>
    public decimal StrokeWidth { get; init; }

    /// <summary>Fill rule.</summary>
    public required string FillRule { get; init; }

    /// <summary>Stroke-line cap.</summary>
    public required string StrokeLineCap { get; init; }

    /// <summary>Stroke-line join.</summary>
    public required string StrokeLineJoin { get; init; }
}

/// <summary>An imported SVG asset: the author's bytes, plus the bounds read from them.</summary>
public sealed record NormalizedGlyphSvg
{
    /// <summary>The author's own SVG bytes, unmodified.</summary>
    public required ReadOnlyMemory<byte> SvgUtf8 { get; init; }

    /// <summary>Coordinate bounds, from the view box or the intrinsic size.</summary>
    public required GlyphViewBox ViewBox { get; init; }

    /// <summary>The paths WSGM's own renderer can draw, which may be empty.</summary>
    /// <remarks>
    /// Extracted for Avalonia, which draws geometry rather than documents, and for nothing else —
    /// Steam is handed <see cref="SvgUtf8"/>. Artwork whose paths cannot all be understood still
    /// imports and still reaches Steam intact; only WSGM's own glyph rendering falls back.
    /// </remarks>
    public IReadOnlyList<NormalizedGlyphPath> Paths { get; init; } = [];
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
    public int RetainedBytes => Vector?.SvgUtf8.Length ?? RasterPng.Length;
}

/// <summary>Validated profile plus every imported hash-addressed asset.</summary>
public sealed record ImportedGlyphProfile
{
    /// <summary>Validated, deterministically ordered profile metadata.</summary>
    public required GlyphProfileManifest Manifest { get; init; }

    /// <summary>Assets keyed only by lowercase SHA-256.</summary>
    public required IReadOnlyDictionary<string, ImportedGlyphAsset> Assets { get; init; }
}
