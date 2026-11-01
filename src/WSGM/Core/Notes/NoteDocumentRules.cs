using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WSGM.Core;

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
