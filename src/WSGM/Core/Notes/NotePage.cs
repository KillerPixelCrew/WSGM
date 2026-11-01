using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>
///     One Notes page: typed text blocks and screenshots placed on a page, with a vector ink layer drawn
///     over them. Both the overlay and Steam render and edit this one structured document.
/// </summary>
/// <remarks>
///     <para>
///         The page is data, never markup: text is plain text drawn by each surface, images are references
///         to separately stored assets, and ink is a list of point strokes. Nothing in a page is ever
///         evaluated as HTML or script.
///     </para>
///     <para>
///         <see cref="Revision" /> covers the content an editor changes: the title, elements and strokes.
///         A save names the revision it started from and is refused when the page moved on, so a stale
///         editor can never silently replace newer content. Pin and order are organization, written
///         through their own operations, and do not invalidate an open draft.
///     </para>
/// </remarks>
public sealed class NotePage
{
    /// <summary>The page format this build writes and reads.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The page's logical width. Element and stroke coordinates are in these units.</summary>
    public const double PageWidth = 1280;

    /// <summary>The format the page was written in.</summary>
    public int Format { get; set; } = CurrentFormat;

    /// <summary>Stable identity: 32 lowercase hexadecimal digits, assigned at creation.</summary>
    public string Id { get; set; } = "";

    /// <summary>The notebook the page belongs to; see <see cref="NoteScope" />.</summary>
    public string Scope { get; set; } = NoteScope.Global;

    /// <summary>The page title shown in lists.</summary>
    public string Title { get; set; } = "";

    /// <summary>Whether the page is pinned to the top of its notebook.</summary>
    public bool Pinned { get; set; }

    /// <summary>Sort key within the notebook; lower values come first after the pinned pages.</summary>
    public double Order { get; set; }

    /// <summary>When the page was created.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>When the page content last changed.</summary>
    public DateTimeOffset Modified { get; set; }

    /// <summary>Content revision; 1 at creation and increased by every accepted content save.</summary>
    public long Revision { get; set; }

    /// <summary>Text blocks and images, back to front.</summary>
    public List<NoteElement> Elements { get; set; } = [];

    /// <summary>Ink strokes in drawing order, drawn above every element.</summary>
    public List<NoteStroke> Strokes { get; set; } = [];

    /// <summary>A detached deep copy.</summary>
    /// <returns>A copy sharing no lists or elements with this page.</returns>
    public NotePage Clone()
    {
        return JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(this, NoteJsonContext.Default.NotePage),
            NoteJsonContext.Default.NotePage)!;
    }
}
