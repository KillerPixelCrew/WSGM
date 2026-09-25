using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Identity;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

/// <summary>How one piece of lab evidence compares with the knowledge record.</summary>
internal enum LabReviewVerdict
{
    /// <summary>The evidence agrees with what the record believes.</summary>
    Confirmed,

    /// <summary>The evidence contradicts the record.</summary>
    Disagrees,

    /// <summary>The record has no belief and the evidence gives a clear answer.</summary>
    New,

    /// <summary>The evidence is missing, unclear or ambiguous.</summary>
    Unresolved,

    /// <summary>Recorded for the developer; nothing in the record to compare it with.</summary>
    Observed
}

/// <summary>A change to the knowledge record that the evidence supports.</summary>
internal abstract record LabReviewProposal;

/// <summary>Sets a button: confirms, replaces or adds one.</summary>
/// <param name="Original">The record's button it replaces, or null for a new button.</param>
/// <param name="Button">The button as the evidence shows it, without provenance.</param>
internal sealed record LabButtonProposal(DeviceButtonKnowledge? Original, DeviceButtonKnowledge Button)
    : LabReviewProposal;

/// <summary>Sets a motion axis map.</summary>
/// <param name="Kind"><c>gyrometer</c> or <c>accelerometer</c>.</param>
/// <param name="Map">The map in HC's convention, as records store it.</param>
internal sealed record LabAxisProposal(string Kind, DeviceAxisMap Map) : LabReviewProposal;

/// <summary>Confirms or adds a mechanism.</summary>
/// <param name="Original">The record's mechanism it confirms, or null for a new mechanism.</param>
/// <param name="Mechanism">The mechanism, without provenance.</param>
internal sealed record LabMechanismProposal(DeviceMechanismKnowledge? Original, DeviceMechanismKnowledge Mechanism)
    : LabReviewProposal;

/// <summary>Confirms or adds an HC capability flag.</summary>
/// <param name="Capability">Flag name, for example <c>FanControl</c>.</param>
internal sealed record LabCapabilityProposal(string Capability) : LabReviewProposal;

/// <summary>Confirms the identity, or adds the observed identity rule.</summary>
/// <param name="Rule">The rule to add, or null when an existing rule matched exactly.</param>
internal sealed record LabIdentityProposal(HardwareMatchRule? Rule) : LabReviewProposal;

/// <summary>One comparison between the lab evidence and the knowledge record.</summary>
internal sealed record LabReviewItem
{
    /// <summary>Stable field ID, for example <c>button:oem-left</c> or <c>motion:gyrometer</c>.</summary>
    public required string Field { get; init; }

    /// <summary><c>identity</c>, <c>buttons</c>, <c>motion</c>, <c>rumble</c>, <c>power</c> or <c>sleep</c>.</summary>
    public required string Area { get; init; }

    /// <summary>How the evidence compares.</summary>
    public required LabReviewVerdict Verdict { get; init; }

    /// <summary>What the record believes, readable.</summary>
    public string? Record { get; init; }

    /// <summary>What the lab saw, readable.</summary>
    public string? Observed { get; init; }

    /// <summary>Why the verdict is what it is.</summary>
    public string? Detail { get; init; }

    /// <summary>The segment attempt the evidence comes from, for example <c>segments/buttons/a/attempt-2</c>.</summary>
    public required string Evidence { get; init; }

    /// <summary>The top candidates and keys, for buttons; other supporting lines elsewhere.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];

    /// <summary>The record change this item supports, if any.</summary>
    [JsonIgnore]
    public LabReviewProposal? Proposal { get; init; }

    /// <summary>Whether <c>promote --field</c> can apply this item.</summary>
    public bool Promotable =>
        Proposal is not null && Verdict is LabReviewVerdict.Confirmed or LabReviewVerdict.New or LabReviewVerdict.Disagrees;

    /// <summary>
    ///     Whether <c>promote</c> without <c>--field</c> applies it: confirmations and new facts do, a
    ///     disagreement has to be named.
    /// </summary>
    public bool PromotedByDefault => Proposal is not null && Verdict is LabReviewVerdict.Confirmed or LabReviewVerdict.New;
}

/// <summary>The review of one returned report against the knowledge base.</summary>
internal sealed record LabReviewResult
{
    /// <summary>Report file name.</summary>
    public required string Report { get; init; }

    /// <summary>Project ID from the manifest.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Device Lab version that ran the test.</summary>
    public string? ToolVersion { get; init; }

    /// <summary>The record the tester confirmed.</summary>
    public string? RecordId { get; init; }

    /// <summary>Its display name.</summary>
    public string? RecordName { get; init; }

    /// <summary>Its review state.</summary>
    public DeviceKnowledgeStatus? RecordStatus { get; init; }

    /// <summary>Why no record could be compared, when none could.</summary>
    public string? RecordProblem { get; init; }

    /// <summary>Product name the tester typed when no record matched.</summary>
    public string? ProductName { get; init; }

    /// <summary>Model the tester typed when no record matched.</summary>
    public string? Model { get; init; }

    /// <summary>How many items agree with the record.</summary>
    public int Confirmations => Items.Count(item => item.Verdict is LabReviewVerdict.Confirmed);

    /// <summary>How many items contradict it.</summary>
    public int Disagreements => Items.Count(item => item.Verdict is LabReviewVerdict.Disagrees);

    /// <summary>Every comparison, in wizard order.</summary>
    public required IReadOnlyList<LabReviewItem> Items { get; init; }

    /// <summary>The record compared against.</summary>
    [JsonIgnore]
    public DeviceKnowledgeRecord? KnowledgeRecord { get; init; }

    /// <summary>The redacted inventory from the identity stage.</summary>
    [JsonIgnore]
    public MachineInventory? Inventory { get; init; }

    /// <summary>The observed identity.</summary>
    [JsonIgnore]
    public DeviceIdentitySnapshot? ObservedIdentity { get; init; }
}

/// <summary>
///     Compares a returned report with the knowledge record the tester confirmed and lists every
///     confirmation and disagreement.
/// </summary>
/// <remarks>
///     Candidates are evidence, not proof: a verdict is <see cref="LabReviewVerdict.Confirmed" /> only
///     when the record's own belief shows up in the recorded evidence, and a new fact is proposed only
///     when one source clearly answers.
/// </remarks>
internal static partial class LabReview
{
    private const int CandidateLines = 5;

    private static readonly string[] JunkIdentity =
    [
        "Default string", "To be filled by O.E.M.", "System Product Name", "System manufacturer",
        "Not Applicable", "N/A", "None", "Unknown"
    ];

    /// <summary>Reviews a report file.</summary>
    /// <param name="path">Report path.</param>
    /// <param name="knowledge">Knowledge base; the embedded one when null.</param>
    /// <returns>The review.</returns>
    /// <exception cref="System.IO.InvalidDataException">The file is not a Device Lab report.</exception>
    public static LabReviewResult Review(string path, DeviceKnowledgeBase? knowledge = null)
    {
        using var archive = LabReviewArchive.Open(path);
        return Review(archive, knowledge ?? DeviceKnowledgeBase.Default);
    }

    /// <summary>Reviews an open report.</summary>
    /// <param name="archive">Report.</param>
    /// <param name="knowledge">Knowledge base.</param>
    /// <returns>The review.</returns>
    public static LabReviewResult Review(LabReviewArchive archive, DeviceKnowledgeBase knowledge)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(knowledge);
        var device = archive.Manifest["device"];
        var recordId = LabReviewArchive.Text(LabReviewArchive.Get(device, "recordId"));
        var record = recordId is null
            ? null
            : knowledge.Records.FirstOrDefault(item => string.Equals(item.Id, recordId, StringComparison.Ordinal));
        string? problem = recordId is null
            ? "The tester did not confirm a knowledge record."
            : record is null
                ? $"Record {recordId} is not in this build's knowledge base."
                : null;

        List<LabReviewItem> items = [];
        var (inventory, identity) = ReviewIdentity(archive, record, items);
        ReviewButtons(archive, record, items);
        ReviewMotion(archive, record, items);
        ReviewRumble(archive, record, items);
        ReviewPower(archive, record, items);
        ReviewSleep(archive, record, items);
        return new LabReviewResult
        {
            Report = archive.FileName,
            ProjectId = LabReviewArchive.Text(archive.Manifest["id"]),
            ToolVersion = LabReviewArchive.Text(archive.Manifest["toolVersion"]),
            RecordId = recordId,
            RecordName = record?.DisplayName
                         ?? LabReviewArchive.Text(LabReviewArchive.Get(device, "displayName")),
            RecordStatus = record?.Status,
            RecordProblem = problem,
            ProductName = LabReviewArchive.Text(LabReviewArchive.Get(device, "productName")),
            Model = LabReviewArchive.Text(LabReviewArchive.Get(device, "model")),
            Items = items,
            KnowledgeRecord = record,
            Inventory = inventory,
            ObservedIdentity = identity
        };
    }

    /// <summary>Whether an identity value can be copied into a rule: present, not a placeholder and not redacted.</summary>
    /// <param name="value">Value.</param>
    /// <returns>The trimmed value, or null.</returns>
    internal static string? CleanIdentity(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Length > HardwareMatchRule.MaxFieldLength
            || (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            || trimmed.Any(char.IsControl)
            || JunkIdentity.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>The identity rule the observed machine gives: board manufacturer, board product and SKU.</summary>
    /// <param name="identity">Observed identity.</param>
    /// <returns>The rule, or null when the identity is too thin to name the device.</returns>
    internal static HardwareMatchRule? ObservedRule(DeviceIdentitySnapshot? identity)
    {
        if (identity is null)
        {
            return null;
        }

        var manufacturer = CleanIdentity(identity.BaseboardManufacturer);
        var product = CleanIdentity(identity.BaseboardProduct);
        var model = CleanIdentity(identity.SystemProduct);
        var sku = CleanIdentity(identity.SystemSku);
        if (manufacturer is not null && product is not null)
        {
            return new HardwareMatchRule
            {
                BaseboardManufacturer = manufacturer,
                BaseboardProduct = product,
                SystemSku = sku
            };
        }

        return model is null
            ? null
            : new HardwareMatchRule { BaseboardManufacturer = manufacturer, SystemModel = model, SystemSku = sku };
    }

    /// <summary>Describes a rule in one line.</summary>
    /// <param name="rule">Rule.</param>
    /// <returns>The set fields.</returns>
    internal static string DescribeRule(HardwareMatchRule rule)
    {
        List<string> parts = [];
        foreach (var (name, value) in new[]
                 {
                     ("baseboardManufacturer", rule.BaseboardManufacturer),
                     ("baseboardProduct", rule.BaseboardProduct),
                     ("systemModel", rule.SystemModel),
                     ("systemSku", rule.SystemSku),
                     ("processorName", rule.ProcessorName),
                     ("processorNameContains", rule.ProcessorNameContains),
                     ("baseboardVersion", rule.BaseboardVersion)
                 })
        {
            if (value is not null)
            {
                parts.Add($"{name}={value}");
            }
        }

        if (rule.Fallback)
        {
            parts.Add("fallback");
        }

        return string.Join(", ", parts);
    }

    private static (MachineInventory? Inventory, DeviceIdentitySnapshot? Identity) ReviewIdentity(
        LabReviewArchive archive,
        DeviceKnowledgeRecord? record,
        List<LabReviewItem> items)
    {
        var segment = archive.Segment(LabStages.Identity);
        if (segment?.Prefix is null)
        {
            items.Add(new LabReviewItem
            {
                Field = "identity",
                Area = "identity",
                Verdict = LabReviewVerdict.Unresolved,
                Record = record is null ? null : string.Join(" | ", record.Identity.Select(DescribeRule)),
                Detail = "The identity stage never ran.",
                Evidence = $"segments/{LabStages.Identity}"
            });
            return (null, null);
        }

        MachineInventory? inventory = null;
        if (archive.ReadEvidence(segment, "inventory.json") is { } inventoryJson)
        {
            try
            {
                inventory = inventoryJson.Deserialize(DeviceLabJsonContext.Default.MachineInventory);
            }
            catch (JsonException)
            {
                inventory = null;
            }
        }

        var facts = LabReviewArchive.Get(archive.ReadEvidence(segment, "identity.json"), "facts");
        var identity = inventory is not null
            ? DeviceKnowledgeIdentity.From(inventory) with
            {
                SystemManufacturer = inventory.Firmware.SystemManufacturer,
                BiosVersion = inventory.Firmware.BiosVersion
            }
            : facts is null
                ? null
                : new DeviceIdentitySnapshot
                {
                    BaseboardManufacturer = LabReviewArchive.Text(LabReviewArchive.Get(facts, "boardManufacturer")),
                    BaseboardProduct = LabReviewArchive.Text(LabReviewArchive.Get(facts, "boardName")),
                    BaseboardVersion = LabReviewArchive.Text(LabReviewArchive.Get(facts, "boardVersion")),
                    SystemProduct = LabReviewArchive.Text(LabReviewArchive.Get(facts, "systemModel")),
                    SystemSku = LabReviewArchive.Text(LabReviewArchive.Get(facts, "systemSku")),
                    BiosVersion = LabReviewArchive.Text(LabReviewArchive.Get(facts, "biosVersion")),
                    ProcessorName = LabReviewArchive.Text(LabReviewArchive.Get(facts, "processor"))
                };
        var observed = identity is null
            ? null
            : string.Join(", ", new[]
                {
                    ("board", identity.BaseboardManufacturer is null && identity.BaseboardProduct is null
                        ? null
                        : $"{identity.BaseboardManufacturer} {identity.BaseboardProduct}".Trim()),
                    ("model", identity.SystemProduct),
                    ("SKU", identity.SystemSku),
                    ("BIOS", identity.BiosVersion),
                    ("processor", identity.ProcessorName)
                }
                .Where(part => part.Item2 is not null)
                .Select(part => $"{part.Item1} {part.Item2}"));
        var rule = ObservedRule(identity);
        var recordText = record is null ? null : string.Join(" | ", record.Identity.Select(DescribeRule));
        LabReviewItem item;
        if (identity is null)
        {
            item = new LabReviewItem
            {
                Field = "identity",
                Area = "identity",
                Verdict = LabReviewVerdict.Unresolved,
                Record = recordText,
                Detail = "The identity evidence is missing.",
                Evidence = segment.Reference
            };
        }
        else if (record is null)
        {
            item = new LabReviewItem
            {
                Field = "identity",
                Area = "identity",
                Verdict = rule is null ? LabReviewVerdict.Unresolved : LabReviewVerdict.New,
                Observed = observed,
                Detail = rule is null
                    ? "The observed identity is too thin to name the device."
                    : $"No record; the observed rule would be {DescribeRule(rule)}.",
                Evidence = segment.Reference,
                Proposal = rule is null ? null : new LabIdentityProposal(rule)
            };
        }
        else
        {
            var match = HardwareMatcher.Match(record.Identity, identity);
            item = match switch
            {
                { Fallback: false } => new LabReviewItem
                {
                    Field = "identity",
                    Area = "identity",
                    Verdict = LabReviewVerdict.Confirmed,
                    Record = recordText,
                    Observed = observed,
                    Detail = $"Rule {match.Index + 1} matched: {string.Join(" ", match.Explanations)}",
                    Evidence = segment.Reference,
                    Proposal = new LabIdentityProposal(null)
                },
                _ => new LabReviewItem
                {
                    Field = "identity",
                    Area = "identity",
                    Verdict = rule is null ? LabReviewVerdict.Unresolved : LabReviewVerdict.Disagrees,
                    Record = recordText,
                    Observed = observed,
                    Detail = (match is null
                                 ? "No identity rule of the record matches the observed machine."
                                 : "Only the record's vendor-wide fallback rule matches.")
                             + (rule is null ? string.Empty : $" The observed rule would be {DescribeRule(rule)}."),
                    Evidence = segment.Reference,
                    Proposal = rule is null ? null : new LabIdentityProposal(rule)
                }
            };
        }

        items.Add(item);
        return (inventory, identity);
    }
}
