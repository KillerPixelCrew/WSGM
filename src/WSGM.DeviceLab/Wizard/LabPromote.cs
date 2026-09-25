using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Wizard;

/// <summary>What <c>promote</c> wrote.</summary>
internal sealed record LabPromoteResult
{
    /// <summary>The new record file.</summary>
    public required string OutputPath { get; init; }

    /// <summary>ID of the written record.</summary>
    public required string RecordId { get; init; }

    /// <summary>The record it was built from, or null for a new device.</summary>
    public string? SourceRecordId { get; init; }

    /// <summary>Fields whose evidence was written, in review order.</summary>
    public required IReadOnlyList<string> Promoted { get; init; }

    /// <summary>Disagreements with a proposal that were left out because <c>--field</c> did not name them.</summary>
    public IReadOnlyList<string> NotPromoted { get; init; } = [];
}

/// <summary>
///     Builds a curated knowledge record from a reviewed report: the confirmed record with the fields the
///     evidence supports updated and marked <see cref="DeviceKnowledgeSource.LabConfirmed" />.
/// </summary>
/// <remarks>
///     A curated record keeps its ID, so the output replaces its file after review. An extracted record
///     becomes a new curated <c>wsgm.*</c> record that supersedes it, because extracted files are generated.
///     A device without a record gets a new curated record from the observed identity. The output is
///     parsed back with <see cref="DeviceKnowledgeBase.Parse" /> and validated beside the embedded records
///     before anything is written, and it is only ever written to a new file.
/// </remarks>
internal static class LabPromote
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitDefaults } }
    };

    /// <summary>Reviews a report and writes the promoted record to a new file.</summary>
    /// <param name="reportPath">The <c>.wsgmlab</c> report.</param>
    /// <param name="outputPath">New record file.</param>
    /// <param name="fields">Field IDs to promote; every confirmation and new fact when empty.</param>
    /// <param name="boundaries">Output path boundaries.</param>
    /// <param name="knowledge">Knowledge base; the embedded one when null.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="InvalidDataException">A field is unknown or not promotable, or the record is invalid.</exception>
    /// <exception cref="IOException">The output path is refused or exists.</exception>
    public static LabPromoteResult Run(
        string reportPath,
        string outputPath,
        IReadOnlyCollection<string> fields,
        DeviceLabPathBoundaries boundaries,
        DeviceKnowledgeBase? knowledge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(boundaries);
        knowledge ??= DeviceKnowledgeBase.Default;
        var review = LabReview.Review(reportPath, knowledge);
        var (record, applied, left) = Build(review, fields, knowledge);
        var json = Serialize(record);

        var decision = DeviceLabOutputPathPolicy.Evaluate(outputPath, DeviceLabOutputTargetKind.NewFile, boundaries);
        if (!decision.IsAllowed || decision.FullPath is null)
        {
            throw new IOException(decision.Reason ?? "The record output path was refused.");
        }

        var full = decision.FullPath;
        if (File.Exists(full) || Directory.Exists(full))
        {
            throw new IOException($"{full} already exists; promote writes only a new file.");
        }

        var staging = DurableFile.StagingPath(full);
        try
        {
            DurableFile.WriteNewText(staging, json);
            File.Move(staging, full, false);
        }
        catch
        {
            DurableFile.TryDeleteFile(staging);
            throw;
        }

        return new LabPromoteResult
        {
            OutputPath = full,
            RecordId = record.Id,
            SourceRecordId = review.KnowledgeRecord?.Id,
            Promoted = [.. applied.Select(item => item.Field).Distinct(StringComparer.Ordinal)],
            NotPromoted = left
        };
    }

    /// <summary>Builds the promoted record in memory and checks it parses and validates.</summary>
    /// <param name="review">Review of the report.</param>
    /// <param name="fields">Field IDs to promote; every confirmation and new fact when empty.</param>
    /// <param name="knowledge">Knowledge base the record must fit into.</param>
    /// <returns>The record, the applied items and the disagreements left out.</returns>
    /// <exception cref="InvalidDataException">A field is unknown or not promotable, or the record is invalid.</exception>
    public static (DeviceKnowledgeRecord Record, IReadOnlyList<LabReviewItem> Applied, IReadOnlyList<string> NotPromoted)
        Build(LabReviewResult review, IReadOnlyCollection<string> fields, DeviceKnowledgeBase knowledge)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(knowledge);
        var record = BaseRecord(review, knowledge);
        var selected = Select(review, fields, record.Identity.Count == 0);
        foreach (var item in selected)
        {
            record = Apply(record, review, item);
        }

        // Round trip through the strict reader, then validate beside the embedded records so an ID
        // clash or a broken supersedes link fails here, not when the file is added to the build.
        var parsed = DeviceKnowledgeBase.Parse(Serialize(record));
        DeviceKnowledgeBase.Create([.. knowledge.Records.Where(item => item.Id != parsed.Id), parsed]);
        IReadOnlyList<string> left = fields.Count > 0
            ? []
            :
            [
                .. review.Items.Where(item => item is { Promotable: true, PromotedByDefault: false })
                    .Select(item => item.Field).Distinct(StringComparer.Ordinal)
            ];
        return (parsed, selected, left);
    }

    /// <summary>Serializes a record the way curated records are written.</summary>
    /// <param name="record">Record.</param>
    /// <returns>JSON with a trailing newline.</returns>
    internal static string Serialize(DeviceKnowledgeRecord record)
    {
        return JsonSerializer.Serialize(record, WriteOptions) + "\n";
    }

    private static List<LabReviewItem> Select(LabReviewResult review, IReadOnlyCollection<string> fields, bool needsIdentity)
    {
        List<LabReviewItem> selected;
        if (fields.Count == 0)
        {
            selected = [.. review.Items.Where(item => item.PromotedByDefault)];
        }
        else
        {
            selected = [];
            foreach (var field in fields.Distinct(StringComparer.Ordinal))
            {
                var matches = review.Items.Where(item => item.Field == field).ToArray();
                if (matches.Length == 0)
                {
                    throw new InvalidDataException($"The review has no field '{field}'.");
                }

                var promotable = matches.Where(item => item.Promotable).ToArray();
                if (promotable.Length == 0)
                {
                    throw new InvalidDataException(
                        $"Field '{field}' cannot be promoted: {matches[0].Verdict}, {matches[0].Detail ?? "no proposal"}");
                }

                selected.AddRange(promotable);
            }

            // Review order, not command-line order, so the output does not depend on how fields are listed.
            selected = [.. review.Items.Where(selected.Contains)];
        }

        // A new record cannot exist without an identity rule.
        if (needsIdentity && !selected.Any(item => item.Proposal is LabIdentityProposal))
        {
            var identity = review.Items.FirstOrDefault(item => item is { Promotable: true, Proposal: LabIdentityProposal });
            if (identity is null)
            {
                throw new InvalidDataException(
                    "The report has no usable identity, so a record cannot be built for this device.");
            }

            selected.Insert(0, identity);
        }

        return selected;
    }

    private static DeviceKnowledgeRecord BaseRecord(LabReviewResult review, DeviceKnowledgeBase knowledge)
    {
        var source = review.KnowledgeRecord;
        if (source is { Status: DeviceKnowledgeStatus.Curated })
        {
            return source;
        }

        if (source is not null)
        {
            var id = "wsgm." + (source.Id.StartsWith("hc.", StringComparison.Ordinal) ? source.Id[3..] : source.Id);
            if (knowledge.Records.Any(item => item.Id == id))
            {
                throw new InvalidDataException(
                    $"A record {id} already exists; the tester confirmed the extracted {source.Id} instead of it.");
            }

            return source with
            {
                Id = id,
                Status = DeviceKnowledgeStatus.Curated,
                Supersedes = [source.Id],
                Provenance =
                [
                    .. source.Provenance,
                    new DeviceKnowledgeProvenance
                    {
                        Source = DeviceKnowledgeSource.LabConfirmed,
                        Reference = ReportReference(review),
                        Note = $"Curated from the extracted {source.Id} with the Device Lab wizard's evidence. Facts not marked LabConfirmed are still HC-derived."
                    }
                ]
            };
        }

        var name = review.ProductName is { } product
            ? review.Model is { } model ? $"{product} {model}" : product
            : review.RecordName ?? "Unknown device";
        var slug = LabButtonPlan.Slug(name);
        var newId = "wsgm." + (slug.Length == 0 ? "unknown-device" : slug);
        if (knowledge.Records.Any(item => item.Id == newId))
        {
            throw new InvalidDataException($"A record {newId} already exists; review the report against it.");
        }

        return new DeviceKnowledgeRecord
        {
            SchemaVersion = DeviceKnowledgeBase.SchemaVersion,
            Id = newId,
            DisplayName = name,
            Status = DeviceKnowledgeStatus.Curated,
            Provenance =
            [
                new DeviceKnowledgeProvenance
                {
                    Source = DeviceKnowledgeSource.LabConfirmed,
                    Reference = ReportReference(review),
                    Note = "New device recorded with the Device Lab wizard; no earlier record matched."
                }
            ]
        };
    }

    private static DeviceKnowledgeRecord Apply(DeviceKnowledgeRecord record, LabReviewResult review, LabReviewItem item)
    {
        switch (item.Proposal)
        {
            case LabIdentityProposal { Rule: null }:
                return record with { Provenance = [.. record.Provenance, Provenance(review, item, null, "Identity")] };
            case LabIdentityProposal { Rule: { } rule }:
                return record with
                {
                    Identity = record.Identity.Contains(rule) ? record.Identity : [rule, .. record.Identity],
                    Provenance = [.. record.Provenance, Provenance(review, item, null, "Identity rule")]
                };
            case LabButtonProposal { Original: var original, Button: var button }:
                {
                    var updated = button with { Provenance = Provenance(review, item, original?.Provenance, "Button") };
                    return record with { Buttons = Replace(record.Buttons, original, updated) };
                }
            case LabMechanismProposal { Original: var original, Mechanism: var mechanism }:
                {
                    var updated = mechanism with
                    {
                        Provenance = Provenance(review, item, original?.Provenance, "Mechanism")
                    };
                    return record with { Mechanisms = Replace(record.Mechanisms, original, updated) };
                }
            case LabAxisProposal { Kind: var kind, Map: var map }:
                {
                    var motion = record.Motion ?? new DeviceMotionKnowledge();
                    motion = kind == "gyrometer" ? motion with { Gyrometer = map } : motion with { Accelerometer = map };
                    return record with
                    {
                        Motion = motion with
                        {
                            Provenance = Provenance(review, item, record.Motion?.Provenance,
                                kind == "gyrometer" ? "Gyrometer map" : "Accelerometer map")
                        }
                    };
                }
            case LabCapabilityProposal { Capability: var capability }:
                return record with
                {
                    Capabilities = record.Capabilities.Contains(capability, StringComparer.Ordinal)
                        ? record.Capabilities
                        : [.. record.Capabilities, capability],
                    Provenance = [.. record.Provenance, Provenance(review, item, null, $"Capability {capability}")]
                };
            default:
                return record;
        }
    }

    private static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> items, T? original, T updated)
        where T : class
    {
        List<T> list = [.. items];
        var index = original is null ? -1 : list.FindIndex(item => ReferenceEquals(item, original));
        if (index >= 0)
        {
            list[index] = updated;
        }
        else
        {
            list.Add(updated);
        }

        return list;
    }

    private static DeviceKnowledgeProvenance Provenance(
        LabReviewResult review,
        LabReviewItem item,
        DeviceKnowledgeProvenance? earlier,
        string what)
    {
        var verdict = item.Verdict switch
        {
            LabReviewVerdict.Confirmed => "confirmed",
            LabReviewVerdict.Disagrees => "corrected",
            _ => "recorded"
        };
        var note = $"{what} {verdict} in the Device Lab wizard. {item.Detail}".Trim();
        if (earlier is not null && earlier.Source is not DeviceKnowledgeSource.LabConfirmed)
        {
            note += $" Earlier: {earlier.Source} {earlier.Reference}{(earlier.Note is null ? string.Empty : $" ({earlier.Note})")}.";
        }
        else if (earlier is not null)
        {
            // A second promotion into the same provenance (the two motion maps) keeps the first one's note.
            note += $" Earlier: {earlier.Reference}{(earlier.Note is null ? string.Empty : $" ({earlier.Note})")}.";
        }

        return new DeviceKnowledgeProvenance
        {
            Source = DeviceKnowledgeSource.LabConfirmed,
            Reference = $"{review.Report}: {item.Evidence}",
            Note = note
        };
    }

    private static string ReportReference(LabReviewResult review)
    {
        return review.ProjectId is null ? review.Report : $"{review.Report}: project {review.ProjectId}";
    }

    // Curated records leave out false flags and empty lists; required members are always written.
    private static void OmitDefaults(JsonTypeInfo info)
    {
        foreach (var property in info.Properties)
        {
            if (property.IsRequired)
            {
                continue;
            }

            if (property.PropertyType == typeof(bool))
            {
                property.ShouldSerialize = (_, value) => value is true;
            }
            else if (property.PropertyType != typeof(string)
                     && typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
            {
                property.ShouldSerialize = (_, value) => value is IEnumerable enumerable && enumerable.GetEnumerator().MoveNext();
            }
        }
    }
}
