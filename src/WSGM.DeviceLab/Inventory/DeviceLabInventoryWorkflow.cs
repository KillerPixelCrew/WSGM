using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Inventory;

/// <summary>Closed outcomes of the shared inventory workflow.</summary>
internal enum DeviceLabInventoryStatus
{
    /// <summary>The inventory was collected and written.</summary>
    Success,

    /// <summary>The explicit output directory or target file was refused.</summary>
    InvalidOutput,

    /// <summary>Unexpected machine enumeration failure prevented collection.</summary>
    CollectionFailed,

    /// <summary>The safe output target could not be created or completed.</summary>
    WriteFailed
}

/// <summary>Inputs shared by CLI and GUI inventory surfaces.</summary>
internal sealed record DeviceLabInventoryRequest
{
    /// <summary>Explicit directory that will receive <c>inventory.json</c>.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Whether unique identifiers are replaced with session-local tokens.</summary>
    public bool Shareable { get; init; }
}

/// <summary>Result of one shared Device Lab inventory workflow.</summary>
internal sealed record DeviceLabInventoryResult
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
internal static class DeviceLabInventoryWorkflow
{
    /// <summary>Canonical inventory filename inside the explicit output directory.</summary>
    private const string InventoryFileName = "inventory.json";

    internal static readonly (string Namespace, string ClassName)[] ProbedWmiClasses =
    [
        ("root\\WMI", "MSI_ACPI"),
        ("root\\WMI", "MSI_Event"),
        ("root\\WMI", "BatteryStatus"),
        ("root\\WMI", "MSAcpi_ThermalZoneTemperature")
    ];

    /// <summary>Runs inventory collection and creates one new canonical artifact.</summary>
    /// <param name="request">Explicit output and privacy request.</param>
    /// <param name="capturedAt">Timestamp to record.</param>
    /// <param name="repositoryRoot">Detected repository root, when running from a checkout.</param>
    /// <param name="cancellationToken">Cancels collection or atomic publication.</param>
    /// <returns>A value result; expected filesystem and enumeration failures do not escape.</returns>
    public static DeviceLabInventoryResult Run(
        DeviceLabInventoryRequest request,
        DateTimeOffset capturedAt,
        string? repositoryRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var boundaries = DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot);
        var directoryDecision = DeviceLabOutputPathPolicy.Evaluate(
            request.OutputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!directoryDecision.IsAllowed || directoryDecision.FullPath is null)
        {
            return Failure(DeviceLabInventoryStatus.InvalidOutput, directoryDecision.Reason);
        }

        var outputPath = Path.Combine(directoryDecision.FullPath, InventoryFileName);
        var initialFileDecision = DeviceLabOutputPathPolicy.Evaluate(
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
            inventory = WindowsInventoryCollector.Collect(
                capturedAt,
                ProbedWmiClasses,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                              and not OperationCanceledException)
        {
            return Failure(DeviceLabInventoryStatus.CollectionFailed, exception.GetType().Name);
        }

        IReadOnlyList<RedactionSummary> redactions = [];
        if (request.Shareable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inventory = InventoryRedaction.ToShareable(inventory, out redactions);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var json = DeviceLabJson.Serialize(inventory);
        var tempPath = DurableFile.StagingPath(outputPath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directoryDecision.FullPath);

            var recheckedDirectory = DeviceLabOutputPathPolicy.Evaluate(
                directoryDecision.FullPath,
                DeviceLabOutputTargetKind.Directory,
                boundaries);
            var fileDecision = DeviceLabOutputPathPolicy.Evaluate(
                outputPath,
                DeviceLabOutputTargetKind.NewFile,
                boundaries);
            if (!recheckedDirectory.IsAllowed || !fileDecision.IsAllowed)
            {
                return Failure(
                    DeviceLabInventoryStatus.InvalidOutput,
                    recheckedDirectory.Reason ?? fileDecision.Reason);
            }

            DurableFile.WriteNewText(tempPath, json);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, outputPath);
        }
        catch (OperationCanceledException)
        {
            var cleanupFailure = CleanupCancelledWrite(tempPath);
            if (cleanupFailure is not null)
            {
                return cleanupFailure;
            }

            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or ArgumentException)
        {
            var cleanupError = DurableFile.TryDeleteFile(tempPath);
            var detail = cleanupError is null
                ? exception.GetType().Name
                : $"{exception.GetType().Name}; temporary cleanup failed: {cleanupError.GetType().Name}";
            return Failure(DeviceLabInventoryStatus.WriteFailed, detail);
        }

        return new DeviceLabInventoryResult
        {
            Status = DeviceLabInventoryStatus.Success,
            Inventory = inventory,
            Json = json,
            OutputPath = outputPath,
            Redactions = redactions
        };
    }

    internal static DeviceLabInventoryResult? CleanupCancelledWrite(string tempPath)
    {
        var cleanupError = DurableFile.TryDeleteFile(tempPath);
        return cleanupError is null
            ? null
            : Failure(DeviceLabInventoryStatus.WriteFailed,
                $"Cancelled; temporary cleanup failed for {tempPath}: {cleanupError.GetType().Name}");
    }

    private static DeviceLabInventoryResult Failure(DeviceLabInventoryStatus status, string? error)
    {
        return new DeviceLabInventoryResult
        {
            Status = status,
            Error = error ?? "The inventory workflow could not complete."
        };
    }
}
