using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Reports;

/// <summary>One segment of a returned report, with its latest evidence.</summary>
/// <param name="Id">Segment ID.</param>
/// <param name="Status">Status as the tester left it.</param>
/// <param name="Summary">One-line summary.</param>
/// <param name="Attempts">Attempt count.</param>
/// <param name="Evidence">Latest attempt's files, by path inside the report.</param>
internal sealed record LabReportSegment(
    string Id,
    string Status,
    string? Summary,
    int Attempts,
    IReadOnlyList<string> Evidence);

/// <summary>A returned <c>.wsgmlab</c> report, read for the developer.</summary>
/// <param name="Device">Device identity the tester confirmed.</param>
/// <param name="ToolVersion">Device Lab version that ran the test.</param>
/// <param name="Segments">Every segment in wizard order.</param>
/// <param name="Buttons">Per control, the top candidates the capture attributed to it.</param>
internal sealed record LabReportSummary(
    JsonNode? Device,
    string? ToolVersion,
    IReadOnlyList<LabReportSegment> Segments,
    IReadOnlyDictionary<string, JsonNode?> Buttons);

/// <summary>Reads a returned report without extracting it, through the bounded <see cref="LabReviewArchive" />.</summary>
internal static class LabReport
{
    /// <summary>Summarizes a report.</summary>
    /// <param name="path">Report path.</param>
    /// <returns>The summary.</returns>
    /// <exception cref="InvalidDataException">The file is not a Device Lab report or exceeds a review bound.</exception>
    public static LabReportSummary Read(string path)
    {
        using var archive = LabReviewArchive.Open(path);
        List<LabReportSegment> segments = [];
        Dictionary<string, JsonNode?> buttons = new(StringComparer.Ordinal);
        foreach (var segment in archive.Segments)
        {
            segments.Add(new LabReportSegment(
                segment.Id,
                segment.Status,
                segment.Summary,
                segment.Attempts,
                archive.Files(segment)));
            if (segment.Id.StartsWith(LabStages.Buttons + "/", StringComparison.Ordinal)
                && archive.ReadEvidence(segment, "candidates.json") is { } candidates)
            {
                buttons[segment.Id[(LabStages.Buttons.Length + 1)..]] = new JsonObject
                {
                    ["answer"] = LabReviewArchive.Get(candidates, "answer")?.DeepClone(),
                    ["candidates"] = new JsonArray(
                    [
                        .. LabReviewArchive.Objects(LabReviewArchive.Get(candidates, "candidates"))
                            .Take(5)
                            .Select(item => item.DeepClone())
                    ])
                };
            }
        }

        return new LabReportSummary(
            archive.Manifest["device"]?.DeepClone(),
            archive.Manifest["toolVersion"]?.ToString(),
            segments,
            buttons);
    }
}
