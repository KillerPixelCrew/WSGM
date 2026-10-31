using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One vector ink stroke.</summary>
public sealed class NoteStroke
{
    /// <summary>An opaque pen stroke.</summary>
    public const string PenTool = "pen";

    /// <summary>A translucent marker stroke drawn under later pen strokes' weight.</summary>
    public const string HighlighterTool = "highlighter";

    /// <summary>Stable identity within the page: 32 lowercase hexadecimal digits.</summary>
    public string Id { get; set; } = "";

    /// <summary><see cref="PenTool" /> or <see cref="HighlighterTool" />.</summary>
    public string Tool { get; set; } = PenTool;

    /// <summary>Color as <c>#RRGGBB</c>.</summary>
    public string Color { get; set; } = "#FFFFFF";

    /// <summary>Nominal width in page units.</summary>
    public double Width { get; set; } = 4;

    /// <summary>Points as consecutive x, y pairs in page units.</summary>
    public List<float> Points { get; set; } = [];

    /// <summary>Optional pressure per point in [0, 1], parallel to the point pairs; empty without pen data.</summary>
    public List<float> Pressure { get; set; } = [];
}
