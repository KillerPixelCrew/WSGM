using System;
using System.Collections.Generic;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Controls;

/// <summary>One validated vector path rendered in the glyph view box.</summary>
/// <param name="Geometry">Parsed Avalonia geometry retained by the render plan.</param>
/// <param name="Fill">Validated fill token or color.</param>
/// <param name="Stroke">Validated stroke token or color.</param>
/// <param name="StrokeWidth">Stroke width in view-box units.</param>
/// <param name="StrokeLineCap">Validated SVG line-cap name.</param>
/// <param name="StrokeLineJoin">Validated SVG line-join name.</param>
internal sealed record PhysicalGlyphPath(
    Geometry Geometry,
    string Fill,
    string Stroke,
    decimal StrokeWidth,
    string StrokeLineCap,
    string StrokeLineJoin);

/// <summary>What <see cref="PhysicalGlyphImage" /> draws for one control: vector paths, a raster, or a fallback.</summary>
internal sealed record PhysicalGlyphRenderPlan
{
    /// <summary>Resolved physical control identity; null when no device mapping is available.</summary>
    internal required GlyphControlId? PhysicalControl { get; init; }

    /// <summary>Why device artwork could not be selected, or None for a resolved device glyph.</summary>
    internal required PhysicalGlyphFallbackReason FallbackReason { get; init; }

    /// <summary>Vector coordinate bounds; null for a nonvector plan.</summary>
    internal required GlyphViewBox? ViewBox { get; init; }

    /// <summary>Validated vector paths; empty for raster artwork or a fallback.</summary>
    internal required IReadOnlyList<PhysicalGlyphPath> Paths { get; init; }

    /// <summary>Validated raster PNG bytes; empty for vector artwork or a fallback.</summary>
    internal required ReadOnlyMemory<byte> RasterPng { get; init; }

    /// <summary>Whether the plan contains either vector or raster device artwork.</summary>
    internal bool UsesDeviceArtwork => Paths.Count > 0 || !RasterPng.IsEmpty;
}
