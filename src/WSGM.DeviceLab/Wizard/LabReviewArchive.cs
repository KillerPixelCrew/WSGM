using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One segment of a returned report, as its manifest lists it.</summary>
/// <param name="Id">Segment ID.</param>
/// <param name="Status">Status the tester left it in.</param>
/// <param name="Attempts">Attempt count; the highest-numbered attempt is current.</param>
/// <param name="Summary">One-line summary.</param>
internal sealed record LabReviewSegment(string Id, string Status, int Attempts, string? Summary)
{
    /// <summary>Archive path prefix of the current attempt, ending in a slash; null when it never ran.</summary>
    public string? Prefix => Attempts > 0 ? $"segments/{Id}/attempt-{Attempts}/" : null;

    /// <summary>The current attempt as a reference, for example <c>segments/buttons/a/attempt-2</c>.</summary>
    public string Reference => Attempts > 0 ? $"segments/{Id}/attempt-{Attempts}" : $"segments/{Id}";
}

/// <summary>
///     A returned <c>.wsgmlab</c> report opened for review, read entry by entry into memory and never
///     extracted.
/// </summary>
/// <remarks>
///     Reads are bounded three ways: the entry count, the size of any one entry (counted while reading,
///     not taken from the archive's own claim) and the total read across the review.
/// </remarks>
internal sealed class LabReviewArchive : IDisposable
{
    /// <summary>Most entries a report may hold.</summary>
    public const int MaximumEntries = 65536;

    /// <summary>Most bytes one review reads in total.</summary>
    public const long MaximumTotalBytes = LabExport.MaximumTotalBytes;

    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private readonly FileStream _stream;
    private long _read;

    private LabReviewArchive(string path, FileStream stream, ZipArchive archive)
    {
        FileName = Path.GetFileName(path);
        _stream = stream;
        _archive = archive;
        _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            // A repeated name is ambiguous evidence; the first one wins, as ZipArchive.GetEntry would.
            _entries.TryAdd(entry.FullName, entry);
        }

        Manifest = ReadJson(LabProject.ManifestFileName) as JsonObject
                   ?? throw new InvalidDataException($"{FileName} has no {LabProject.ManifestFileName}.");
        List<LabReviewSegment> segments = [];
        foreach (var node in Objects(Manifest["segments"]))
        {
            if (Text(node["id"]) is not { } id)
            {
                continue;
            }

            try
            {
                LabProject.ValidateSegmentId(id);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"{FileName}: {ex.Message}", ex);
            }

            segments.Add(new LabReviewSegment(
                id,
                Text(node["status"]) ?? "unknown",
                Math.Max(0, Integer(node["attempts"]) ?? 0),
                Text(node["summary"])));
        }

        Segments = segments;
    }

    /// <summary>The report's file name, used in provenance references.</summary>
    public string FileName { get; }

    /// <summary>The project manifest.</summary>
    public JsonObject Manifest { get; }

    /// <summary>Every segment in manifest order.</summary>
    public IReadOnlyList<LabReviewSegment> Segments { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }

    /// <summary>Opens a report.</summary>
    /// <param name="path">Report path.</param>
    /// <returns>The open report.</returns>
    /// <exception cref="InvalidDataException">The file is not a Device Lab report or exceeds a bound.</exception>
    public static LabReviewArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            ZipArchive archive = new(stream, ZipArchiveMode.Read, true);
            try
            {
                if (archive.Entries.Count > MaximumEntries)
                {
                    throw new InvalidDataException($"{Path.GetFileName(path)} has more than {MaximumEntries} entries.");
                }

                return new LabReviewArchive(path, stream, archive);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>A segment by ID, or null when the manifest does not list it.</summary>
    /// <param name="id">Segment ID.</param>
    /// <returns>The segment.</returns>
    public LabReviewSegment? Segment(string id)
    {
        return Segments.FirstOrDefault(segment => segment.Id == id);
    }

    /// <summary>The JSON files directly inside a segment's current attempt, in ordinal order.</summary>
    /// <param name="segment">Segment.</param>
    /// <returns>Archive paths.</returns>
    public IReadOnlyList<string> JsonFiles(LabReviewSegment segment)
    {
        if (segment.Prefix is not { } prefix)
        {
            return [];
        }

        return
        [
            .. _entries.Keys
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
                               && name.EndsWith(".json", StringComparison.Ordinal)
                               && name.IndexOf('/', prefix.Length) < 0)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>Reads one evidence file of a segment's current attempt.</summary>
    /// <param name="segment">Segment.</param>
    /// <param name="fileName">File name inside the attempt, for example <c>candidates.json</c>.</param>
    /// <returns>The JSON, or null when the file is absent.</returns>
    public JsonNode? ReadEvidence(LabReviewSegment segment, string fileName)
    {
        return segment.Prefix is { } prefix ? ReadJson(prefix + fileName) : null;
    }

    /// <summary>Reads one JSON entry.</summary>
    /// <param name="name">Archive path.</param>
    /// <returns>The JSON, or null when the entry is absent.</returns>
    /// <exception cref="InvalidDataException">The entry is too large or not JSON.</exception>
    public JsonNode? ReadJson(string name)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            return null;
        }

        var limit = Math.Min(LabReport.MaximumEntryBytes, MaximumTotalBytes - _read);
        if (entry.Length > limit)
        {
            throw new InvalidDataException($"{name} is larger than this review may read.");
        }

        using MemoryStream buffer = new();
        using (var stream = entry.Open())
        {
            var chunk = new byte[81920];
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + count > limit)
                {
                    throw new InvalidDataException($"{name} is larger than this review may read.");
                }

                buffer.Write(chunk, 0, count);
            }
        }

        _read += buffer.Length;
        try
        {
            return JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name} is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>A member of a JSON object, or null when the node is not an object.</summary>
    /// <param name="node">Node.</param>
    /// <param name="name">Member name.</param>
    /// <returns>The member.</returns>
    public static JsonNode? Get(JsonNode? node, string name)
    {
        return (node as JsonObject)?[name];
    }

    /// <summary>A JSON string leaf, or null.</summary>
    /// <param name="node">Node.</param>
    /// <returns>The string.</returns>
    public static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>A JSON integer leaf, or null.</summary>
    /// <param name="node">Node.</param>
    /// <returns>The integer.</returns>
    public static int? Integer(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<double>(out var real) && real is >= int.MinValue and <= int.MaxValue
                                                       && Math.Abs(real - Math.Round(real)) < 1e-9
            ? (int)Math.Round(real)
            : null;
    }

    /// <summary>A JSON number leaf, or null.</summary>
    /// <param name="node">Node.</param>
    /// <returns>The number.</returns>
    public static double? Number(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
    }

    /// <summary>A JSON boolean leaf, or null.</summary>
    /// <param name="node">Node.</param>
    /// <returns>The boolean.</returns>
    public static bool? Boolean(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    }

    /// <summary>The objects of a JSON array, skipping anything else.</summary>
    /// <param name="node">Array node.</param>
    /// <returns>The objects.</returns>
    public static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        return (node as JsonArray ?? []).OfType<JsonObject>();
    }

    /// <summary>The strings of a JSON array, skipping anything else.</summary>
    /// <param name="node">Array node.</param>
    /// <returns>The strings.</returns>
    public static IEnumerable<string> Strings(JsonNode? node)
    {
        foreach (var item in node as JsonArray ?? [])
        {
            if (Text(item) is { } text)
            {
                yield return text;
            }
        }
    }
}
