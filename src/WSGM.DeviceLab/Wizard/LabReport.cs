using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WSGM.DeviceLab.Wizard;

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

/// <summary>Reads a returned report without extracting it.</summary>
internal static class LabReport
{
    /// <summary>Largest entry read into memory.</summary>
    public const long MaximumEntryBytes = 64L * 1024 * 1024;

    /// <summary>Summarizes a report.</summary>
    /// <param name="path">Report path.</param>
    /// <returns>The summary.</returns>
    /// <exception cref="InvalidDataException">The file is not a Device Lab report.</exception>
    public static LabReportSummary Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifest = ReadJson(archive, LabProject.ManifestFileName)
                       ?? throw new InvalidDataException($"{path} has no {LabProject.ManifestFileName}.");
        var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        List<LabReportSegment> segments = [];
        Dictionary<string, JsonNode?> buttons = new(StringComparer.Ordinal);
        foreach (var segment in manifest["segments"]?.AsArray() ?? [])
        {
            var id = segment?["id"]?.GetValue<string>();
            if (id is null)
            {
                continue;
            }

            var attempts = segment!["attempts"]?.GetValue<int>() ?? 0;
            var prefix = $"segments/{id}/attempt-{attempts}/";
            List<string> evidence = [.. names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Order()];
            segments.Add(new LabReportSegment(
                id,
                segment["status"]?.ToString() ?? "unknown",
                segment["summary"]?.ToString(),
                attempts,
                evidence));
            if (id.StartsWith(LabStages.Buttons + "/", StringComparison.Ordinal)
                && ReadJson(archive, prefix + "candidates.json") is { } candidates)
            {
                buttons[id[(LabStages.Buttons.Length + 1)..]] = new JsonObject
                {
                    ["answer"] = candidates["answer"]?.DeepClone(),
                    ["candidates"] = new JsonArray(
                        [.. (candidates["candidates"]?.AsArray() ?? []).Take(5).Select(item => item?.DeepClone())])
                };
            }
        }

        return new LabReportSummary(
            manifest["device"]?.DeepClone(),
            manifest["toolVersion"]?.ToString(),
            segments,
            buttons);
    }

    private static JsonNode? ReadJson(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        if (entry.Length > MaximumEntryBytes)
        {
            throw new InvalidDataException($"{name} is larger than {MaximumEntryBytes / (1024 * 1024)} MiB.");
        }

        using var stream = entry.Open();
        try
        {
            return JsonNode.Parse(stream);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name} is not valid JSON: {ex.Message}", ex);
        }
    }
}
