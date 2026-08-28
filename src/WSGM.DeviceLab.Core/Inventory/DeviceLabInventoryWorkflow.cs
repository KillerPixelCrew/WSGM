using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Inventory;

/// <summary>Closed outcomes of the shared inventory workflow.</summary>
public enum DeviceLabInventoryStatus
{
    /// <summary>The inventory was collected and written.</summary>
    Success,

    /// <summary>The explicit output directory or target file was refused.</summary>
    InvalidOutput,

    /// <summary>Unexpected machine enumeration failure prevented collection.</summary>
    CollectionFailed,

    /// <summary>The safe output target could not be created or completed.</summary>
    WriteFailed,
}

/// <summary>Inputs shared by CLI and GUI inventory surfaces.</summary>
public sealed record DeviceLabInventoryRequest
{
    /// <summary>Explicit directory that will receive <c>inventory.json</c>.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Whether unique identifiers are replaced with session-local tokens.</summary>
    public bool Shareable { get; init; }
}

/// <summary>Result of one shared Device Lab inventory workflow.</summary>
public sealed record DeviceLabInventoryResult
{
    /// <summary>Closed workflow outcome.</summary>
    public required DeviceLabInventoryStatus Status { get; init; }

    /// <summary>Collected private or sanitized inventory when collection succeeded.</summary>
    public MachineInventory? Inventory { get; init; }

    /// <summary>Canonical JSON written to disk and suitable for stdout.</summary>
    public string? Json { get; init; }

    /// <summary>Absolute path of the completed new file.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Sanitization summary for a shareable inventory.</summary>
    public IReadOnlyList<RedactionSummary> Redactions { get; init; } = [];

    /// <summary>Bounded operator-facing failure detail.</summary>
    public string? Error { get; init; }
}

/// <summary>Collects and atomically persists read-only machine inventory.</summary>
public static class DeviceLabInventoryWorkflow
{
    /// <summary>Canonical inventory filename inside the explicit output directory.</summary>
    public const string InventoryFileName = "inventory.json";

    private static readonly (string Namespace, string ClassName)[] ProbedWmiClasses =
    [
        ("root\\WMI", "MSI_ACPI"),
        ("root\\WMI", "MSI_Event"),
        ("root\\WMI", "BatteryStatus"),
        ("root\\WMI", "MSAcpi_ThermalZoneTemperature"),
    ];

    /// <summary>Runs inventory collection and creates one new canonical artifact.</summary>
    /// <param name="request">Explicit output and privacy request.</param>
    /// <param name="capturedAt">Timestamp to record.</param>
    /// <param name="repositoryRoot">Detected repository root, when running from a checkout.</param>
    /// <returns>A value result; expected filesystem and enumeration failures do not escape.</returns>
    public static DeviceLabInventoryResult Run(
        DeviceLabInventoryRequest request,
        DateTimeOffset capturedAt,
        string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        DeviceLabPathBoundaries boundaries = DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot);
        DeviceLabOutputPathDecision directoryDecision = DeviceLabOutputPathPolicy.Evaluate(
            request.OutputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!directoryDecision.IsAllowed || directoryDecision.FullPath is null)
        {
            return Failure(DeviceLabInventoryStatus.InvalidOutput, directoryDecision.Reason);
        }

        string outputPath = Path.Combine(directoryDecision.FullPath, InventoryFileName);
        DeviceLabOutputPathDecision initialFileDecision = DeviceLabOutputPathPolicy.Evaluate(
            outputPath,
            DeviceLabOutputTargetKind.NewFile,
            boundaries);
        if (!initialFileDecision.IsAllowed)
        {
            return Failure(DeviceLabInventoryStatus.InvalidOutput, initialFileDecision.Reason);
        }

        MachineInventory inventory;
        try
        {
            inventory = WindowsInventoryCollector.Collect(capturedAt, ProbedWmiClasses);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(DeviceLabInventoryStatus.CollectionFailed, exception.GetType().Name);
        }

        IReadOnlyList<RedactionSummary> redactions = [];
        if (request.Shareable)
        {
            inventory = InventoryRedaction.ToShareable(inventory, out redactions);
        }

        string json = DeviceLabJson.Serialize(inventory);
        string tempPath = Path.Combine(
            directoryDecision.FullPath,
            $".{InventoryFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directoryDecision.FullPath);

            DeviceLabOutputPathDecision recheckedDirectory = DeviceLabOutputPathPolicy.Evaluate(
                directoryDecision.FullPath,
                DeviceLabOutputTargetKind.Directory,
                boundaries);
            DeviceLabOutputPathDecision fileDecision = DeviceLabOutputPathPolicy.Evaluate(
                outputPath,
                DeviceLabOutputTargetKind.NewFile,
                boundaries);
            if (!recheckedDirectory.IsAllowed || !fileDecision.IsAllowed)
            {
                return Failure(
                    DeviceLabInventoryStatus.InvalidOutput,
                    recheckedDirectory.Reason ?? fileDecision.Reason);
            }

            using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            using (StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, outputPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            string? cleanupError = TryDeleteTemporaryFile(tempPath);
            string detail = cleanupError is null
                ? exception.GetType().Name
                : $"{exception.GetType().Name}; temporary cleanup failed: {cleanupError}";
            return Failure(DeviceLabInventoryStatus.WriteFailed, detail);
        }

        return new DeviceLabInventoryResult
        {
            Status = DeviceLabInventoryStatus.Success,
            Inventory = inventory,
            Json = json,
            OutputPath = outputPath,
            Redactions = redactions,
        };
    }

    private static string? TryDeleteTemporaryFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.GetType().Name;
        }
    }

    private static DeviceLabInventoryResult Failure(DeviceLabInventoryStatus status, string? error) =>
        new()
        {
            Status = status,
            Error = error ?? "The inventory workflow could not complete.",
        };
}
