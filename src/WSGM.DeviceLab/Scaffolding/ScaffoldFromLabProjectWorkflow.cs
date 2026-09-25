using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Scaffolding;

/// <summary>What a scaffold from a lab report prefilled.</summary>
internal sealed record LabScaffoldResult
{
    /// <summary>The generated project.</summary>
    public required PluginScaffoldResult Scaffold { get; init; }

    /// <summary>Report file name.</summary>
    public required string Report { get; init; }

    /// <summary>The promoted record the data came from.</summary>
    public required string RecordId { get; init; }

    /// <summary>Hardware rules in the manifest.</summary>
    public required IReadOnlyList<string> Hardware { get; init; }

    /// <summary>Buttons in <c>DeviceProfile.cs</c>.</summary>
    public required int Buttons { get; init; }

    /// <summary>Axis maps in <c>DeviceProfile.cs</c>: <c>gyrometer</c>, <c>accelerometer</c>.</summary>
    public required IReadOnlyList<string> AxisMaps { get; init; }

    /// <summary>Capability roles the evidence supports.</summary>
    public required IReadOnlyList<CapabilityRole> Capabilities { get; init; }

    /// <summary>Fields the review could not settle, for the plugin author to look at.</summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];

    /// <summary>Disagreements that were not promoted and so are not in the profile.</summary>
    public IReadOnlyList<string> Disagreements { get; init; } = [];
}

/// <summary>
///     Scaffolds a plugin project from a returned lab report: the minimal template with the exact identity
///     the report observed, the manifest's hardware and capability lists, and a <c>DeviceProfile.cs</c> of
///     identity rules, buttons, axis maps and capability roles.
/// </summary>
/// <remarks>
///     The data is the record <see cref="LabPromote" /> builds with its default selection (every
///     confirmation and new fact), so the scaffold and a promoted record never disagree. Disagreements
///     stay as the record has them until they are promoted explicitly.
/// </remarks>
internal static class ScaffoldFromLabProjectWorkflow
{
    /// <summary>File extension of a lab report.</summary>
    public const string Extension = ".wsgmlab";

    private static readonly (string Template, string Output) ProfileTemplate = ("DeviceProfile.cs.template",
        "DeviceProfile.cs");

    // Example roles the minimal template's in-memory example publishes; the host refuses unlisted roles.
    private static readonly CapabilityRole[] ExampleRoles =
        [CapabilityRole.GenericToggle, CapabilityRole.GenericReadOnly];

    private static readonly JsonSerializerOptions RuleJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>Whether a scaffold source is a lab report rather than a capture.</summary>
    /// <param name="path">Source path.</param>
    /// <returns>True for a <c>.wsgmlab</c> file.</returns>
    public static bool IsLabReport(string path)
    {
        return path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes a plugin starter prefilled from a lab report.</summary>
    /// <param name="reportPath">The <c>.wsgmlab</c> report.</param>
    /// <param name="outputDirectory">New explicit output directory.</param>
    /// <param name="boundaries">Filesystem safety boundaries.</param>
    /// <param name="usbEndpoint">
    ///     <c>VID:PID</c> or <c>VID:PID:release</c> of the controller endpoint when the report shows more than
    ///     one candidate.
    /// </param>
    /// <param name="knowledge">Knowledge base; the embedded one when null.</param>
    /// <param name="cancellationToken">Cancels rendering or publication.</param>
    /// <returns>What was written and prefilled.</returns>
    public static LabScaffoldResult Run(
        string reportPath,
        string outputDirectory,
        DeviceLabPathBoundaries boundaries,
        string? usbEndpoint = null,
        DeviceKnowledgeBase? knowledge = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(boundaries);
        knowledge ??= DeviceKnowledgeBase.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var review = LabReview.Review(reportPath, knowledge);
        var (record, _, left) = LabPromote.Build(review, [], knowledge);
        var inventory = review.Inventory
                        ?? throw new InvalidDataException("The report has no identity inventory to scaffold from.");
        var identity = ExactIdentity(inventory, record, usbEndpoint);
        var observed = review.ObservedIdentity ?? DeviceKnowledgeIdentity.From(inventory);

        // The manifest names this device: the record's exact rules that match what the report observed,
        // or the observed rule when none does.
        HardwareMatchRule[] rules =
        [
            .. record.Identity.Where(rule => !rule.Fallback && HardwareMatcher.Matches(rule, observed, []))
                .Take(HardwareMatchRule.MaxRules)
        ];
        if (rules.Length == 0)
        {
            rules = LabReview.ObservedRule(observed) is { } rule
                ? [rule]
                : throw new InvalidDataException("The report's identity is too thin to write a hardware rule.");
        }

        var buttons = record.Buttons.Where(button => button.WizardButton is not null || Confirmed(button.Provenance))
            .ToArray();
        var roles = Roles(record, review);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LAB_REPORT_CS"] = ScaffoldFromCaptureWorkflow.CSharp(review.Report),
            ["RECORD_ID_CS"] = ScaffoldFromCaptureWorkflow.CSharp(record.Id),
            ["HARDWARE_CS"] = string.Join(",\n", rules.Select(RuleCode)),
            ["BUTTONS_CS"] = string.Join(",\n", buttons.Select(ButtonCode)),
            ["GYROMETER_CS"] = MapCode(record.Motion?.Gyrometer, record.Motion?.Provenance, "gyrometer"),
            ["ACCELEROMETER_CS"] = MapCode(record.Motion?.Accelerometer, record.Motion?.Provenance, "accelerometer"),
            ["CAPABILITIES_CS"] = string.Join(",\n", roles.Select(role => $"        CapabilityRole.{role}"))
        };
        var extras = new PluginScaffoldExtras
        {
            HardwareJson = string.Join(",\n    ", rules.Select(rule => JsonSerializer.Serialize(rule, RuleJson))),
            CapabilitiesJson = "["
                               + string.Join(", ", roles.Concat(ExampleRoles).Distinct().Select(role => $"\"{role}\""))
                               + "]",
            Templates =
                [new ScaffoldFromCaptureWorkflow.TemplateFile(ProfileTemplate.Template, ProfileTemplate.Output)],
            Tokens = tokens
        };
        var scaffold =
            ScaffoldFromCaptureWorkflow.Write(identity, outputDirectory, boundaries, extras, cancellationToken);
        List<string> maps = [];
        if (record.Motion?.Gyrometer is not null)
        {
            maps.Add("gyrometer");
        }

        if (record.Motion?.Accelerometer is not null)
        {
            maps.Add("accelerometer");
        }

        return new LabScaffoldResult
        {
            Scaffold = scaffold,
            Report = review.Report,
            RecordId = record.Id,
            Hardware = [.. rules.Select(LabReview.DescribeRule)],
            Buttons = buttons.Length,
            AxisMaps = maps,
            Capabilities = roles,
            Unresolved =
            [
                .. review.Items.Where(item => item.Verdict is LabReviewVerdict.Unresolved)
                    .Select(item => item.Field).Distinct(StringComparer.Ordinal)
            ],
            Disagreements = left
        };
    }

    // The template's exact detection: SMBIOS fields and one controller endpoint with its firmware release.
    private static PluginScaffoldIdentity ExactIdentity(
        MachineInventory inventory,
        DeviceKnowledgeRecord record,
        string? selection)
    {
        UsbInterfaceInventory[] endpoints =
        [
            .. inventory.UsbInterfaces
                .Where(usb => usb is { Present: true, VendorId.Length: 4, ProductId.Length: 4, DeviceRelease.Length: 4 }
                              && string.Equals(usb.DeviceClass, "HIDClass", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(usb => $"{usb.VendorId}:{usb.ProductId}:{usb.DeviceRelease}".ToUpperInvariant())
                .OrderBy(usb => usb.VendorId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(usb => usb.ProductId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(usb => usb.DeviceRelease, StringComparer.OrdinalIgnoreCase)
        ];
        UsbInterfaceInventory[] candidates;
        if (!string.IsNullOrWhiteSpace(selection))
        {
            var parts = selection.Split(':');
            candidates =
            [
                .. endpoints.Where(usb => parts.Length is 2 or 3
                                          && string.Equals(usb.VendorId, parts[0], StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(usb.ProductId, parts[1], StringComparison.OrdinalIgnoreCase)
                                          && (parts.Length == 2
                                              || string.Equals(usb.DeviceRelease, parts[2],
                                                  StringComparison.OrdinalIgnoreCase)))
            ];
        }
        else
        {
            var controllers = record.HidEndpoints.Where(endpoint => endpoint.Role == "controller").ToArray();
            UsbInterfaceInventory[] known =
            [
                .. endpoints.Where(usb => controllers.Any(endpoint =>
                    string.Equals(endpoint.VendorId, usb.VendorId, StringComparison.OrdinalIgnoreCase)
                    && (endpoint.ProductIds.Count == 0
                        || endpoint.ProductIds.Contains(usb.ProductId!, StringComparer.OrdinalIgnoreCase))))
            ];
            candidates = known.Length > 0 ? known : endpoints;
        }

        if (candidates.Length != 1)
        {
            var choices = string.Join(", ",
                endpoints.Take(16).Select(usb => $"{usb.VendorId}:{usb.ProductId}:{usb.DeviceRelease}"));
            throw new InvalidDataException(candidates.Length == 0
                ? $"No HID endpoint with an exact VID, PID and release matches. Endpoints: {(choices.Length == 0 ? "none" : choices)}."
                : $"The report has {candidates.Length} candidate controller endpoints. Select one with --usb-instance VID:PID[:release]: {choices}.");
        }

        var firmware = inventory.Firmware;
        return new PluginScaffoldIdentity
        {
            SystemManufacturer = LabReview.CleanIdentity(firmware.SystemManufacturer)
                                 ?? throw new InvalidDataException(
                                     "The report has no exact SMBIOS system manufacturer."),
            BaseboardProduct = LabReview.CleanIdentity(firmware.BaseboardProduct)
                               ?? throw new InvalidDataException("The report has no exact baseboard product."),
            SystemSku = LabReview.CleanIdentity(firmware.SystemSku)
                        ?? throw new InvalidDataException("The report has no exact SMBIOS system SKU."),
            BiosVersion = LabReview.CleanIdentity(firmware.BiosVersion)
                          ?? throw new InvalidDataException("The report has no exact BIOS version."),
            UsbVendorId = candidates[0].VendorId!,
            UsbProductId = candidates[0].ProductId!,
            UsbDeviceRelease = candidates[0].DeviceRelease!
        };
    }

    // Roles a plugin would publish for what the record and the evidence support.
    private static IReadOnlyList<CapabilityRole> Roles(DeviceKnowledgeRecord record, LabReviewResult review)
    {
        SortedSet<CapabilityRole> roles = [];
        foreach (var mechanism in record.Mechanisms.Where(mechanism => Confirmed(mechanism.Provenance)))
        {
            switch (mechanism.Feature)
            {
                case "tdp":
                    roles.Add(CapabilityRole.PowerSustainedLimit);
                    break;
                case "power-profile":
                    roles.Add(CapabilityRole.ScenarioMode);
                    break;
                case "fan":
                    roles.Add(CapabilityRole.FanMode);
                    if (mechanism.HasReadback)
                    {
                        roles.Add(CapabilityRole.FanMeasuredRpm);
                    }

                    break;
                case "charge-limit":
                    roles.Add(CapabilityRole.ChargeLimit);
                    break;
                case "lighting":
                    roles.Add(CapabilityRole.LightingZoneColor);
                    break;
                case "rumble":
                    roles.Add(CapabilityRole.HapticSink);
                    break;
            }
        }

        if (review.Items.Any(item => item.Area == "buttons" && item.Verdict is LabReviewVerdict.Confirmed
                                                                or LabReviewVerdict.New or LabReviewVerdict.Observed
                                                            && item.Observed != "not on this device"))
        {
            roles.Add(CapabilityRole.ControllerSource);
        }

        if ((record.Motion?.Gyrometer is not null || record.Motion?.Accelerometer is not null)
            && Confirmed(record.Motion?.Provenance))
        {
            roles.Add(CapabilityRole.MotionSource);
        }

        return [.. roles];
    }

    private static bool Confirmed(DeviceKnowledgeProvenance? provenance)
    {
        return provenance?.Source is DeviceKnowledgeSource.LabConfirmed;
    }

    private static string RuleCode(HardwareMatchRule rule)
    {
        List<string> fields = [];

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                fields.Add($"{name} = {Literal(value)}");
            }
        }

        Add(nameof(HardwareMatchRule.BaseboardManufacturer), rule.BaseboardManufacturer);
        Add(nameof(HardwareMatchRule.BaseboardProduct), rule.BaseboardProduct);
        Add(nameof(HardwareMatchRule.SystemModel), rule.SystemModel);
        Add(nameof(HardwareMatchRule.SystemSku), rule.SystemSku);
        Add(nameof(HardwareMatchRule.ProcessorName), rule.ProcessorName);
        Add(nameof(HardwareMatchRule.ProcessorNameContains), rule.ProcessorNameContains);
        Add(nameof(HardwareMatchRule.BaseboardVersion), rule.BaseboardVersion);
        return $"        new HardwareMatchRule {{ {string.Join(", ", fields)} }}";
    }

    private static string ButtonCode(DeviceButtonKnowledge button)
    {
        static string Number(int? value)
        {
            return value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "null";
        }

        var keys = button.PressKeys.Count == 0 ? "[]" : $"[{string.Join(", ", button.PressKeys.Select(Literal))}]";
        return "        new DeviceButton("
               + $"{Literal(button.Name)}, {(button.WizardButton is null ? "null" : Literal(button.WizardButton))}, "
               + $"DeviceButtonSource.{button.Source}, {keys}, {Number(button.ReportId)}, {Number(button.ByteOffset)}, "
               + $"{Number(button.Mask)}, {(button.MatchesValue ? "true" : "false")}, {Number(button.EventCode)}, "
               + $"{(Confirmed(button.Provenance) ? "true" : "false")})";
    }

    private static string MapCode(DeviceAxisMap? map, DeviceKnowledgeProvenance? provenance, string kind)
    {
        if (map is null)
        {
            return "null";
        }

        // The motion provenance names each map a promotion wrote, for example "Gyrometer map confirmed".
        var confirmed = Confirmed(provenance)
                        && provenance!.Note?.Contains(char.ToUpperInvariant(kind[0]) + kind[1..] + " map ",
                            StringComparison.Ordinal) == true;
        var swap = string.Join(", ", map.Swap.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"[{Literal(pair.Key)}] = {Literal(pair.Value)}"));
        var sign = string.Join(", ", map.Sign.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"[{Literal(pair.Key)}] = {pair.Value.ToString(CultureInfo.InvariantCulture)}"));
        return $"new DeviceAxisMap(\n        new Dictionary<string, string> {{ {swap} }},\n"
               + $"        new Dictionary<string, int> {{ {sign} }},\n        {(confirmed ? "true" : "false")})";
    }

    /// <summary>A C# string literal with every character outside printable ASCII escaped.</summary>
    /// <param name="value">Text.</param>
    /// <returns>The literal, quotes included.</returns>
    internal static string Literal(string value)
    {
        StringBuilder builder = new("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '{' or '}':
                    // Never let two braces meet: the template renderer treats a double brace as a token.
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    break;
                case >= ' ' and <= '~':
                    builder.Append(c);
                    break;
                default:
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
