using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using WSGM.Device.Sdk.Windows;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One HID top-level collection, read without sending any report.</summary>
internal sealed record LabHidCollection
{
    /// <summary>Device interface path.</summary>
    public required string Path { get; init; }

    /// <summary>Device instance ID.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Vendor ID, as hex.</summary>
    public string? VendorId { get; init; }

    /// <summary>Product ID, as hex.</summary>
    public string? ProductId { get; init; }

    /// <summary>Version number, as hex.</summary>
    public string? Version { get; init; }

    /// <summary>Product string.</summary>
    public string? Product { get; init; }

    /// <summary>Manufacturer string.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Top-level usage page, as hex.</summary>
    public string? UsagePage { get; init; }

    /// <summary>Top-level usage, as hex.</summary>
    public string? Usage { get; init; }

    /// <summary>Input report length in bytes, including the report ID.</summary>
    public int? InputReportLength { get; init; }

    /// <summary>Output report length in bytes, including the report ID.</summary>
    public int? OutputReportLength { get; init; }

    /// <summary>Feature report length in bytes, including the report ID.</summary>
    public int? FeatureReportLength { get; init; }

    /// <summary>Number of link collection nodes.</summary>
    public int? LinkCollections { get; init; }

    /// <summary>Input button capabilities.</summary>
    public IReadOnlyList<LabHidCaps> InputButtons { get; init; } = [];

    /// <summary>Input value capabilities.</summary>
    public IReadOnlyList<LabHidCaps> InputValues { get; init; } = [];

    /// <summary>Output button capabilities.</summary>
    public IReadOnlyList<LabHidCaps> OutputButtons { get; init; } = [];

    /// <summary>Output value capabilities.</summary>
    public IReadOnlyList<LabHidCaps> OutputValues { get; init; } = [];

    /// <summary>Feature button capabilities.</summary>
    public IReadOnlyList<LabHidCaps> FeatureButtons { get; init; } = [];

    /// <summary>Feature value capabilities.</summary>
    public IReadOnlyList<LabHidCaps> FeatureValues { get; init; } = [];

    /// <summary>What could not be read.</summary>
    public string? Problem { get; init; }
}

/// <summary>One HIDP button or value capability.</summary>
internal sealed record LabHidCaps
{
    /// <summary>Report ID.</summary>
    public required int ReportId { get; init; }

    /// <summary>Usage page, as hex.</summary>
    public required string UsagePage { get; init; }

    /// <summary>First usage, or the only one.</summary>
    public required string UsageMin { get; init; }

    /// <summary>Last usage; the same as the first when not a range.</summary>
    public required string UsageMax { get; init; }

    /// <summary>Link collection index.</summary>
    public int LinkCollection { get; init; }

    /// <summary>Whether the value is absolute rather than relative.</summary>
    public bool IsAbsolute { get; init; }

    /// <summary>Bit size of one value; 1 for buttons.</summary>
    public int BitSize { get; init; }

    /// <summary>Number of values in the report.</summary>
    public int ReportCount { get; init; }

    /// <summary>Logical minimum; values only.</summary>
    public int? LogicalMin { get; init; }

    /// <summary>Logical maximum; values only.</summary>
    public int? LogicalMax { get; init; }

    /// <summary>Physical minimum; values only.</summary>
    public int? PhysicalMin { get; init; }

    /// <summary>Physical maximum; values only.</summary>
    public int? PhysicalMax { get; init; }

    /// <summary>Unit code, as hex; values only.</summary>
    public string? Units { get; init; }

    /// <summary>Unit exponent; values only.</summary>
    public uint? UnitsExponent { get; init; }

    /// <summary>Whether the value has a null state; values only.</summary>
    public bool? HasNull { get; init; }
}

internal static partial class LabSystemDump
{
    private static LabSystemDumpSectionResult CollectHid(LabSystemDumpContext context)
    {
        List<string> issues = [];
        List<LabHidCollection> collections = [];
        foreach (var collection in HidDevices.EnumerateAll((path, problem) =>
                 {
                     AddIssue(issues, $"{path}: {problem}");
                     collections.Add(new LabHidCollection { Path = path, Problem = problem });
                 }))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var read = ReadHidCollection(collection);
            if (read.Problem is { } problem)
            {
                AddIssue(issues, $"{collection.DevicePath}: {problem}");
            }

            collections.Add(read);
        }

        context.Write("hid", new { collections.Count, Collections = collections });
        return Result("hid", collections.Count, Plural(collections.Count, "collection", "collections"), issues);
    }

    // Inspect opens the collection with no access: it reads strings and descriptors, never a report.
    private static LabHidCollection ReadHidCollection(HidCollection collection)
    {
        LabHidCollection read = new()
        {
            Path = collection.DevicePath,
            InstanceId = collection.InstancePath.Length == 0 ? null : collection.InstancePath,
            VendorId = Hex(collection.VendorId, 4),
            ProductId = Hex(collection.ProductId, 4),
            Version = Hex(collection.ReleaseNumber, 4),
            UsagePage = Hex(collection.UsagePage, 4),
            Usage = Hex(collection.Usage, 4),
            InputReportLength = collection.InputLength,
            OutputReportLength = collection.OutputLength,
            FeatureReportLength = collection.FeatureLength
        };

        HidCollectionDetails details;
        try
        {
            details = HidDevices.Inspect(collection);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return read with { Problem = ex.Message };
        }

        var capabilities = details.Capabilities;
        return read with
        {
            Product = details.Product,
            Manufacturer = details.Manufacturer,
            LinkCollections = details.LinkCollections,
            InputButtons = Caps(capabilities, HidReportType.Input, true),
            InputValues = Caps(capabilities, HidReportType.Input, false),
            OutputButtons = Caps(capabilities, HidReportType.Output, true),
            OutputValues = Caps(capabilities, HidReportType.Output, false),
            FeatureButtons = Caps(capabilities, HidReportType.Feature, true),
            FeatureValues = Caps(capabilities, HidReportType.Feature, false)
        };
    }

    private static IReadOnlyList<LabHidCaps> Caps(IReadOnlyList<HidCapability> capabilities,
        HidReportType reportType, bool buttons)
    {
        return
        [
            .. capabilities.Where(item => item.ReportType == reportType && item.IsButton == buttons)
                .Select(ToLabCaps)
        ];
    }

    /// <summary>The dump's form of one capability: hex usages, and the value-only fields left out for a button.</summary>
    /// <param name="capability">The capability.</param>
    /// <returns>The dump entry.</returns>
    public static LabHidCaps ToLabCaps(HidCapability capability)
    {
        LabHidCaps caps = new()
        {
            ReportId = capability.ReportId,
            UsagePage = Hex(capability.UsagePage, 4),
            UsageMin = Hex(capability.UsageMin, 4),
            UsageMax = Hex(capability.UsageMax, 4),
            LinkCollection = capability.LinkCollection,
            IsAbsolute = capability.IsAbsolute,
            BitSize = capability.BitSize,
            ReportCount = capability.ReportCount
        };
        return capability.IsButton
            ? caps
            : caps with
            {
                HasNull = capability.HasNull,
                UnitsExponent = capability.UnitsExponent,
                Units = Hex(capability.Units),
                LogicalMin = capability.LogicalMin,
                LogicalMax = capability.LogicalMax,
                PhysicalMin = capability.PhysicalMin,
                PhysicalMax = capability.PhysicalMax
            };
    }
}
