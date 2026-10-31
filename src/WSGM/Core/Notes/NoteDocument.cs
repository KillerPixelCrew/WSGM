using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

/// <summary>Notes preferences and display names kept beside the pages.</summary>
public sealed class NoteSettings
{
    /// <summary>The settings format this build writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Shortest selectable screenshot countdown, in seconds.</summary>
    public const int MinimumCountdown = 5;

    /// <summary>Longest selectable screenshot countdown, in seconds.</summary>
    public const int MaximumCountdown = 10;

    /// <summary>The format the file was written in.</summary>
    public int Format { get; set; } = CurrentFormat;

    /// <summary>The countdown the next screenshot offers first, in seconds.</summary>
    public int CaptureCountdownSeconds { get; set; } = 7;

    /// <summary>The last known display name of each game notebook, by scope.</summary>
    /// <remarks>Kept so a notebook stays readable after its game is uninstalled or Steam is unavailable.</remarks>
    public Dictionary<string, string> NotebookTitles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Brings a countdown into the selectable range.</summary>
    /// <param name="seconds">The requested countdown, in seconds.</param>
    /// <returns>The countdown, from <see cref="MinimumCountdown" /> to <see cref="MaximumCountdown" />.</returns>
    public static int ClampCountdown(int seconds)
    {
        return Math.Clamp(seconds, MinimumCountdown, MaximumCountdown);
    }
}

/// <summary>Source-generated metadata for every Notes file.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NotePage))]
[JsonSerializable(typeof(NoteSettings))]
internal sealed partial class NoteJsonContext : JsonSerializerContext;

/// <summary>Notebook identities.</summary>
/// <remarks>
///     <list type="bullet">
///         <item><c>global</c>: the notebook that belongs to no game.</item>
///         <item><c>steam:&lt;appid&gt;</c>: a Steam title, keyed by AppID.</item>
///         <item>
///             <c>library:&lt;source&gt;:&lt;key&gt;</c>: a title WSGM's Game Library imported, keyed by its
///             durable source identity, so recreating its Steam shortcut or moving it to another drive
///             keeps its notes.
///         </item>
///         <item><c>steam:&lt;shortcut appid&gt;</c>: a non-Steam shortcut WSGM did not create.</item>
///         <item><c>process:&lt;executable&gt;</c>: an application Steam does not know about.</item>
///     </list>
///     A title is never an identity.
/// </remarks>
public static partial class NoteScope
{
    /// <summary>The Global notebook.</summary>
    public const string Global = "global";

    /// <summary>The notebook of a Steam AppID, including a shortcut's generated id.</summary>
    /// <param name="appId">The app id.</param>
    /// <returns>The scope.</returns>
    public static string ForSteamApp(uint appId)
    {
        return "steam:" + appId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The notebook of a Game Library title.</summary>
    /// <param name="source">The library source id.</param>
    /// <param name="key">The title's stable key in that source.</param>
    /// <returns>The scope.</returns>
    public static string ForLibraryTitle(string source, string key)
    {
        return $"library:{source}:{key}";
    }

    /// <summary>Whether a value is a well-formed scope.</summary>
    /// <param name="scope">The candidate.</param>
    /// <returns>True for Global or a recognized, nonempty identity.</returns>
    public static bool IsValid(string? scope)
    {
        if (scope is null || scope.Length == 0 || scope.Any(char.IsControl))
        {
            return false;
        }

        return scope == Global
               || SteamPattern().IsMatch(scope)
               || (scope.StartsWith("library:", StringComparison.Ordinal) && scope.IndexOf(':', 8) > 8
                                                                         && !scope.EndsWith(':'))
               || (scope.StartsWith("process:", StringComparison.Ordinal) && scope.Length > 8);
    }

    [GeneratedRegex("^steam:[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamPattern();
}

/// <summary>Shape rules shared by the store, the service and both surfaces' command readers.</summary>
public static partial class NoteDocumentRules
{
    /// <summary>A new identity for a page, element or stroke.</summary>
    /// <returns>32 lowercase hexadecimal digits.</returns>
    public static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>Whether a value is a well-formed page, element or stroke identity.</summary>
    /// <param name="id">The candidate.</param>
    /// <returns>True for 32 lowercase hexadecimal digits.</returns>
    public static bool IsId(string? id)
    {
        return id is not null && IdPattern().IsMatch(id);
    }

    /// <summary>Whether a value is a <c>#RRGGBB</c> color.</summary>
    /// <param name="color">The candidate.</param>
    /// <returns>True for six hexadecimal digits after a hash.</returns>
    public static bool IsColor(string? color)
    {
        return color is not null && ColorPattern().IsMatch(color);
    }

    /// <summary>
    ///     Checks a page's shape. Content length is never limited; only types, identities, finite numbers and
    ///     references are.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="error">Why it is malformed.</param>
    /// <returns>True when every field is well formed.</returns>
    public static bool TryValidate(NotePage? page, out string error)
    {
        error = "";
        if (page is null)
        {
            error = "The page is missing.";
            return false;
        }

        if (!IsId(page.Id))
        {
            error = "The page id is malformed.";
            return false;
        }

        if (!NoteScope.IsValid(page.Scope))
        {
            error = "The page's notebook is not a recognized identity.";
            return false;
        }

        if (page.Title is null || page.Elements is null || page.Strokes is null || !double.IsFinite(page.Order)
            || page.Revision < 1)
        {
            error = "The page is incomplete.";
            return false;
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var element in page.Elements)
        {
            if (element is null || !IsId(element.Id) || !ids.Add(element.Id))
            {
                error = "An element id is malformed or repeated.";
                return false;
            }

            if (!double.IsFinite(element.X) || !double.IsFinite(element.Y)
                                            || !double.IsFinite(element.Width) || !double.IsFinite(element.Height)
                                            || element.Width <= 0 || element.Height <= 0)
            {
                error = "An element's position or size is not a positive finite number.";
                return false;
            }

            switch (element.Kind)
            {
                case NoteElement.TextKind when element.Text is not null:
                    break;
                case NoteElement.ImageKind when NoteAssets.IsAssetId(element.Asset)
                                                && element.PixelWidth > 0 && element.PixelHeight > 0:
                    break;
                default:
                    error = "An element is neither a text block nor an image with an asset.";
                    return false;
            }
        }

        foreach (var stroke in page.Strokes)
        {
            if (!TryValidate(stroke, out error) || !ids.Add(stroke.Id))
            {
                error = error.Length > 0 ? error : "A stroke id is repeated.";
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks one stroke's shape.</summary>
    /// <param name="stroke">The stroke.</param>
    /// <param name="error">Why it is malformed.</param>
    /// <returns>True when the stroke is drawable.</returns>
    public static bool TryValidate(NoteStroke? stroke, out string error)
    {
        error = "";
        if (stroke is null || !IsId(stroke.Id))
        {
            error = "A stroke id is malformed.";
            return false;
        }

        if (stroke.Tool is not (NoteStroke.PenTool or NoteStroke.HighlighterTool) || !IsColor(stroke.Color)
            || !double.IsFinite(stroke.Width) || stroke.Width <= 0)
        {
            error = "A stroke's tool, color or width is invalid.";
            return false;
        }

        if (stroke.Points is null || stroke.Points.Count < 2 || stroke.Points.Count % 2 != 0
            || stroke.Points.Any(value => !float.IsFinite(value)))
        {
            error = "A stroke's points are not finite x, y pairs.";
            return false;
        }

        if (stroke.Pressure is null || (stroke.Pressure.Count != 0 && stroke.Pressure.Count != stroke.Points.Count / 2)
                                    || stroke.Pressure.Any(value => !float.IsFinite(value) || value < 0 || value > 1))
        {
            error = "A stroke's pressure does not match its points.";
            return false;
        }

        return true;
    }

    /// <summary>The asset ids a page refers to.</summary>
    /// <param name="page">The page.</param>
    /// <returns>Every distinct image asset id.</returns>
    public static IEnumerable<string> AssetsOf(NotePage page)
    {
        return page.Elements.Where(element => element.Kind == NoteElement.ImageKind && element.Asset is not null)
            .Select(element => element.Asset!).Distinct(StringComparer.Ordinal);
    }

    /// <summary>The page height a renderer needs to show every element and stroke.</summary>
    /// <param name="page">The page.</param>
    /// <returns>At least a 16:10 screen of page, plus room below the lowest content.</returns>
    public static double ContentHeight(NotePage page)
    {
        var bottom = NotePage.PageWidth * 10 / 16;
        foreach (var element in page.Elements)
        {
            bottom = Math.Max(bottom, element.Y + element.Height + 240);
        }

        foreach (var stroke in page.Strokes)
        {
            for (var index = 1; index < stroke.Points.Count; index += 2)
            {
                bottom = Math.Max(bottom, stroke.Points[index] + 240);
            }
        }

        return bottom;
    }

    /// <summary>The strokes an eraser touching a point removes.</summary>
    /// <param name="page">The page.</param>
    /// <param name="x">Eraser center x in page units.</param>
    /// <param name="y">Eraser center y in page units.</param>
    /// <param name="radius">Eraser radius in page units.</param>
    /// <returns>Ids of every stroke with a segment within the radius plus half its own width.</returns>
    /// <remarks>A stroke eraser, as in OneNote: a touched stroke goes whole, so undo restores it whole.</remarks>
    public static IReadOnlyList<string> StrokesAt(NotePage page, double x, double y, double radius)
    {
        List<string> hits = [];
        foreach (var stroke in page.Strokes)
        {
            var reach = radius + stroke.Width / 2;
            var points = stroke.Points;
            if (points.Count == 2)
            {
                if (Distance(x, y, points[0], points[1]) <= reach)
                {
                    hits.Add(stroke.Id);
                }

                continue;
            }

            for (var index = 2; index + 1 < points.Count; index += 2)
            {
                if (SegmentDistance(x, y, points[index - 2], points[index - 1], points[index], points[index + 1])
                    <= reach)
                {
                    hits.Add(stroke.Id);
                    break;
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     Drops points that add nothing visible: closer than a quarter of the stroke width to the last kept
    ///     point. Input arrives at the digitizer's rate; this keeps the stored stroke proportional to its shape.
    /// </summary>
    /// <param name="points">Raw x, y pairs.</param>
    /// <param name="pressure">Raw pressure per point, or empty.</param>
    /// <param name="width">The stroke width.</param>
    /// <returns>The kept points and their pressure; the first and last points always stay.</returns>
    public static (List<float> Points, List<float> Pressure) Simplify(
        IReadOnlyList<float> points, IReadOnlyList<float> pressure, double width)
    {
        List<float> keptPoints = [];
        List<float> keptPressure = [];
        var count = points.Count / 2;
        var minimum = Math.Max(0.5, width / 4);
        for (var index = 0; index < count; index++)
        {
            var x = points[index * 2];
            var y = points[index * 2 + 1];
            var last = index == count - 1;
            if (keptPoints.Count >= 2 && !last
                                      && Distance(x, y, keptPoints[^2], keptPoints[^1]) < minimum)
            {
                continue;
            }

            keptPoints.Add(MathF.Round(x, 1));
            keptPoints.Add(MathF.Round(y, 1));
            if (pressure.Count == count)
            {
                keptPressure.Add(MathF.Round(pressure[index], 3));
            }
        }

        return (keptPoints, keptPressure);
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var length = dx * dx + dy * dy;
        if (length <= 0)
        {
            return Distance(px, py, ax, ay);
        }

        var t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / length, 0, 1);
        return Distance(px, py, ax + t * dx, ay + t * dy);
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
}
