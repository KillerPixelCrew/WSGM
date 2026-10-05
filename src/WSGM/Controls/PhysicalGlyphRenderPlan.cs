using System;
using System.Collections.Generic;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Controls;

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
    internal required GlyphControlId? PhysicalControl { get; init; }
    internal required PhysicalGlyphFallbackReason FallbackReason { get; init; }
    internal required GlyphViewBox? ViewBox { get; init; }
    internal required IReadOnlyList<PhysicalGlyphPath> Paths { get; init; }
    internal required ReadOnlyMemory<byte> RasterPng { get; init; }

    internal bool UsesDeviceArtwork => Paths.Count > 0 || !RasterPng.IsEmpty;
}
