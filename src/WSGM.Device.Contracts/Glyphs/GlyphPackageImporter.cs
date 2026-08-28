using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Glyphs;

/// <summary>Supplies immutable package files only at WSGM-owned relative paths.</summary>
/// <remarks>
/// Implementations own package-root confinement, reparse-point rejection, and stable bounded reads.
/// The importer derives every requested path from a validated content hash.
/// </remarks>
public interface IGlyphPackageSource
{
    /// <summary>Reads one WSGM-owned relative package path under a byte budget.</summary>
    /// <param name="relativePath">Path produced only by <see cref="GlyphPackageLayout"/>.</param>
    /// <param name="maximumBytes">Maximum accepted byte count.</param>
    /// <param name="bytes">Stable owned bytes when the read succeeds.</param>
    /// <returns>True only when the file exists and was read within the budget.</returns>
    bool TryRead(string relativePath, int maximumBytes, out byte[] bytes);
}

/// <summary>Stable reason a package-carried glyph profile was rejected.</summary>
public enum GlyphPackageImportCode
{
    /// <summary>A hash-addressed profile manifest was absent.</summary>
    ProfileManifestMissing,
    /// <summary>Profile manifest bytes did not match the package-manifest lock.</summary>
    ProfileManifestHashMismatch,
    /// <summary>Profile manifest JSON or semantic data was invalid.</summary>
    ProfileManifestInvalid,
    /// <summary>Profile JSON was valid but not the deterministic canonical representation.</summary>
    ProfileManifestDrift,
    /// <summary>The profile identifier did not match its package-manifest reference.</summary>
    ProfileIdentityMismatch,
    /// <summary>More than one package reference produced the same profile identifier.</summary>
    DuplicateProfile,
    /// <summary>A source asset failed strict import.</summary>
    AssetRejected,
    /// <summary>A generated safe asset was absent or differed from deterministic importer output.</summary>
    GeneratedAssetDrift,
    /// <summary>A hash-pinned notice was absent, malformed, or had changed.</summary>
    NoticeRejected,
}

/// <summary>One deterministic package-glyph rejection.</summary>
/// <param name="ProfileId">Referenced profile identifier.</param>
/// <param name="Path">WSGM-owned relative package path.</param>
/// <param name="Code">Stable failure reason.</param>
/// <param name="Message">Sanitized human-readable detail.</param>
public sealed record GlyphPackageImportError(
    string ProfileId,
    string Path,
    GlyphPackageImportCode Code,
    string Message);

/// <summary>Safe imported profiles and all rejected package entries.</summary>
/// <param name="Profiles">Profiles whose metadata, assets, generated outputs, and notices passed.</param>
/// <param name="Errors">Deterministically ordered rejection reasons.</param>
public sealed record GlyphPackageImportResult(
    IReadOnlyList<ImportedGlyphProfile> Profiles,
    IReadOnlyList<GlyphPackageImportError> Errors)
{
    /// <summary>Whether every declared package profile passed.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Whether package import must prove generated assets are current.</summary>
public enum GlyphGeneratedAssetPolicy
{
    /// <summary>Require every generated file and compare it byte-for-byte with current output.</summary>
    RequireExact,

    /// <summary>Generate safe assets in memory without requiring existing output files.</summary>
    Generate,
}

/// <summary>Shared pack-time and load-time validator for package-carried glyph profiles.</summary>
public static class GlyphPackageImporter
{
    /// <summary>Imports every profile declared by a validated package manifest.</summary>
    /// <param name="packageManifest">Validated package manifest.</param>
    /// <param name="source">Immutable, confined package source.</param>
    /// <returns>Valid safe profiles and deterministic rejection reasons.</returns>
    public static GlyphPackageImportResult Import(
        PluginManifest packageManifest,
        IGlyphPackageSource source,
        GlyphGeneratedAssetPolicy generatedAssetPolicy = GlyphGeneratedAssetPolicy.RequireExact)
    {
        ArgumentNullException.ThrowIfNull(packageManifest);
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(generatedAssetPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(generatedAssetPolicy));
        }

        List<ImportedGlyphProfile> profiles = [];
        List<GlyphPackageImportError> errors = [];
        HashSet<string> profileIds = new(StringComparer.Ordinal);

        foreach (GlyphProfilePackageReference reference in packageManifest.GlyphProfiles
            .OrderBy(item => item.ProfileId, StringComparer.Ordinal))
        {
            if (!profileIds.Add(reference.ProfileId))
            {
                errors.Add(new GlyphPackageImportError(
                    reference.ProfileId,
                    "glyphs/profiles",
                    GlyphPackageImportCode.DuplicateProfile,
                    "The package declares the same profile identifier more than once."));
                continue;
            }

            string profilePath = GlyphPackageLayout.ProfileManifest(reference.ManifestSha256);
            if (!source.TryRead(profilePath, GlyphProfileLimits.MaxDocumentBytes, out byte[] manifestBytes))
            {
                errors.Add(new GlyphPackageImportError(
                    reference.ProfileId,
                    profilePath,
                    GlyphPackageImportCode.ProfileManifestMissing,
                    "The hash-addressed profile manifest is absent or exceeds its byte budget."));
                continue;
            }

            string actualManifestHash = Hash(manifestBytes);
            if (!string.Equals(actualManifestHash, reference.ManifestSha256, StringComparison.Ordinal))
            {
                errors.Add(new GlyphPackageImportError(
                    reference.ProfileId,
                    profilePath,
                    GlyphPackageImportCode.ProfileManifestHashMismatch,
                    "Profile manifest bytes do not match the package-manifest hash."));
                continue;
            }

            GlyphProfileReadResult read = GlyphProfileReader.Read(manifestBytes);
            if (!read.IsValid || read.Manifest is null)
            {
                errors.Add(new GlyphPackageImportError(
                    reference.ProfileId,
                    profilePath,
                    GlyphPackageImportCode.ProfileManifestInvalid,
                    string.Join("; ", read.Errors.Select(error =>
                        $"{error.Path}: {error.Code} {error.Message}"))));
                continue;
            }

            GlyphProfileManifest manifest = read.Manifest;
            byte[] canonicalBytes = GlyphProfileReader.ToCanonicalUtf8(manifest);
            if (!manifestBytes.AsSpan().SequenceEqual(canonicalBytes))
            {
                errors.Add(new GlyphPackageImportError(
                    reference.ProfileId,
                    profilePath,
                    GlyphPackageImportCode.ProfileManifestDrift,
                    "Profile manifest differs from deterministic canonical JSON."));
                continue;
            }
            if (!string.Equals(manifest.ProfileId, reference.ProfileId, StringComparison.Ordinal))
            {
                errors.Add(new GlyphPackageImportError(
                    reference.ProfileId,
                    profilePath,
                    GlyphPackageImportCode.ProfileIdentityMismatch,
                    "Profile identifier differs from its package-manifest reference."));
                continue;
            }

            GlyphProfileImportResult imported = GlyphProfileImporter.Import(
                manifest,
                new ProfileAssetSource(source, manifest));
            if (!imported.IsValid || imported.Profile is null)
            {
                errors.AddRange(imported.Errors.Select(error => new GlyphPackageImportError(
                    reference.ProfileId,
                    error.Sha256.Length == 0
                        ? profilePath
                        : AssetPath(manifest, error.Sha256),
                    GlyphPackageImportCode.AssetRejected,
                    $"{error.Code}: {error.Message}")));
                continue;
            }

            List<GlyphPackageImportError> profileErrors = [];
            if (generatedAssetPolicy is GlyphGeneratedAssetPolicy.RequireExact)
            {
                ValidateGeneratedOutputs(reference.ProfileId, imported.Profile, source, profileErrors);
            }
            ValidateNotices(reference.ProfileId, imported.Profile.Manifest, source, profileErrors);
            if (profileErrors.Count == 0)
            {
                profiles.Add(imported.Profile);
            }
            else
            {
                errors.AddRange(profileErrors);
            }
        }

        return new GlyphPackageImportResult(
            profiles.OrderBy(profile => profile.Manifest.ProfileId, StringComparer.Ordinal).ToArray(),
            errors.OrderBy(error => error.ProfileId, StringComparer.Ordinal)
                .ThenBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Code)
                .ToArray());
    }

    private static void ValidateGeneratedOutputs(
        string profileId,
        ImportedGlyphProfile profile,
        IGlyphPackageSource source,
        ICollection<GlyphPackageImportError> errors)
    {
        foreach (GlyphAssetLockEntry assetLock in profile.Manifest.Assets)
        {
            string path = GlyphPackageLayout.GeneratedAsset(assetLock.Sha256, assetLock.Format);
            if (!source.TryRead(path, GlyphProfileLimits.MaxAssetBytes, out byte[] generated))
            {
                errors.Add(new GlyphPackageImportError(
                    profileId,
                    path,
                    GlyphPackageImportCode.GeneratedAssetDrift,
                    "The deterministic generated asset is absent or oversized."));
                continue;
            }

            ImportedGlyphAsset asset = profile.Assets[assetLock.Sha256];
            ReadOnlySpan<byte> expected = asset.Vector is { } vector
                ? vector.CanonicalSvgUtf8.Span
                : asset.RasterPng.Span;
            if (!generated.AsSpan().SequenceEqual(expected))
            {
                errors.Add(new GlyphPackageImportError(
                    profileId,
                    path,
                    GlyphPackageImportCode.GeneratedAssetDrift,
                    "Generated asset differs from current deterministic importer output."));
            }
        }
    }

    private static void ValidateNotices(
        string profileId,
        GlyphProfileManifest manifest,
        IGlyphPackageSource source,
        ICollection<GlyphPackageImportError> errors)
    {
        string[] hashes = manifest.Assets.Select(asset => asset.Provenance.LicenseNoticeSha256)
            .Append(manifest.Provenance.LicenseNoticeSha256)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string hash in hashes)
        {
            string path = GlyphPackageLayout.Notice(hash);
            if (!source.TryRead(path, GlyphProfileLimits.MaxNoticeBytes, out byte[] notice)
                || !string.Equals(Hash(notice), hash, StringComparison.Ordinal)
                || !IsPlainUtf8(notice))
            {
                errors.Add(new GlyphPackageImportError(
                    profileId,
                    path,
                    GlyphPackageImportCode.NoticeRejected,
                    "Notice is absent, changed, oversized, or not bounded plain UTF-8 text."));
            }
        }
    }

    private static bool IsPlainUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Length > 0 && text.All(character =>
                character is '\r' or '\n' or '\t' || !char.IsControl(character));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string AssetPath(GlyphProfileManifest manifest, string hash)
    {
        GlyphAssetLockEntry? asset = manifest.Assets.FirstOrDefault(item =>
            string.Equals(item.Sha256, hash, StringComparison.Ordinal));
        return asset is null
            ? "glyphs/assets"
            : GlyphPackageLayout.Asset(hash, asset.Format);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class ProfileAssetSource(
        IGlyphPackageSource package,
        GlyphProfileManifest manifest) : IGlyphAssetSource
    {
        private readonly IReadOnlyDictionary<string, GlyphAssetFormat> _formats =
            manifest.Assets.ToDictionary(asset => asset.Sha256, asset => asset.Format, StringComparer.Ordinal);

        public bool TryRead(string sha256, int maximumBytes, out byte[] bytes)
        {
            if (_formats.TryGetValue(sha256, out GlyphAssetFormat format))
            {
                return package.TryRead(GlyphPackageLayout.Asset(sha256, format), maximumBytes, out bytes);
            }

            bytes = [];
            return false;
        }
    }
}
