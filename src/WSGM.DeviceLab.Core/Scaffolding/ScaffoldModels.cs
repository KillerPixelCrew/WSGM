using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Ipc;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Evidence;

namespace WSGM.DeviceLab.Core.Scaffolding;

/// <summary>Version and ownership markers for deterministic plugin scaffolds.</summary>
public static class ScaffoldSchema
{
    /// <summary>Current scaffold input and output schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Marker carried by files wholly owned by regeneration.</summary>
    public const string GeneratedMarker = "wsgm-generated:v1";

    /// <summary>Marker carried by starter files that become developer-owned after generation.</summary>
    public const string HandwrittenTemplateMarker = "wsgm-handwritten-template:v1";

    /// <summary>Maximum number of files described by one scaffold output.</summary>
    public const int MaximumFiles = 8192;
}

/// <summary>Frozen inputs used by a deterministic scaffold generation.</summary>
public sealed record ScaffoldInputManifest
{
    /// <summary>Schema version of this input.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Exact device definition being created.</summary>
    public required string DeviceDefinitionId { get; init; }

    /// <summary>SHA-256 of the sanitized source capture.</summary>
    public required string SourceCaptureSha256 { get; init; }

    /// <summary>Generator identity and version.</summary>
    public required string GeneratorVersion { get; init; }

    /// <summary>Runtime API range and version negotiated for generated code.</summary>
    public required ScaffoldRuntimeApi RuntimeApi { get; init; }

    /// <summary>Exact implementation modules selected for composition.</summary>
    public IReadOnlyList<PinnedModule> ModuleLocks { get; init; } = [];

    /// <summary>Evidence lock that pins the claims behind generated constants.</summary>
    public required ScaffoldEvidenceLockReference EvidenceLock { get; init; }

    /// <summary>Plain fixture IDs included in the generated project.</summary>
    public IReadOnlyList<string> FixtureIds { get; init; } = [];
}

/// <summary>Runtime contract selected during scaffold generation.</summary>
public sealed record ScaffoldRuntimeApi
{
    /// <summary>Oldest protocol version the generated plugin accepts.</summary>
    public required ushort MinimumVersion { get; init; }

    /// <summary>Newest protocol version the generated plugin accepts.</summary>
    public required ushort MaximumVersion { get; init; }

    /// <summary>Version selected for generated fixtures and host-adapter code.</summary>
    public required ushort NegotiatedVersion { get; init; }

    /// <summary>Schema fingerprint compiled into generated code.</summary>
    public required string SchemaFingerprint { get; init; }
}

/// <summary>Location and content pin for a generated project's evidence lock.</summary>
public sealed record ScaffoldEvidenceLockReference
{
    /// <summary>Relative output path of the lock.</summary>
    public string Path { get; init; } = "evidence.lock.json";

    /// <summary>Lowercase hexadecimal SHA-256 digest of the canonical lock.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>Manifest describing the exact files emitted by scaffold generation.</summary>
public sealed record ScaffoldOutputManifest
{
    /// <summary>Schema version of this output.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>SHA-256 of the canonical <see cref="ScaffoldInputManifest"/>.</summary>
    public required string InputSha256 { get; init; }

    /// <summary>Generator identity and version.</summary>
    public required string GeneratorVersion { get; init; }

    /// <summary>Negotiated runtime API emitted into the project.</summary>
    public required ScaffoldRuntimeApi RuntimeApi { get; init; }

    /// <summary>Generation status; scaffolding never grants support or package trust.</summary>
    public ScaffoldStatus Status { get; init; } = ScaffoldStatus.Scaffolded;

    /// <summary>Every generated or starter file and its ownership boundary.</summary>
    public IReadOnlyList<ScaffoldOutputFile> Files { get; init; } = [];
}

/// <summary>The only support status a generator may assign.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScaffoldStatus>))]
public enum ScaffoldStatus
{
    /// <summary>Generated for developer review; not supported or trusted.</summary>
    Scaffolded,
}

/// <summary>One emitted scaffold file and who may overwrite it on regeneration.</summary>
public sealed record ScaffoldOutputFile
{
    /// <summary>Canonical project-relative path.</summary>
    public required string Path { get; init; }

    /// <summary>Whether regeneration or the developer owns subsequent edits.</summary>
    public required ScaffoldFileOwnership Ownership { get; init; }

    /// <summary>Machine-readable ownership marker embedded in or beside the file.</summary>
    public required string OwnershipMarker { get; init; }

    /// <summary>Lowercase hexadecimal SHA-256 digest of initial content.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>Ownership boundary for one scaffold output file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScaffoldFileOwnership>))]
public enum ScaffoldFileOwnership
{
    /// <summary>Regeneration may replace this file after showing a semantic diff.</summary>
    Generated,

    /// <summary>Starter content becomes developer-owned and is never overwritten.</summary>
    HandwrittenTemplate,
}

/// <summary>Validates scaffold input and output contracts without creating files.</summary>
public static class ScaffoldSchemaValidator
{
    /// <summary>Validates frozen scaffold input.</summary>
    /// <param name="input">Input to validate.</param>
    /// <returns>Every validation failure found.</returns>
    public static IReadOnlyList<CaptureValidationError> Validate(ScaffoldInputManifest input)
    {
        ArgumentNullException.ThrowIfNull(input);

        List<CaptureValidationError> errors = [];
        ValidateVersion(input.SchemaVersion, "scaffoldInput.schemaVersion", errors);
        ValidateIdentifier(input.DeviceDefinitionId, "scaffoldInput.deviceDefinitionId", errors);
        ValidateIdentifier(input.GeneratorVersion, "scaffoldInput.generatorVersion", errors);
        ValidateSha256(input.SourceCaptureSha256, "scaffoldInput.sourceCaptureSha256", errors);
        ValidateRuntime(input.RuntimeApi, errors);

        if (!string.Equals(input.EvidenceLock.Path, "evidence.lock.json", StringComparison.Ordinal))
        {
            errors.Add(new("scaffoldInput.evidenceLock.path",
                "Evidence lock path must be 'evidence.lock.json'."));
        }

        ValidateSha256(input.EvidenceLock.Sha256, "scaffoldInput.evidenceLock.sha256", errors);

        HashSet<string> moduleIds = new(StringComparer.Ordinal);
        foreach (PinnedModule module in input.ModuleLocks)
        {
            ValidateIdentifier(module.ModuleId, "scaffoldInput.moduleLocks", errors);
            if (module.Version <= 0)
            {
                errors.Add(new(module.ModuleId, "Pinned module version must be positive."));
            }

            if (!moduleIds.Add(module.ModuleId))
            {
                errors.Add(new(module.ModuleId, "Pinned module ID is duplicated."));
            }
        }

        HashSet<string> fixtureIds = new(StringComparer.Ordinal);
        foreach (string fixtureId in input.FixtureIds)
        {
            ValidateIdentifier(fixtureId, "scaffoldInput.fixtureIds", errors);
            if (!fixtureIds.Add(fixtureId))
            {
                errors.Add(new(fixtureId, "Fixture ID is duplicated."));
            }
        }

        return errors;
    }

    /// <summary>Validates a scaffold output manifest and its file-ownership markers.</summary>
    /// <param name="output">Output to validate.</param>
    /// <returns>Every validation failure found.</returns>
    public static IReadOnlyList<CaptureValidationError> Validate(ScaffoldOutputManifest output)
    {
        ArgumentNullException.ThrowIfNull(output);

        List<CaptureValidationError> errors = [];
        ValidateVersion(output.SchemaVersion, "scaffoldOutput.schemaVersion", errors);
        ValidateIdentifier(output.GeneratorVersion, "scaffoldOutput.generatorVersion", errors);
        ValidateSha256(output.InputSha256, "scaffoldOutput.inputSha256", errors);
        ValidateRuntime(output.RuntimeApi, errors);

        if (output.Status is not ScaffoldStatus.Scaffolded)
        {
            errors.Add(new("scaffoldOutput.status", "Generation may assign only Scaffolded status."));
        }

        if (output.Files.Count > ScaffoldSchema.MaximumFiles)
        {
            errors.Add(new("scaffoldOutput.files",
                $"A scaffold may describe at most {ScaffoldSchema.MaximumFiles} files."));
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ScaffoldOutputFile file in output.Files)
        {
            if (!CaptureBundleLayout.IsSafeRelativePath(file.Path))
            {
                errors.Add(new(file.Path, "Scaffold file path is not canonical and relative."));
            }

            if (!paths.Add(file.Path))
            {
                errors.Add(new(file.Path, "Scaffold file path is duplicated."));
            }

            string expectedMarker = file.Ownership switch
            {
                ScaffoldFileOwnership.Generated => ScaffoldSchema.GeneratedMarker,
                ScaffoldFileOwnership.HandwrittenTemplate => ScaffoldSchema.HandwrittenTemplateMarker,
                _ => string.Empty,
            };

            if (!string.Equals(file.OwnershipMarker, expectedMarker, StringComparison.Ordinal))
            {
                errors.Add(new(file.Path, "File ownership marker does not match its ownership."));
            }

            if (file.Path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                && file.Ownership is not ScaffoldFileOwnership.Generated)
            {
                errors.Add(new(file.Path, "A .g.cs file must be generator-owned."));
            }

            ValidateSha256(file.Sha256, file.Path, errors);
        }

        return errors;
    }

    private static void ValidateRuntime(
        ScaffoldRuntimeApi runtime,
        ICollection<CaptureValidationError> errors)
    {
        if (runtime.MinimumVersion > runtime.MaximumVersion
            || runtime.NegotiatedVersion < runtime.MinimumVersion
            || runtime.NegotiatedVersion > runtime.MaximumVersion)
        {
            errors.Add(new("runtimeApi", "Negotiated version must be inside a non-inverted range."));
        }

        if (runtime.NegotiatedVersion < DeviceProtocol.MinSupportedVersion
            || runtime.NegotiatedVersion > DeviceProtocol.MaxSupportedVersion)
        {
            errors.Add(new("runtimeApi.negotiatedVersion",
                "Negotiated version is outside this generator's supported runtime window."));
        }

        if (!string.Equals(
                runtime.SchemaFingerprint,
                DeviceProtocol.SchemaFingerprint,
                StringComparison.Ordinal))
        {
            errors.Add(new("runtimeApi.schemaFingerprint", "Runtime schema fingerprint does not match."));
        }
    }

    private static void ValidateVersion(
        int version,
        string path,
        ICollection<CaptureValidationError> errors)
    {
        if (version != ScaffoldSchema.CurrentVersion)
        {
            errors.Add(new(path, "Unsupported scaffold schema version."));
        }
    }

    private static void ValidateIdentifier(
        string? value,
        string path,
        ICollection<CaptureValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > CaptureSchema.MaximumIdentifierLength)
        {
            errors.Add(new(path,
                $"Identifier must contain 1 to {CaptureSchema.MaximumIdentifierLength} characters."));
        }
    }

    private static void ValidateSha256(
        string hash,
        string path,
        ICollection<CaptureValidationError> errors)
    {
        if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            errors.Add(new(path, "SHA-256 must be 64 lowercase hexadecimal characters."));
        }
    }
}
