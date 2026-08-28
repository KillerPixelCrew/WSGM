using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WSGM.Device.Contracts.Glyphs;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Packaging;

/// <summary>One profile included in a deterministic glyph-generation result.</summary>
public sealed record GeneratedGlyphProfileSummary
{
    /// <summary>Stable package-scoped profile identifier.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Package-authored profile revision.</summary>
    public required int Revision { get; init; }

    /// <summary>Reviewed source identity.</summary>
    public required string SourceId { get; init; }

    /// <summary>Exact immutable source revision.</summary>
    public required string SourceRevision { get; init; }

    /// <summary>Hash-pinned license and attribution notices validated during import.</summary>
    public IReadOnlyList<string> NoticeHashes { get; init; } = [];
}

/// <summary>Deterministic glyph import/generation result that grants no package authority.</summary>
public sealed record GlyphPackageGenerationReport
{
    /// <summary>Whether every profile, source asset, and notice passed import.</summary>
    public required bool Valid { get; init; }

    /// <summary>Stable package-validation issues.</summary>
    public IReadOnlyList<PluginPackageValidationIssue> Issues { get; init; } = [];

    /// <summary>Profiles and exact source revisions used by this generation.</summary>
    public IReadOnlyList<GeneratedGlyphProfileSummary> Profiles { get; init; } = [];

    /// <summary>Generated WSGM-owned paths and their exact SHA-256 values.</summary>
    public IReadOnlyDictionary<string, string> GeneratedFileHashes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Glyph generation never grants package trust.</summary>
    public bool GrantsTrust => false;

    /// <summary>Glyph generation never grants privilege.</summary>
    public bool GrantsPrivilege => false;

    /// <summary>Glyph generation never grants hardware verification.</summary>
    public bool GrantsHardwareVerification => false;

    /// <summary>Glyph generation never grants retail support.</summary>
    public bool GrantsRetailSupport => false;
}

/// <summary>Explicit offline import that emits only deterministic WSGM-owned safe glyph assets.</summary>
public static class GlyphPackageGenerationWorkflow
{
    /// <summary>Validates package glyph sources and writes their current generated outputs.</summary>
    /// <param name="sourceDirectory">Existing plugin package source.</param>
    /// <param name="outputDirectory">New explicit directory receiving a package-layout mirror.</param>
    /// <param name="boundaries">Filesystem safety boundaries.</param>
    /// <returns>Generation report and exact output hashes.</returns>
    public static GlyphPackageGenerationReport Generate(
        string sourceDirectory,
        string outputDirectory,
        DeviceLabPathBoundaries boundaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentNullException.ThrowIfNull(boundaries);
        List<PluginPackageValidationIssue> issues = [];
        string root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root) || PluginPackageWorkflow.IsLink(root))
        {
            return Failure("invalid-root", "", "Package root is absent or is a reparse point.");
        }

        _ = PluginPackageWorkflow.EnumeratePackageFiles(root, issues);
        if (issues.Count > 0)
        {
            return Report([], issues, EmptyHashes());
        }

        ImmutableGlyphPackageDirectorySource source;
        try
        {
            source = new ImmutableGlyphPackageDirectorySource(root);
        }
        catch (InvalidDataException exception)
        {
            return Failure("invalid-root", "", exception.Message);
        }

        if (!source.TryRead(
            PluginPackageWorkflow.ManifestPath,
            ManifestLimits.MaxDocumentBytes,
            out byte[] manifestBytes))
        {
            return Failure("missing-manifest", PluginPackageWorkflow.ManifestPath,
                "Package manifest is absent or oversized.");
        }

        PluginManifestReadResult read = PluginManifestReader.Read(manifestBytes);
        if (!read.IsValid || read.Manifest is null)
        {
            return Report(
                [],
                read.Errors.Select(error => new PluginPackageValidationIssue(
                    error.Code.ToString(),
                    error.Path,
                    error.Message)).ToArray(),
                EmptyHashes());
        }

        GlyphPackageImportResult imported = GlyphPackageImporter.Import(
            read.Manifest,
            source,
            GlyphGeneratedAssetPolicy.Generate);
        if (!imported.IsValid)
        {
            return Report(
                [],
                imported.Errors.Select(error => new PluginPackageValidationIssue(
                    $"glyph-{error.Code}",
                    error.Path,
                    $"{error.ProfileId}: {error.Message}"))
                    .OrderBy(issue => issue.Path, StringComparer.Ordinal)
                    .ThenBy(issue => issue.Code, StringComparer.Ordinal)
                    .ToArray(),
                EmptyHashes());
        }
        if (imported.Profiles.Count == 0)
        {
            return Failure(
                "no-glyph-profiles",
                "glyphProfiles",
                "Package manifest declares no glyph profiles to import.");
        }

        DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
            outputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!decision.IsAllowed || decision.FullPath is null
            || Directory.Exists(decision.FullPath) || File.Exists(decision.FullPath))
        {
            return Failure(
                "invalid-output",
                outputDirectory,
                decision.Reason ?? "Glyph generation output must be a new explicit directory.");
        }
        string sourcePrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.Equals(decision.FullPath, root, StringComparison.OrdinalIgnoreCase)
            || decision.FullPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "invalid-output",
                outputDirectory,
                "Glyph generation output must be outside the immutable source package.");
        }

        Dictionary<string, byte[]> generated = BuildGeneratedFiles(imported.Profiles);
        Dictionary<string, string> hashes = generated.ToDictionary(
            pair => pair.Key,
            pair => Hash(pair.Value),
            StringComparer.Ordinal);
        try
        {
            Directory.CreateDirectory(decision.FullPath);
            foreach ((string relative, byte[] bytes) in generated.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string path = Path.GetFullPath(Path.Combine(
                    decision.FullPath,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(
                    decision.FullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Generated glyph path escaped its output directory.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using FileStream output = new(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough);
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
        }
        catch
        {
            TryDeleteNewDirectory(decision.FullPath);
            throw;
        }

        GeneratedGlyphProfileSummary[] profiles = imported.Profiles.Select(profile =>
            new GeneratedGlyphProfileSummary
            {
                ProfileId = profile.Manifest.ProfileId,
                Revision = profile.Manifest.Revision,
                SourceId = profile.Manifest.Provenance.SourceId,
                SourceRevision = profile.Manifest.Provenance.SourceRevision,
                NoticeHashes = profile.Manifest.Assets
                    .Select(asset => asset.Provenance.LicenseNoticeSha256)
                    .Append(profile.Manifest.Provenance.LicenseNoticeSha256)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
            }).OrderBy(profile => profile.ProfileId, StringComparer.Ordinal).ToArray();
        return Report(profiles, [], hashes);
    }

    private static Dictionary<string, byte[]> BuildGeneratedFiles(
        IReadOnlyList<ImportedGlyphProfile> profiles)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (ImportedGlyphProfile profile in profiles)
        {
            foreach (GlyphAssetLockEntry assetLock in profile.Manifest.Assets)
            {
                ImportedGlyphAsset asset = profile.Assets[assetLock.Sha256];
                byte[] bytes = asset.Vector is { } vector
                    ? vector.CanonicalSvgUtf8.ToArray()
                    : asset.RasterPng.ToArray();
                string path = GlyphPackageLayout.GeneratedAsset(assetLock.Sha256, assetLock.Format);
                if (files.TryGetValue(path, out byte[]? existing))
                {
                    if (!existing.AsSpan().SequenceEqual(bytes))
                    {
                        throw new InvalidDataException("One source hash produced conflicting generated bytes.");
                    }
                }
                else
                {
                    files.Add(path, bytes);
                }
            }
        }
        return files;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static GlyphPackageGenerationReport Failure(string code, string path, string message) =>
        Report([], [new PluginPackageValidationIssue(code, path, message)], EmptyHashes());

    private static GlyphPackageGenerationReport Report(
        IReadOnlyList<GeneratedGlyphProfileSummary> profiles,
        IReadOnlyList<PluginPackageValidationIssue> issues,
        IReadOnlyDictionary<string, string> hashes) => new()
        {
            Valid = issues.Count == 0,
            Issues = issues,
            Profiles = profiles,
            GeneratedFileHashes = hashes,
        };

    private static IReadOnlyDictionary<string, string> EmptyHashes() =>
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static void TryDeleteNewDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Preserve the generation failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the generation failure.
        }
    }
}
