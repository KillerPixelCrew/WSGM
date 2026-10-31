namespace WSGM.Core;

/// <summary>A positioned text block or image on a page.</summary>
public sealed class NoteElement
{
    /// <summary>The kind of a text block.</summary>
    public const string TextKind = "text";

    /// <summary>The kind of an image.</summary>
    public const string ImageKind = "image";

    /// <summary>Stable identity within the page: 32 lowercase hexadecimal digits.</summary>
    public string Id { get; set; } = "";

    /// <summary><see cref="TextKind" /> or <see cref="ImageKind" />.</summary>
    public string Kind { get; set; } = TextKind;

    /// <summary>Left edge in page units.</summary>
    public double X { get; set; }

    /// <summary>Top edge in page units.</summary>
    public double Y { get; set; }

    /// <summary>Width in page units.</summary>
    public double Width { get; set; }

    /// <summary>Height in page units. A text block grows to its content; this is its minimum.</summary>
    public double Height { get; set; }

    /// <summary>The block's plain text, for a text element.</summary>
    public string? Text { get; set; }

    /// <summary>The asset id of an image element; see <see cref="NoteAssets" />.</summary>
    public string? Asset { get; set; }

    /// <summary>The image's pixel width, for aspect-correct resizing.</summary>
    public int PixelWidth { get; set; }

    /// <summary>The image's pixel height.</summary>
    public int PixelHeight { get; set; }
}
