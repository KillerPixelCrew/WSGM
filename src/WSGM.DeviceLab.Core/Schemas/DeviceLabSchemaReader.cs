using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Fixtures;
using WSGM.DeviceLab.Core.Scaffolding;

namespace WSGM.DeviceLab.Core.Schemas;

/// <summary>Closed failure states returned while reading an imported Device Lab schema.</summary>
public enum DeviceLabSchemaReadFailure
{
    /// <summary>The artifact was read, migrated when necessary, and validated.</summary>
    None,

    /// <summary>The input contained no bytes.</summary>
    Empty,

    /// <summary>The input exceeded the hard JSON artifact limit.</summary>
    Oversized,

    /// <summary>The input could not be read from its stream.</summary>
    Unreadable,

    /// <summary>The bytes were not one well-formed JSON object of bounded depth.</summary>
    Malformed,

    /// <summary>The declared schema version is not supported by this reader.</summary>
    UnsupportedVersion,

    /// <summary>A known legacy schema could not be migrated without ambiguity.</summary>
    MigrationFailed,

    /// <summary>The typed artifact violated its current semantic schema.</summary>
    Invalid,
}

/// <summary>Result of a bounded, typed Device Lab schema read.</summary>
/// <typeparam name="T">Typed schema artifact.</typeparam>
public sealed record DeviceLabSchemaReadResult<T>
    where T : class
{
    /// <summary>Typed artifact when the read succeeded; otherwise <see langword="null"/>.</summary>
    public T? Value { get; init; }

    /// <summary>Closed reason the artifact was rejected.</summary>
    public required DeviceLabSchemaReadFailure Failure { get; init; }

    /// <summary>Version declared by the imported artifact, when it could be read.</summary>
    public int? SourceVersion { get; init; }

    /// <summary>Whether a supported legacy representation was upgraded in memory.</summary>
    public bool Migrated { get; init; }

    /// <summary>Semantic validation errors for an otherwise well-formed current artifact.</summary>
    public IReadOnlyList<CaptureValidationError> ValidationErrors { get; init; } = [];

    /// <summary>Bounded diagnostic detail that never includes imported document contents.</summary>
    public string? Detail { get; init; }

    /// <summary>Whether a validated typed value is available.</summary>
    public bool Succeeded => Failure is DeviceLabSchemaReadFailure.None && Value is not null;
}

/// <summary>
/// Reads versioned Device Lab JSON through hard size and depth bounds before exposing typed values.
/// </summary>
/// <remarks>
/// Draft version-zero migrations only rename metadata fields and restore closed safety markers. They
/// never turn imported recipes into executable authority or fixtures into hardware-capable inputs.
/// </remarks>
public static class DeviceLabSchemaReader
{
    /// <summary>Largest standalone Device Lab JSON artifact accepted by the reader.</summary>
    public const int MaximumJsonBytes = 4 * 1024 * 1024;

    /// <summary>Largest JSON nesting depth accepted before typed deserialization.</summary>
    public const int MaximumJsonDepth = 64;

    private const int LegacyDraftVersion = 0;

    /// <summary>Reads and validates a shareable capture manifest.</summary>
    /// <param name="input">JSON stream positioned at the start of the manifest.</param>
    /// <param name="cancellationToken">Cancellation token for stream I/O.</param>
    /// <returns>A validated manifest or a closed rejection reason.</returns>
    public static Task<DeviceLabSchemaReadResult<ShareableCaptureManifest>> ReadShareableManifestAsync(
        Stream input,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            input,
            DeviceLabJsonContext.Default.ShareableCaptureManifest,
            CaptureSchema.ShareableManifestVersion,
            NoLegacyMigration,
            CaptureSchemaValidator.Validate,
            cancellationToken);

    /// <summary>Reads, safely migrates, and validates an inert observe-only recipe.</summary>
    /// <param name="input">JSON stream positioned at the start of the recipe.</param>
    /// <param name="cancellationToken">Cancellation token for stream I/O.</param>
    /// <returns>A validated inert recipe or a closed rejection reason.</returns>
    public static Task<DeviceLabSchemaReadResult<ObserveOnlyRecipe>> ReadRecipeAsync(
        Stream input,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            input,
            DeviceLabJsonContext.Default.ObserveOnlyRecipe,
            CaptureSchema.RecipeVersion,
            MigrateRecipe,
            CaptureSchemaValidator.Validate,
            cancellationToken);

    /// <summary>Reads, safely migrates, and validates a simulator-only fixture manifest.</summary>
    /// <param name="input">JSON stream positioned at the start of the manifest.</param>
    /// <param name="cancellationToken">Cancellation token for stream I/O.</param>
    /// <returns>A validated fixture or a closed rejection reason.</returns>
    public static Task<DeviceLabSchemaReadResult<FixtureManifest>> ReadFixtureAsync(
        Stream input,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            input,
            DeviceLabJsonContext.Default.FixtureManifest,
            FixtureSchema.CurrentVersion,
            MigrateFixture,
            FixtureSchemaValidator.Validate,
            cancellationToken);

    /// <summary>Reads and validates frozen scaffold input.</summary>
    /// <param name="input">JSON stream positioned at the start of the manifest.</param>
    /// <param name="cancellationToken">Cancellation token for stream I/O.</param>
    /// <returns>Validated scaffold input or a closed rejection reason.</returns>
    public static Task<DeviceLabSchemaReadResult<ScaffoldInputManifest>> ReadScaffoldInputAsync(
        Stream input,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            input,
            DeviceLabJsonContext.Default.ScaffoldInputManifest,
            ScaffoldSchema.CurrentVersion,
            NoLegacyMigration,
            ScaffoldSchemaValidator.Validate,
            cancellationToken);

    /// <summary>Reads and validates a scaffold output manifest.</summary>
    /// <param name="input">JSON stream positioned at the start of the manifest.</param>
    /// <param name="cancellationToken">Cancellation token for stream I/O.</param>
    /// <returns>A validated scaffold output or a closed rejection reason.</returns>
    public static Task<DeviceLabSchemaReadResult<ScaffoldOutputManifest>> ReadScaffoldOutputAsync(
        Stream input,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            input,
            DeviceLabJsonContext.Default.ScaffoldOutputManifest,
            ScaffoldSchema.CurrentVersion,
            NoLegacyMigration,
            ScaffoldSchemaValidator.Validate,
            cancellationToken);

    private static async Task<DeviceLabSchemaReadResult<T>> ReadAsync<T>(
        Stream input,
        JsonTypeInfo<T> typeInfo,
        int currentVersion,
        SchemaMigration migration,
        Func<T, IReadOnlyList<CaptureValidationError>> validate,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(input);

        BoundedReadResult bytes = await ReadBoundedAsync(input, cancellationToken).ConfigureAwait(false);
        if (bytes.Failure is not DeviceLabSchemaReadFailure.None)
        {
            return Failure<T>(bytes.Failure, detail: bytes.Detail);
        }

        JsonObject root;
        try
        {
            JsonNode? node = JsonNode.Parse(
                bytes.Bytes,
                nodeOptions: null,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            if (node is not JsonObject objectRoot)
            {
                return Failure<T>(
                    DeviceLabSchemaReadFailure.Malformed,
                    detail: "The JSON root must be an object.");
            }

            root = objectRoot;
        }
        catch (JsonException exception)
        {
            return Failure<T>(
                DeviceLabSchemaReadFailure.Malformed,
                detail: JsonDiagnostic(exception));
        }

        if (!TryReadVersion(root, out int sourceVersion))
        {
            return Failure<T>(
                DeviceLabSchemaReadFailure.Malformed,
                detail: "schemaVersion must be a JSON integer.");
        }

        bool migrated = false;
        if (sourceVersion != currentVersion)
        {
            SchemaMigrationResult migrationResult = migration(root, sourceVersion, currentVersion);
            if (migrationResult.Status is SchemaMigrationStatus.Unsupported)
            {
                return Failure<T>(
                    DeviceLabSchemaReadFailure.UnsupportedVersion,
                    sourceVersion,
                    migrationResult.Detail);
            }

            if (migrationResult.Status is SchemaMigrationStatus.Failed)
            {
                return Failure<T>(
                    DeviceLabSchemaReadFailure.MigrationFailed,
                    sourceVersion,
                    migrationResult.Detail);
            }

            migrated = true;
        }

        T? value;
        try
        {
            value = root.Deserialize(typeInfo);
        }
        catch (JsonException exception)
        {
            return Failure<T>(
                DeviceLabSchemaReadFailure.Malformed,
                sourceVersion,
                JsonDiagnostic(exception));
        }

        if (value is null)
        {
            return Failure<T>(
                DeviceLabSchemaReadFailure.Malformed,
                sourceVersion,
                "The JSON object did not produce a typed value.");
        }

        IReadOnlyList<CaptureValidationError> validationErrors = validate(value);
        if (validationErrors.Count > 0)
        {
            return new DeviceLabSchemaReadResult<T>
            {
                Failure = DeviceLabSchemaReadFailure.Invalid,
                SourceVersion = sourceVersion,
                Migrated = migrated,
                ValidationErrors = validationErrors,
            };
        }

        return new DeviceLabSchemaReadResult<T>
        {
            Value = value,
            Failure = DeviceLabSchemaReadFailure.None,
            SourceVersion = sourceVersion,
            Migrated = migrated,
        };
    }

    private static async Task<BoundedReadResult> ReadBoundedAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        using MemoryStream output = new(capacity: Math.Min(MaximumJsonBytes, 64 * 1024));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                int remaining = MaximumJsonBytes + 1 - checked((int)output.Length);
                int read = await input.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                if (output.Length > MaximumJsonBytes)
                {
                    return new(
                        Bytes: [],
                        Failure: DeviceLabSchemaReadFailure.Oversized,
                        Detail: $"JSON artifacts may contain at most {MaximumJsonBytes} bytes.");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return new(
                Bytes: [],
                Failure: DeviceLabSchemaReadFailure.Unreadable,
                Detail: exception.GetType().Name);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (output.Length == 0)
        {
            return new(Bytes: [], Failure: DeviceLabSchemaReadFailure.Empty, Detail: null);
        }

        return new(
            Bytes: output.ToArray(),
            Failure: DeviceLabSchemaReadFailure.None,
            Detail: null);
    }

    private static bool TryReadVersion(JsonObject root, out int version)
    {
        version = default;
        return root["schemaVersion"] is JsonValue value && value.TryGetValue(out version);
    }

    private static SchemaMigrationResult MigrateRecipe(
        JsonObject root,
        int sourceVersion,
        int currentVersion)
    {
        if (sourceVersion != LegacyDraftVersion || currentVersion != CaptureSchema.RecipeVersion)
        {
            return SchemaMigrationResult.UnsupportedVersion(sourceVersion);
        }

        if (!RenameLegacyProperty(root, "name", "displayName", out string? detail))
        {
            return SchemaMigrationResult.FailedMigration(detail);
        }

        root["authority"] = nameof(RecipeAuthority.InertEvidence);
        root["schemaVersion"] = currentVersion;
        return SchemaMigrationResult.Migrated;
    }

    private static SchemaMigrationResult MigrateFixture(
        JsonObject root,
        int sourceVersion,
        int currentVersion)
    {
        if (sourceVersion != LegacyDraftVersion || currentVersion != FixtureSchema.CurrentVersion)
        {
            return SchemaMigrationResult.UnsupportedVersion(sourceVersion);
        }

        if (!RenameLegacyProperty(root, "id", "fixtureId", out string? detail))
        {
            return SchemaMigrationResult.FailedMigration(detail);
        }

        root["replayPolicy"] = nameof(FixtureReplayPolicy.SimulatorOnly);
        root["schemaVersion"] = currentVersion;
        return SchemaMigrationResult.Migrated;
    }

    private static SchemaMigrationResult NoLegacyMigration(
        JsonObject root,
        int sourceVersion,
        int currentVersion)
    {
        _ = root;
        _ = currentVersion;
        return SchemaMigrationResult.UnsupportedVersion(sourceVersion);
    }

    private static bool RenameLegacyProperty(
        JsonObject root,
        string legacyName,
        string currentName,
        out string? detail)
    {
        bool hasLegacy = root.TryGetPropertyValue(legacyName, out JsonNode? legacyValue);
        bool hasCurrent = root.ContainsKey(currentName);
        if (hasLegacy && hasCurrent)
        {
            detail = $"Legacy property '{legacyName}' conflicts with '{currentName}'.";
            return false;
        }

        if (!hasLegacy)
        {
            detail = $"Legacy property '{legacyName}' is missing.";
            return false;
        }

        root.Remove(legacyName);
        root[currentName] = legacyValue;
        detail = null;
        return true;
    }

    private static DeviceLabSchemaReadResult<T> Failure<T>(
        DeviceLabSchemaReadFailure failure,
        int? sourceVersion = null,
        string? detail = null)
        where T : class => new()
        {
            Failure = failure,
            SourceVersion = sourceVersion,
            Detail = detail,
        };

    private static string JsonDiagnostic(JsonException exception) =>
        $"Invalid JSON at line {exception.LineNumber ?? 0}, byte {exception.BytePositionInLine ?? 0}.";

    private delegate SchemaMigrationResult SchemaMigration(
        JsonObject root,
        int sourceVersion,
        int currentVersion);

    private enum SchemaMigrationStatus
    {
        Migrated,
        Unsupported,
        Failed,
    }

    private readonly record struct SchemaMigrationResult(
        SchemaMigrationStatus Status,
        string? Detail)
    {
        public static SchemaMigrationResult Migrated { get; } = new(
            SchemaMigrationStatus.Migrated,
            Detail: null);

        public static SchemaMigrationResult UnsupportedVersion(int version) => new(
            SchemaMigrationStatus.Unsupported,
            $"Schema version {version} is not supported.");

        public static SchemaMigrationResult FailedMigration(string? detail) => new(
            SchemaMigrationStatus.Failed,
            detail ?? "The legacy artifact could not be migrated safely.");
    }

    private readonly record struct BoundedReadResult(
        byte[] Bytes,
        DeviceLabSchemaReadFailure Failure,
        string? Detail);
}
