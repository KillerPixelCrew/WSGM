using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Capture;

/// <summary>Closed outcomes for the shared observe-only capture workflow.</summary>
internal enum ObserveOnlyCaptureStatus
{
    /// <summary>The private session is complete and a sanitized export is ready for review.</summary>
    ReadyForExport,

    /// <summary>The operator or local-interactive safety gate refused the run.</summary>
    Refused,

    /// <summary>The imported recipe was absent, oversized, malformed, or outside the closed schema.</summary>
    InvalidRecipe,

    /// <summary>The explicit output path was unsafe.</summary>
    InvalidOutput,

    /// <summary>Inventory or passive observation could not complete.</summary>
    CaptureFailed,

    /// <summary>The private session could not be persisted.</summary>
    WriteFailed
}

/// <summary>Inputs for one explicitly approved observe-only capture preparation.</summary>
internal sealed record ObserveOnlyCaptureRequest
{
    /// <summary>Imported inert recipe JSON.</summary>
    public required string RecipePath { get; init; }

    /// <summary>Explicit safe directory receiving physically separated private and export children.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>SHA-256 of the exact recipe bytes the operator reviewed.</summary>
    public required string ReviewedRecipeSha256 { get; init; }

    /// <summary>Whether the caller is a local interactive operator session.</summary>
    public required bool IsLocalInteractive { get; init; }

    /// <summary>Whether the operator reviewed and approved the observation scope.</summary>
    public required bool ObservationScopeConfirmed { get; init; }
}

/// <summary>Bounded inert recipe detail shown before an operator approves observation.</summary>
internal sealed record ObserveOnlyRecipeReview
{
    /// <summary>SHA-256 that binds later approval to these exact bytes.</summary>
    public required string RecipeSha256 { get; init; }

    /// <summary>Stable recipe identifier.</summary>
    public required string RecipeId { get; init; }

    /// <summary>Operator-facing recipe name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Closed observation steps and their prompts.</summary>
    public IReadOnlyList<ObservationStep> Steps { get; init; } = [];
}

/// <summary>A prepared capture and its not-yet-written sanitized export.</summary>
internal sealed record CaptureExportPlan
{
    /// <summary>Private working directory already persisted locally.</summary>
    public required string PrivateWorkingDirectory { get; init; }

    /// <summary>Proposed new shareable bundle path.</summary>
    public required string ShareableOutputPath { get; init; }

    /// <summary>Sanitized bundle retained until the operator accepts its preview.</summary>
    public required SanitizedCaptureBundle Bundle { get; init; }

    /// <summary>Observation prompts retained for operator review.</summary>
    public IReadOnlyList<string> Prompts { get; init; } = [];

    /// <summary>Honest platform limits attached to the prepared capture.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Result of preparing one capture without automatically exporting it.</summary>
internal sealed record ObserveOnlyCaptureResult
{
    /// <summary>Closed workflow outcome.</summary>
    public required ObserveOnlyCaptureStatus Status { get; init; }

    /// <summary>Prepared export when capture succeeded.</summary>
    public CaptureExportPlan? ExportPlan { get; init; }

    /// <summary>Bounded operator-facing failure detail.</summary>
    public string? Error { get; init; }
}

/// <summary>Result of the separate sanitized-export approval step.</summary>
internal sealed record CaptureExportResult
{
    /// <summary>Whether a new shareable bundle was written.</summary>
    public required bool Exported { get; init; }

    /// <summary>Absolute path of the completed bundle.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Reason export was refused or failed.</summary>
    public string? Error { get; init; }
}

/// <summary>
///     Prepares a private observe-only session and exports its sanitized projection only after a second
///     explicit approval.
/// </summary>
/// <remarks>
///     Recipe data selects only closed observation kinds. The only live source registered here records
///     the inventory snapshot that this workflow collected itself; every other source is represented as
///     unavailable until a separately reviewed local observer is compiled into Device Lab. Imported
///     recipe data therefore cannot open a device or authorize a write.
/// </remarks>
internal static class ObserveOnlyCaptureWorkflow
{
    private const int MaximumRecipeBytes = 2 * 1024 * 1024;

    private static readonly (string Namespace, string ClassName)[] KnownInventoryClasses =
    [
        ("root\\WMI", "MSI_ACPI"),
        ("root\\WMI", "MSI_Event"),
        ("root\\WMI", "BatteryStatus"),
        ("root\\WMI", "MSAcpi_ThermalZoneTemperature")
    ];

    internal static IReadOnlyList<CaptureStreamFile> RedactStreams(
        IReadOnlyList<CaptureStreamFile> streams,
        CaptureRedactor redactor)
    {
        HashSet<string> sourceIds = new(StringComparer.Ordinal);
        List<CaptureStreamFile> shareable = [];
        foreach (var stream in streams)
        {
            var sourceId = redactor.Redact(stream.SourceId);
            if (!sourceIds.Add(sourceId))
            {
                throw new InvalidDataException(
                    "Redaction produced duplicate capture source identifiers; the capture cannot be exported.");
            }

            shareable.Add(new CaptureStreamFile
            {
                SourceId = sourceId,
                Events =
                [
                    .. stream.Events.Select(captureEvent => captureEvent with
                    {
                        SourceId = redactor.Redact(captureEvent.SourceId),
                        RecipeStepId = redactor.Redact(captureEvent.RecipeStepId)
                    })
                ]
            });
        }

        return shareable;
    }

    /// <summary>Reads and validates one inert recipe for operator scope review.</summary>
    /// <param name="recipePath">Imported recipe JSON.</param>
    /// <param name="cancellationToken">Cancels bounded recipe validation.</param>
    /// <returns>Closed steps plus a hash that expires approval if the file changes.</returns>
    public static ObserveOnlyRecipeReview Review(
        string recipePath,
        CancellationToken cancellationToken = default)
    {
        var (recipe, hash) = ReadRecipe(recipePath, cancellationToken);
        return new ObserveOnlyRecipeReview
        {
            RecipeSha256 = hash,
            RecipeId = recipe.RecipeId,
            DisplayName = recipe.DisplayName,
            Steps = recipe.Steps
        };
    }

    /// <summary>Prepares and persists a private capture, leaving shareable output unwritten.</summary>
    /// <param name="request">Explicit recipe, path, and operator gates.</param>
    /// <param name="capturedAt">Session timestamp.</param>
    /// <param name="repositoryRoot">Detected repository root, when running from a checkout.</param>
    /// <param name="cancellationToken">Whole-session cancellation.</param>
    /// <returns>A privacy-preview plan or a closed failure.</returns>
    public static async Task<ObserveOnlyCaptureResult> PrepareAsync(
        ObserveOnlyCaptureRequest request,
        DateTimeOffset capturedAt,
        string? repositoryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsLocalInteractive || !request.ObservationScopeConfirmed)
        {
            return Failure(
                ObserveOnlyCaptureStatus.Refused,
                "A local interactive operator must review and approve the observation scope.");
        }

        var boundaries = DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot);
        var rootDecision = DeviceLabOutputPathPolicy.Evaluate(
            request.OutputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!rootDecision.IsAllowed || rootDecision.FullPath is null)
        {
            return Failure(ObserveOnlyCaptureStatus.InvalidOutput, rootDecision.Reason);
        }

        ObserveOnlyRecipe recipe;
        try
        {
            (recipe, var hash) = ReadRecipe(request.RecipePath, cancellationToken);
            if (!string.Equals(hash, request.ReviewedRecipeSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    ObserveOnlyCaptureStatus.Refused,
                    "The recipe changed after scope review; review the exact current bytes again.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or JsonException)
        {
            return Failure(ObserveOnlyCaptureStatus.InvalidRecipe, exception.Message);
        }

        var captureId = $"capture-{capturedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var privateDirectory = Path.Combine(rootDecision.FullPath, "private", captureId);
        var shareablePath = Path.Combine(rootDecision.FullPath, "shareable", $"{captureId}.wsgmcap");
        var privateDecision = DeviceLabOutputPathPolicy.Evaluate(
            privateDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        var exportDecision = DeviceLabOutputPathPolicy.Evaluate(
            shareablePath,
            DeviceLabOutputTargetKind.NewFile,
            boundaries);
        if (!privateDecision.IsAllowed || !exportDecision.IsAllowed)
        {
            return Failure(
                ObserveOnlyCaptureStatus.InvalidOutput,
                privateDecision.Reason ?? exportDecision.Reason);
        }

        MachineInventory privateInventory;
        PassiveCaptureTimeline timeline = new(new QpcCaptureReceiptClock());
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            privateInventory = WindowsInventoryCollector.Collect(
                capturedAt,
                KnownInventoryClasses,
                cancellationToken);
            IPassiveCaptureSource[] sources =
            [
                .. recipe.Steps
                    .Select(step => step.SourceId)
                    .Distinct(StringComparer.Ordinal)
                    .Select(sourceId => new ClosedObserveOnlyCaptureSource(sourceId))
            ];
            PassiveCaptureCoordinator coordinator = new(sources, timeline);
            await coordinator.RunAsync(recipe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            return Failure(ObserveOnlyCaptureStatus.CaptureFailed, exception.GetType().Name);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var completedAt = DateTimeOffset.UtcNow;
        var events = timeline.SnapshotByReceipt();
        IReadOnlyList<CaptureStreamFile> streams =
        [
            .. events
                .GroupBy(captureEvent => captureEvent.SourceId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new CaptureStreamFile
                {
                    SourceId = group.Key,
                    Events = [.. group.OrderBy(captureEvent => captureEvent.SourceSequence)]
                })
        ];
        CaptureRedactor recipeRedactor = new();
        var shareableInventory = InventoryRedaction.ToShareable(privateInventory, recipeRedactor);
        var shareableRecipe = recipe with
        {
            RecipeId = recipeRedactor.Redact(recipe.RecipeId),
            DisplayName = recipeRedactor.Redact(recipe.DisplayName),
            Steps =
            [
                .. recipe.Steps.Select(step => step with
                {
                    StepId = recipeRedactor.Redact(step.StepId),
                    SourceId = recipeRedactor.Redact(step.SourceId),
                    OperatorPrompt = step.OperatorPrompt is null
                        ? null
                        : recipeRedactor.Redact(step.OperatorPrompt)
                })
            ]
        };
        IReadOnlyList<CaptureStreamFile> shareableStreams;
        try
        {
            shareableStreams = RedactStreams(streams, recipeRedactor);
        }
        catch (InvalidDataException exception)
        {
            return Failure(ObserveOnlyCaptureStatus.CaptureFailed, exception.Message);
        }

        var replacements = recipeRedactor.Summarize();
        CaptureRedactionManifest redaction = new()
        {
            SchemaVersion = CaptureSchema.CurrentVersion,
            DefaultRedactionApplied = true,
            Replacements = replacements,
            Quarantined = []
        };
        var bundle = CreateShareableBundle(
            captureId,
            capturedAt,
            completedAt,
            timeline.QpcFrequency,
            shareableRecipe,
            shareableInventory,
            shareableStreams,
            redaction);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PersistPrivateSession(
                privateDirectory,
                captureId,
                capturedAt,
                completedAt,
                timeline.QpcFrequency,
                recipe,
                privateInventory,
                streams,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or ArgumentException)
        {
            return Failure(ObserveOnlyCaptureStatus.WriteFailed, exception.GetType().Name);
        }

        return new ObserveOnlyCaptureResult
        {
            Status = ObserveOnlyCaptureStatus.ReadyForExport,
            ExportPlan = new CaptureExportPlan
            {
                PrivateWorkingDirectory = privateDirectory,
                ShareableOutputPath = shareablePath,
                Bundle = bundle,
                Prompts =
                [
                    .. recipe.Steps
                        .Where(step => !string.IsNullOrWhiteSpace(step.OperatorPrompt))
                        .Select(step => step.OperatorPrompt!)
                ],
                Limitations = PassiveCaptureLimitations.All
            }
        };
    }

    /// <summary>Writes the sanitized bundle only after the operator accepts its redaction preview.</summary>
    /// <param name="plan">Prepared in-memory export.</param>
    /// <param name="exportPreviewConfirmed">Whether the redaction preview was explicitly accepted.</param>
    /// <param name="repositoryRoot">Detected repository root, when running from a checkout.</param>
    /// <param name="cancellationToken">Cancels bundle generation before atomic publication.</param>
    /// <returns>Export result; refusal is a value and never writes a partial target.</returns>
    public static CaptureExportResult Export(
        CaptureExportPlan plan,
        bool exportPreviewConfirmed,
        string? repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        return Export(plan, exportPreviewConfirmed, repositoryRoot, File.Move, cancellationToken);
    }

    // The publisher commits a closed, flushed temporary file with create-new semantics.
    internal static CaptureExportResult Export(
        CaptureExportPlan plan,
        bool exportPreviewConfirmed,
        string? repositoryRoot,
        Action<string, string> publishFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(publishFile);
        cancellationToken.ThrowIfCancellationRequested();
        if (!exportPreviewConfirmed)
        {
            return new CaptureExportResult
            {
                Exported = false,
                Error = "The redaction and quarantine preview must be accepted before export."
            };
        }

        var boundaries = DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot);
        var decision = DeviceLabOutputPathPolicy.Evaluate(
            plan.ShareableOutputPath,
            DeviceLabOutputTargetKind.NewFile,
            boundaries);
        if (!decision.IsAllowed || decision.FullPath is null)
        {
            return new CaptureExportResult { Exported = false, Error = decision.Reason };
        }

        var directory = Path.GetDirectoryName(decision.FullPath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(decision.FullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            DurableFile.WriteNew(
                temporaryPath,
                output => CaptureBundleWriter.Write(output, plan.Bundle, cancellationToken),
                64 * 1024,
                FileAccess.ReadWrite);

            cancellationToken.ThrowIfCancellationRequested();
            publishFile(temporaryPath, decision.FullPath);
            return new CaptureExportResult { Exported = true, OutputPath = decision.FullPath };
        }
        catch (OperationCanceledException)
        {
            var cleanupError = TryDelete(temporaryPath);
            if (cleanupError is not null)
            {
                return new CaptureExportResult { Exported = false, Error = $"Export cancelled. {cleanupError}" };
            }

            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or ArgumentException or InvalidDataException)
        {
            var cleanupError = TryDelete(temporaryPath);
            return new CaptureExportResult
            {
                Exported = false,
                Error = cleanupError is null ? exception.Message : $"{exception.Message} {cleanupError}"
            };
        }
    }

    private static (ObserveOnlyRecipe Recipe, string Sha256) ReadRecipe(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumRecipeBytes)
        {
            throw new InvalidDataException("Recipe is absent, empty, or oversized.");
        }

        var bytes = new byte[(int)file.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = file.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Recipe ended before its inspected length.");
            }

            offset += read;
        }

        var recipe = JsonSerializer.Deserialize(
                         bytes,
                         DeviceLabJsonContext.Default.ObserveOnlyRecipe) ??
                     throw new InvalidDataException("Recipe could not be decoded.");
        var errors = CaptureSchemaValidator.Validate(recipe);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                errors.Select(error => $"{error.Path}: {error.Message}")));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (recipe, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static SanitizedCaptureBundle CreateShareableBundle(
        string captureId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        long qpcFrequency,
        ObserveOnlyRecipe recipe,
        MachineInventory inventory,
        IReadOnlyList<CaptureStreamFile> streams,
        CaptureRedactionManifest redaction)
    {
        CaptureStreamDescriptor[] descriptors =
        [
            .. streams.Select((stream, index) => new CaptureStreamDescriptor
            {
                SourceId = stream.SourceId,
                Path = $"streams/{index:D3}-{DeviceLabPaths.SafeName(stream.SourceId, allowDot: false)}.ndjson",
                EventCount = stream.Events.Count
            })
        ];
        return new SanitizedCaptureBundle
        {
            Manifest = new ShareableCaptureManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                BundleId = $"shareable-{captureId}",
                ToolVersion = typeof(ObserveOnlyCaptureWorkflow).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                QpcFrequency = qpcFrequency,
                Streams = descriptors,
                Analysis = [],
                Blobs = []
            },
            Recipe = recipe,
            Inventory = inventory,
            Streams = streams,
            Analysis = [],
            Blobs = [],
            Redaction = redaction
        };
    }

    private static void PersistPrivateSession(
        string directory,
        string captureId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        long qpcFrequency,
        ObserveOnlyRecipe recipe,
        MachineInventory inventory,
        IReadOnlyList<CaptureStreamFile> streams,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var streamsDirectory = Path.Combine(directory, "streams");
        Directory.CreateDirectory(streamsDirectory);
        CaptureStreamDescriptor[] descriptors =
        [
            .. streams.Select((stream, index) => new CaptureStreamDescriptor
            {
                SourceId = stream.SourceId,
                Path = $"streams/{index:D3}-{DeviceLabPaths.SafeName(stream.SourceId, allowDot: false)}.ndjson",
                EventCount = stream.Events.Count
            })
        ];
        PrivateCaptureManifest manifest = new()
        {
            SchemaVersion = CaptureSchema.CurrentVersion,
            CaptureId = captureId,
            ToolVersion = typeof(ObserveOnlyCaptureWorkflow).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            StartedAt = startedAt,
            CompletedAt = completedAt,
            QpcFrequency = qpcFrequency,
            RecipePath = CaptureBundleLayout.RecipePath,
            InventoryPath = CaptureBundleLayout.InventoryPath,
            Streams = descriptors,
            Analysis = [],
            Blobs = []
        };

        WriteNew(Path.Combine(directory, CaptureBundleLayout.RecipePath), DeviceLabJson.Serialize(recipe));
        cancellationToken.ThrowIfCancellationRequested();
        WriteNew(Path.Combine(directory, CaptureBundleLayout.InventoryPath), DeviceLabJson.Serialize(inventory));
        for (var index = 0; index < streams.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = streams[index];
            WriteNewNdjson(
                Path.Combine(
                    directory,
                    descriptors[index].Path.Replace('/', Path.DirectorySeparatorChar)),
                stream.Events,
                cancellationToken);
        }

        // Completion is published last. A process death can leave reviewable raw files, but never a
        // manifest that falsely labels a partial private session complete.
        cancellationToken.ThrowIfCancellationRequested();
        WriteNew(Path.Combine(directory, "private-manifest.json"), JsonSerializer.Serialize(
            manifest,
            DeviceLabJsonContext.Default.PrivateCaptureManifest));
    }

    private static void WriteNew(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        DurableFile.WriteNewText(path, content + Environment.NewLine);
    }

    private static void WriteNewNdjson(
        string path,
        IReadOnlyList<CaptureStreamEvent> events,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        DurableFile.WriteNew(path, stream =>
        {
            foreach (var captureEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var json = JsonSerializer.SerializeToUtf8Bytes(
                    captureEvent,
                    DeviceLabCompactJson.CaptureStreamEvent);
                stream.Write(json);
                stream.WriteByte((byte)'\n');
            }
        });
    }

    private static string? TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Temporary export cleanup failed for '{path}': {exception.Message}";
        }
    }

    private static ObserveOnlyCaptureResult Failure(ObserveOnlyCaptureStatus status, string? error)
    {
        return new ObserveOnlyCaptureResult
        {
            Status = status,
            Error = error ?? "The observe-only capture workflow could not complete."
        };
    }

    private sealed class ClosedObserveOnlyCaptureSource(string sourceId) : IPassiveCaptureSource
    {
        private long _sequence;

        public string SourceId { get; } = sourceId;

        public Task ObserveAsync(
            ObservationStep step,
            Func<PassiveObservation, ValueTask> emit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = Interlocked.Increment(ref _sequence);
            if (step.Kind is not ObservationStepKind.InventorySnapshot)
            {
                return emit(new PassiveObservation
                {
                    SourceId = SourceId,
                    RecipeStepId = step.StepId,
                    SourceSequence = sequence,
                    DeviceGeneration = 0,
                    Payload = new CapturedPayload { Length = 0, Disposition = PayloadDisposition.NotCaptured },
                    Access = EventAccessState.Unavailable
                }).AsTask();
            }

            var payload = "inventory-snapshot-recorded"u8.ToArray();
            return emit(new PassiveObservation
            {
                SourceId = SourceId,
                RecipeStepId = step.StepId,
                SourceSequence = sequence,
                DeviceGeneration = 0,
                Payload = new CapturedPayload
                {
                    Length = payload.Length,
                    Disposition = PayloadDisposition.Included,
                    Bytes = payload,
                    Sha256 = CaptureHashFile.Hash(payload)
                }
            }).AsTask();
        }
    }
}
