using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSGM.Device.Sdk.Ipc;

namespace WSGM.Device.Sdk.Glyphs;

/// <summary>Supplies immutable files from one already selected plugin package.</summary>
/// <remarks>
/// Implementations own package-root confinement, reparse-point rejection, and stable bounded reads.
/// The loader derives artwork paths from validated hashes and validates the sole manifest-provided
/// notice path before asking the source to read it.
/// </remarks>
public interface IGlyphPackageSource
{
    /// <summary>Returns bounded profile identifiers from the fixed profiles directory.</summary>
    /// <returns>Package profile identifiers, never paths.</returns>
    IReadOnlyList<string> EnumerateProfileIds();

    /// <summary>Reads one loader-approved relative package path under a byte budget.</summary>
    /// <param name="relativePath">A fixed or validated relative package path.</param>
    /// <param name="maximumBytes">Maximum accepted byte count.</param>
    /// <param name="bytes">Stable owned bytes when the read succeeds.</param>
    /// <returns>True only when the file exists and was read within the budget.</returns>
    bool TryRead(string relativePath, int maximumBytes, out byte[] bytes);
}

/// <summary>Stable reason a package-carried glyph profile was rejected.</summary>
public enum GlyphPackageImportCode
{
    /// <summary>A profile manifest was absent.</summary>
    ProfileManifestMissing,

    /// <summary>Profile JSON or semantic data was invalid.</summary>
    ProfileManifestInvalid,

    /// <summary>The profile identifier did not match its manifest filename.</summary>
    ProfileIdentityMismatch,

    /// <summary>The source returned the same profile identifier more than once.</summary>
    DuplicateProfile,

    /// <summary>A hash-addressed artwork file was absent or exceeded its byte budget.</summary>
    AssetMissing,

    /// <summary>An artwork file failed its hash, size, format, dimension, or safety checks.</summary>
    AssetRejected,

    /// <summary>The required license or attribution notice was unsafe or unavailable.</summary>
    NoticeRejected,
}

/// <summary>One deterministic package-glyph rejection.</summary>
/// <param name="ProfileId">Referenced profile identifier.</param>
/// <param name="Path">Confined relative package path, or the fixed profiles directory.</param>
/// <param name="Code">Stable failure reason.</param>
/// <param name="Message">Sanitized human-readable detail.</param>
public sealed record GlyphPackageImportError(
    string ProfileId,
    string Path,
    GlyphPackageImportCode Code,
    string Message);

/// <summary>Safe imported profiles and all rejected package entries.</summary>
/// <param name="Profiles">Profiles whose metadata, artwork, control map, and notice passed.</param>
/// <param name="Errors">Deterministically ordered rejection reasons.</param>
public sealed record GlyphPackageImportResult(
    IReadOnlyList<ImportedGlyphProfile> Profiles,
    IReadOnlyList<GlyphPackageImportError> Errors)
{
    /// <summary>Whether every discovered package profile passed.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The single bounded loader for plugin-owned glyph manifests, artwork, control maps, and notices.
/// </summary>
public static class GlyphPackageImporter
{
    private const int MaxProfiles = GlyphProfileLimits.MaxProfiles;
    private const int MaxNoticePathLength = 256;
    private const int MaxJsonDepth = 12;

    private static readonly DeviceWireJsonContext ReadContext = new(
        new JsonSerializerOptions(DeviceWireJsonContext.Default.Options)
        {
            MaxDepth = MaxJsonDepth,
        });

    /// <summary>Loads and validates every profile in one immutable package source.</summary>
    /// <param name="source">Immutable, confined package source.</param>
    /// <returns>Valid safe profiles and deterministic rejection reasons.</returns>
    public static GlyphPackageImportResult Import(IGlyphPackageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<ImportedGlyphProfile> profiles = [];
        List<GlyphPackageImportError> errors = [];
        HashSet<string> profileIds = new(StringComparer.Ordinal);
        IReadOnlyList<string> discovered = source.EnumerateProfileIds() ?? [];

        foreach (string profileId in discovered.Take(MaxProfiles).Order(StringComparer.Ordinal))
        {
            if (!IsIdentifier(profileId))
            {
                errors.Add(new GlyphPackageImportError(
                    profileId ?? string.Empty,
                    "glyphs/profiles",
                    GlyphPackageImportCode.ProfileManifestInvalid,
                    "The profile filename is not a bounded identifier."));
                continue;
            }

            if (!profileIds.Add(profileId))
            {
                errors.Add(new GlyphPackageImportError(
                    profileId,
                    "glyphs/profiles",
                    GlyphPackageImportCode.DuplicateProfile,
                    "The package source returned the same profile identifier more than once."));
                continue;
            }

            LoadProfile(profileId, source, profiles, errors);
        }

        if (discovered.Count > MaxProfiles)
        {
            errors.Add(new GlyphPackageImportError(
                string.Empty,
                "glyphs/profiles",
                GlyphPackageImportCode.ProfileManifestInvalid,
                $"The package contains more than {MaxProfiles} glyph profiles."));
        }

        return new GlyphPackageImportResult(
            profiles.OrderBy(profile => profile.Manifest.ProfileId, StringComparer.Ordinal).ToArray(),
            errors.OrderBy(error => error.ProfileId, StringComparer.Ordinal)
                .ThenBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Code)
                .ToArray());
    }

    private static void LoadProfile(
        string profileId,
        IGlyphPackageSource source,
        ICollection<ImportedGlyphProfile> profiles,
        ICollection<GlyphPackageImportError> errors)
    {
        string profilePath = GlyphPackageLayout.ProfileManifest(profileId);
        if (!source.TryRead(profilePath, GlyphProfileLimits.MaxDocumentBytes, out byte[] manifestBytes)
            || manifestBytes is not { Length: > 0 }
            || manifestBytes.Length > GlyphProfileLimits.MaxDocumentBytes)
        {
            errors.Add(new GlyphPackageImportError(
                profileId,
                profilePath,
                GlyphPackageImportCode.ProfileManifestMissing,
                "The profile manifest is absent or exceeds its byte budget."));
            return;
        }

        GlyphProfileManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                manifestBytes.AsSpan(),
                ReadContext.GlyphProfileManifest);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            errors.Add(new GlyphPackageImportError(
                profileId,
                profilePath,
                GlyphPackageImportCode.ProfileManifestInvalid,
                exception.Message));
            return;
        }

        if (manifest is null)
        {
            errors.Add(new GlyphPackageImportError(
                profileId,
                profilePath,
                GlyphPackageImportCode.ProfileManifestInvalid,
                "The profile document deserialized to null."));
            return;
        }

        List<GlyphPackageImportError> profileErrors = [];
        ValidateManifest(profileId, profilePath, manifest, profileErrors);
        if (!string.Equals(manifest.ProfileId, profileId, StringComparison.Ordinal))
        {
            profileErrors.Add(new GlyphPackageImportError(
                profileId,
                profilePath,
                GlyphPackageImportCode.ProfileIdentityMismatch,
                "The profile identifier differs from its manifest filename."));
        }

        if (profileErrors.Count > 0)
        {
            AddRange(errors, profileErrors);
            return;
        }

        GlyphProfileManifest ordered = OrderManifest(manifest);
        Dictionary<string, ImportedGlyphAsset> importedAssets = new(StringComparer.Ordinal);
        foreach (GlyphAssetLockEntry asset in ordered.Assets)
        {
            string assetPath = GlyphPackageLayout.Asset(asset.Sha256, asset.Format);
            if (!source.TryRead(assetPath, GlyphProfileLimits.MaxAssetBytes, out byte[] suppliedBytes)
                || suppliedBytes is not { Length: > 0 }
                || suppliedBytes.Length > GlyphProfileLimits.MaxAssetBytes)
            {
                profileErrors.Add(new GlyphPackageImportError(
                    profileId,
                    assetPath,
                    GlyphPackageImportCode.AssetMissing,
                    "The hash-addressed artwork is absent or exceeds its byte budget."));
                continue;
            }

            byte[] bytes = suppliedBytes.ToArray();
            if (bytes.Length != asset.ByteCount)
            {
                profileErrors.Add(new GlyphPackageImportError(
                    profileId,
                    assetPath,
                    GlyphPackageImportCode.AssetRejected,
                    $"Declared byte count is {asset.ByteCount}; the package supplied {bytes.Length}."));
                continue;
            }

            string actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.Ordinal))
            {
                profileErrors.Add(new GlyphPackageImportError(
                    profileId,
                    assetPath,
                    GlyphPackageImportCode.AssetRejected,
                    "The artwork bytes do not match the declared SHA-256."));
                continue;
            }

            AssetImportResult result = asset.Format switch
            {
                GlyphAssetFormat.Svg => GlyphSvgNormalizer.Normalize(asset, bytes),
                GlyphAssetFormat.Png => GlyphPngInspector.Inspect(asset, bytes),
                _ => AssetImportResult.Failure(
                    asset.Sha256,
                    GlyphAssetImportCode.MalformedAsset,
                    "The artwork format is unsupported."),
            };
            if (result.Asset is not null)
            {
                importedAssets.Add(asset.Sha256, result.Asset);
            }
            else
            {
                profileErrors.Add(new GlyphPackageImportError(
                    profileId,
                    assetPath,
                    GlyphPackageImportCode.AssetRejected,
                    result.Error is null
                        ? "The artwork was rejected."
                        : $"{result.Error.Code}: {result.Error.Message}"));
            }
        }

        ValidateNotice(profileId, ordered.NoticePath, source, profileErrors);
        if (profileErrors.Count == 0)
        {
            profiles.Add(new ImportedGlyphProfile
            {
                Manifest = ordered,
                Assets = importedAssets,
            });
        }
        else
        {
            AddRange(errors, profileErrors);
        }
    }

    private static void ValidateManifest(
        string profileId,
        string profilePath,
        GlyphProfileManifest manifest,
        ICollection<GlyphPackageImportError> errors)
    {
        void Invalid(string path, string message) => errors.Add(new GlyphPackageImportError(
            profileId,
            profilePath,
            GlyphPackageImportCode.ProfileManifestInvalid,
            $"{path}: {message}"));

        if (manifest.SchemaVersion != GlyphProfileLimits.CurrentSchemaVersion)
        {
            Invalid("schemaVersion", $"Schema version {manifest.SchemaVersion} is not supported.");
        }
        if (!IsIdentifier(manifest.ProfileId))
        {
            Invalid("profileId", "A bounded identifier is required.");
        }
        if (!IsDisplayText(manifest.DisplayName, GlyphProfileLimits.MaxDisplayNameLength))
        {
            Invalid("displayName", "A bounded plain display name is required.");
        }
        if (manifest.Revision <= 0)
        {
            Invalid("revision", "The profile revision must be positive.");
        }
        if (!IsIdentifier(manifest.SourceRevision))
        {
            Invalid("sourceRevision", "A bounded immutable source revision is required.");
        }
        if (!IsNoticePath(manifest.NoticePath))
        {
            Invalid("noticePath", "The notice must be a confined .md or .txt package-relative path.");
        }

        IReadOnlyList<string> exactDeviceIds = manifest.ExactDeviceIds ?? [];
        if (exactDeviceIds.Count > GlyphProfileLimits.MaxExactDevices)
        {
            Invalid("exactDeviceIds", $"At most {GlyphProfileLimits.MaxExactDevices} entries are accepted.");
        }
        HashSet<string> deviceIds = new(StringComparer.Ordinal);
        for (int index = 0; index < exactDeviceIds.Count; index++)
        {
            string deviceId = exactDeviceIds[index];
            if (!IsIdentifier(deviceId))
            {
                Invalid($"exactDeviceIds[{index}]", "A bounded identifier is required.");
            }
            else if (!deviceIds.Add(deviceId))
            {
                Invalid($"exactDeviceIds[{index}]", "The exact device is declared more than once.");
            }
        }

        IReadOnlyList<GlyphAssetLockEntry> assets = manifest.Assets ?? [];
        if (assets.Count > GlyphProfileLimits.MaxAssets)
        {
            Invalid("assets", $"At most {GlyphProfileLimits.MaxAssets} entries are accepted.");
        }
        Dictionary<string, GlyphAssetLockEntry> assetsByHash = new(StringComparer.Ordinal);
        long totalBytes = 0;
        for (int index = 0; index < assets.Count; index++)
        {
            GlyphAssetLockEntry? asset = assets[index];
            string path = $"assets[{index}]";
            if (asset is null)
            {
                Invalid(path, "An asset declaration is required.");
                continue;
            }
            if (!IsHash(asset.Sha256))
            {
                Invalid($"{path}.sha256", "SHA-256 must be 64 lowercase hexadecimal characters.");
            }
            else if (!assetsByHash.TryAdd(asset.Sha256, asset))
            {
                Invalid($"{path}.sha256", "The asset hash is declared more than once.");
            }
            if (!Enum.IsDefined(asset.Format) || !Enum.IsDefined(asset.Role))
            {
                Invalid(path, "The artwork format or role is undefined.");
            }
            if (asset.ByteCount is <= 0 or > GlyphProfileLimits.MaxAssetBytes)
            {
                Invalid($"{path}.byteCount",
                    $"The byte count must be between 1 and {GlyphProfileLimits.MaxAssetBytes}.");
            }
            else
            {
                totalBytes += asset.ByteCount;
            }
            ValidateAssetShape(asset, path, Invalid);
        }
        if (totalBytes > GlyphProfileLimits.MaxProfileBytes)
        {
            Invalid("assets", $"Aggregate artwork exceeds {GlyphProfileLimits.MaxProfileBytes} bytes.");
        }

        GlyphControllerImages images = manifest.ControllerImages ?? new GlyphControllerImages();
        ValidateImageReference(
            images.FullSha256,
            GlyphAssetRole.FullController,
            "controllerImages.fullSha256",
            assetsByHash,
            Invalid);
        ValidateImageReference(
            images.LeftSha256,
            GlyphAssetRole.LeftController,
            "controllerImages.leftSha256",
            assetsByHash,
            Invalid);
        ValidateImageReference(
            images.RightSha256,
            GlyphAssetRole.RightController,
            "controllerImages.rightSha256",
            assetsByHash,
            Invalid);

        IReadOnlyList<GlyphControlMapping> controls = manifest.Controls ?? [];
        if (controls.Count > GlyphProfileLimits.MaxControls)
        {
            Invalid("controls", $"At most {GlyphProfileLimits.MaxControls} entries are accepted.");
        }
        Dictionary<GlyphControlId, GlyphControlMapping> controlsById = [];
        for (int index = 0; index < controls.Count; index++)
        {
            GlyphControlMapping? control = controls[index];
            string path = $"controls[{index}]";
            if (control is null)
            {
                Invalid(path, "A control mapping is required.");
                continue;
            }
            if (!Enum.IsDefined(control.Control)
                || !Enum.IsDefined(control.Presence)
                || !Enum.IsDefined(control.Side))
            {
                Invalid(path, "The control, presence, or side value is undefined.");
            }
            if (!controlsById.TryAdd(control.Control, control))
            {
                Invalid($"{path}.control", "The control is mapped more than once.");
            }
            if (control.PhysicalLabel is { } label
                && !IsDisplayText(label, GlyphProfileLimits.MaxPhysicalLabelLength))
            {
                Invalid($"{path}.physicalLabel", "The physical label is not bounded plain text.");
            }
            if (control.Presence is GlyphControlPresence.Absent && control.AssetSha256 is not null)
            {
                Invalid($"{path}.assetSha256", "A physically absent control cannot declare artwork.");
            }
            if (control.AssetSha256 is { } hash
                && (!IsHash(hash)
                    || !assetsByHash.TryGetValue(hash, out GlyphAssetLockEntry? asset)
                    || asset.Role is not GlyphAssetRole.Control))
            {
                Invalid($"{path}.assetSha256", "Control artwork must resolve to a Control asset.");
            }
        }

        IReadOnlyList<GlyphControlAlias> aliases = manifest.Aliases ?? [];
        if (aliases.Count > GlyphProfileLimits.MaxAliases)
        {
            Invalid("aliases", $"At most {GlyphProfileLimits.MaxAliases} entries are accepted.");
        }
        HashSet<GlyphControlId> aliasSources = aliases
            .Where(alias => alias is not null)
            .Select(alias => alias.LogicalControl)
            .ToHashSet();
        HashSet<GlyphControlId> seenAliases = [];
        for (int index = 0; index < aliases.Count; index++)
        {
            GlyphControlAlias? alias = aliases[index];
            string path = $"aliases[{index}]";
            if (alias is null)
            {
                Invalid(path, "A control alias is required.");
                continue;
            }
            if (!Enum.IsDefined(alias.LogicalControl) || !Enum.IsDefined(alias.PhysicalControl))
            {
                Invalid(path, "The logical or physical control is undefined.");
            }
            if (!seenAliases.Add(alias.LogicalControl))
            {
                Invalid($"{path}.logicalControl", "The logical control is aliased more than once.");
            }
            bool targetPresent = controlsById.TryGetValue(
                alias.PhysicalControl,
                out GlyphControlMapping? target)
                && target.Presence is GlyphControlPresence.Present;
            if (alias.LogicalControl == alias.PhysicalControl
                || aliasSources.Contains(alias.PhysicalControl)
                || !targetPresent)
            {
                Invalid(path, "An alias must directly target a distinct, present physical control.");
            }
        }
    }

    private static void ValidateAssetShape(
        GlyphAssetLockEntry asset,
        string path,
        Action<string, string> invalid)
    {
        if (asset.Format is GlyphAssetFormat.Svg)
        {
            if (asset.ViewBox is not { } viewBox
                || asset.PixelWidth is not null
                || asset.PixelHeight is not null)
            {
                invalid(path, "SVG artwork requires a view box and no raster dimensions.");
                return;
            }
            if (viewBox.Width <= 0 || viewBox.Height <= 0
                || viewBox.Width > GlyphProfileLimits.MaxDimension
                || viewBox.Height > GlyphProfileLimits.MaxDimension
                || viewBox.X < -GlyphProfileLimits.MaxDimension
                || viewBox.X > GlyphProfileLimits.MaxDimension
                || viewBox.Y < -GlyphProfileLimits.MaxDimension
                || viewBox.Y > GlyphProfileLimits.MaxDimension)
            {
                invalid($"{path}.viewBox", "The SVG view box exceeds the coordinate budget.");
            }
            return;
        }

        if (asset.Format is GlyphAssetFormat.Png)
        {
            if (asset.ViewBox is not null || asset.PixelWidth is not > 0 || asset.PixelHeight is not > 0)
            {
                invalid(path, "PNG artwork requires positive pixel dimensions and no view box.");
                return;
            }
            if (asset.PixelWidth > GlyphProfileLimits.MaxDimension
                || asset.PixelHeight > GlyphProfileLimits.MaxDimension
                || (long)asset.PixelWidth.Value * asset.PixelHeight.Value
                    > GlyphProfileLimits.MaxRasterPixels)
            {
                invalid(path, "PNG dimensions exceed the axis or decoded-pixel budget.");
            }
        }
    }

    private static void ValidateImageReference(
        string? hash,
        GlyphAssetRole expectedRole,
        string path,
        IReadOnlyDictionary<string, GlyphAssetLockEntry> assets,
        Action<string, string> invalid)
    {
        if (hash is null)
        {
            return;
        }
        if (!IsHash(hash)
            || !assets.TryGetValue(hash, out GlyphAssetLockEntry? asset)
            || asset.Role != expectedRole)
        {
            invalid(path, $"The image must resolve to a {expectedRole} asset.");
        }
    }

    private static void ValidateNotice(
        string profileId,
        string noticePath,
        IGlyphPackageSource source,
        ICollection<GlyphPackageImportError> errors)
    {
        if (!source.TryRead(noticePath, GlyphProfileLimits.MaxNoticeBytes, out byte[] supplied)
            || supplied is not { Length: > 0 }
            || supplied.Length > GlyphProfileLimits.MaxNoticeBytes
            || !IsPlainUtf8(supplied))
        {
            errors.Add(new GlyphPackageImportError(
                profileId,
                noticePath,
                GlyphPackageImportCode.NoticeRejected,
                "The notice is absent, empty, oversized, or not bounded plain UTF-8 text."));
        }
    }

    private static GlyphProfileManifest OrderManifest(GlyphProfileManifest manifest) => manifest with
    {
        ExactDeviceIds = (manifest.ExactDeviceIds ?? [])
            .Order(StringComparer.Ordinal)
            .ToArray(),
        Assets = (manifest.Assets ?? [])
            .OrderBy(asset => asset.Sha256, StringComparer.Ordinal)
            .ToArray(),
        Controls = (manifest.Controls ?? [])
            .OrderBy(control => control.Control)
            .ToArray(),
        Aliases = (manifest.Aliases ?? [])
            .OrderBy(alias => alias.LogicalControl)
            .ThenBy(alias => alias.PhysicalControl)
            .ToArray(),
    };

    private static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > GlyphProfileLimits.MaxIdentifierLength)
        {
            return false;
        }
        return value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');
    }

    private static bool IsDisplayText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(character => !char.IsControl(character));

    private static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsNoticePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxNoticePathLength
            || value[0] == '/'
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal)
            || (!value.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !value.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string[] segments = value.Split('/');
        return segments.All(segment => segment.Length > 0
            && segment is not "." and not ".."
            && segment.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_'));
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

    private static void AddRange<T>(ICollection<T> target, IEnumerable<T> values)
    {
        foreach (T value in values)
        {
            target.Add(value);
        }
    }
}
