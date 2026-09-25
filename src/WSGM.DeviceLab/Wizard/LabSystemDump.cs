using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace WSGM.DeviceLab.Wizard;

/// <summary>How one system dump section ended.</summary>
internal enum LabSystemDumpSectionStatus
{
    /// <summary>Everything the section looks for was read.</summary>
    Completed,

    /// <summary>The section wrote its file, but some items could not be read.</summary>
    Partial,

    /// <summary>The section could not run on this machine or without administrator rights.</summary>
    Skipped,

    /// <summary>The section failed and wrote nothing useful.</summary>
    Failed
}

/// <summary>What one system dump section recorded.</summary>
internal sealed record LabSystemDumpSectionResult
{
    /// <summary>Section ID, also the evidence file name.</summary>
    public required string Id { get; init; }

    /// <summary>How the section ended.</summary>
    public required LabSystemDumpSectionStatus Status { get; init; }

    /// <summary>Number of items recorded, for example tables or devices.</summary>
    public int Count { get; init; }

    /// <summary>Short plain-English result shown to the tester.</summary>
    public required string Summary { get; init; }

    /// <summary>Items that could not be read.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}

/// <summary>One section of the system dump.</summary>
/// <param name="Id">Section ID and evidence file name.</param>
/// <param name="Title">Title shown to the tester.</param>
/// <param name="Collect">Reads the section and writes its evidence; runs off the UI thread.</param>
internal sealed record LabSystemDumpSection(
    string Id,
    string Title,
    Func<LabSystemDumpContext, LabSystemDumpSectionResult> Collect);

/// <summary>Shared state for one system dump attempt.</summary>
/// <param name="Project">Project the attempt belongs to.</param>
/// <param name="Attempt">Attempt directory evidence is written into.</param>
/// <param name="Elevated">Whether the process has an administrator token.</param>
/// <param name="Cancellation">Cancelled when the wizard closes.</param>
internal sealed record LabSystemDumpContext(
    LabProject Project,
    string Attempt,
    bool Elevated,
    CancellationToken Cancellation)
{
    /// <summary>The device tree, once that section ran; later sections reuse it.</summary>
    public IReadOnlyList<LabDumpDevice> Devices { get; set; } = [];

    /// <summary>
    ///     Whether the preflight's device owner reservation is held, so no WSGM device integration can
    ///     touch the embedded controller or processor registers while they are read.
    /// </summary>
    public bool OwnerReserved { get; init; }

    /// <summary>The <c>root\wmi</c> classes, once that section ran; later sections reuse them.</summary>
    public IReadOnlyList<LabWmiClass> WmiClasses { get; set; } = [];

    /// <summary>Writes one JSON evidence file into the attempt.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="name">File name without extension.</param>
    /// <param name="value">Value to write.</param>
    public void Write<T>(string name, T value)
    {
        Project.WriteEvidence(Attempt, name, value);
    }
}

/// <summary>
///     The read-only system dump: ACPI tables, SMBIOS, the device tree, HID collections, serial ports,
///     WMI, sensors, the EC registers, display, battery and power, CPU, memory and CPU power limits.
///     Nothing on the machine is changed.
/// </summary>
/// <remarks>
///     Every section is optional. A section that throws is recorded as failed and the next one still
///     runs, so one broken provider never costs the tester the rest of the dump. Serial numbers, UUIDs,
///     asset tags, licence tables and MAC addresses are never collected.
/// </remarks>
internal static partial class LabSystemDump
{
    /// <summary>Sections in the order they run.</summary>
    public static IReadOnlyList<LabSystemDumpSection> Sections { get; } =
    [
        new("acpi", "ACPI tables", CollectAcpi),
        new("smbios", "Firmware tables (SMBIOS)", CollectSmbios),
        new("device-tree", "Devices", CollectDeviceTree),
        new("hid", "HID devices", CollectHid),
        new("serial-ports", "Serial ports", CollectSerialPorts),
        new("wmi", "WMI classes", CollectWmi),
        new("inventory", "Device summary", CollectInventory),
        new("sensors", "Sensors", CollectSensors),
        new("ec", "Embedded controller", CollectEc),
        new("display", "Display", CollectDisplay),
        new("battery-power", "Battery and power", CollectBatteryPower),
        new("cpu", "Processor and memory", CollectCpu),
        new("cpu-power", "Processor power limits", CollectCpuPower)
    ];

    /// <summary>Runs one section, turning any failure into a failed result.</summary>
    /// <param name="section">Section to run.</param>
    /// <param name="context">Attempt context.</param>
    /// <returns>The section result; never throws except for cancellation.</returns>
    public static LabSystemDumpSectionResult Run(LabSystemDumpSection section, LabSystemDumpContext context)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        try
        {
            return section.Collect(context);
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new LabSystemDumpSectionResult
            {
                Id = section.Id,
                Status = LabSystemDumpSectionStatus.Failed,
                Summary = "could not be read",
                Issues = [$"{ex.GetType().Name}: {ex.Message}"]
            };
        }
    }

    /// <summary>The one-line stage summary, for example "38 ACPI tables, 412 devices".</summary>
    /// <param name="results">Section results.</param>
    /// <returns>Summary text.</returns>
    public static string Summarize(IReadOnlyList<LabSystemDumpSectionResult> results)
    {
        List<string> parts = [];
        AddCount("acpi", "ACPI table", "ACPI tables");
        AddCount("device-tree", "device", "devices");
        AddCount("hid", "HID collection", "HID collections");
        AddCount("sensors", "sensor", "sensors");
        var failed = results.Count(result => result.Status is LabSystemDumpSectionStatus.Failed);
        if (failed > 0)
        {
            parts.Add(failed == 1 ? "1 part could not be read" : $"{failed} parts could not be read");
        }

        return parts.Count == 0 ? "Nothing could be read." : string.Join(", ", parts);

        void AddCount(string id, string one, string many)
        {
            if (results.FirstOrDefault(result => result.Id == id) is
                { Status: not LabSystemDumpSectionStatus.Failed } found)
            {
                parts.Add(
                    found.Count == 1 ? $"1 {one}" : $"{found.Count.ToString(CultureInfo.InvariantCulture)} {many}");
            }
        }
    }

    /// <summary>The <c>system-dump.json</c> index of every section.</summary>
    /// <param name="results">Section results.</param>
    /// <param name="elevated">Whether the dump ran with administrator rights.</param>
    /// <returns>Serializable report.</returns>
    public static object Report(IReadOnlyList<LabSystemDumpSectionResult> results, bool elevated)
    {
        return new
        {
            SchemaVersion = 1,
            Elevated = elevated,
            Summary = Summarize(results),
            Sections = results,
            Issues = results.SelectMany(result => result.Issues.Select(issue => $"{result.Id}: {issue}")).ToList()
        };
    }

    // Builds a result from a section's item count and issue list.
    private static LabSystemDumpSectionResult Result(
        string id,
        int count,
        string summary,
        IReadOnlyList<string> issues)
    {
        return new LabSystemDumpSectionResult
        {
            Id = id,
            Status = issues.Count == 0 ? LabSystemDumpSectionStatus.Completed : LabSystemDumpSectionStatus.Partial,
            Count = count,
            Summary = summary,
            Issues = issues
        };
    }

    // Keeps the issue list bounded when a section fails the same way for hundreds of items.
    private static void AddIssue(List<string> issues, string issue)
    {
        const int maximumIssues = 50;
        if (issues.Count < maximumIssues)
        {
            issues.Add(issue);
        }
        else if (issues.Count == maximumIssues)
        {
            issues.Add("More problems were not listed.");
        }
    }

    private static string Plural(int count, string one, string many)
    {
        return count == 1 ? $"1 {one}" : $"{count.ToString(CultureInfo.InvariantCulture)} {many}";
    }

    private static string Hex(uint value, int digits = 8)
    {
        return "0x" + value.ToString("X" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
