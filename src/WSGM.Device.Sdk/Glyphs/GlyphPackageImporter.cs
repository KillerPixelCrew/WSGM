using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Serialization;

namespace WSGM.Device.Sdk.Glyphs;

/// <summary>Supplies immutable files from one already selected plugin package.</summary>
/// <remarks>
///     Implementations own package-root confinement, reparse-point rejection, and stable bounded reads.
///     The loader derives artwork paths from validated asset identifiers and validates the sole
///     manifest-provided notice path before asking the source to read it.
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

    /// <summary>The package profile directory could not be inspected.</summary>
    ProfileEnumerationFailed,

    /// <summary>An identified artwork file was absent or exceeded its byte budget.</summary>
    AssetMissing,

    /// <summary>An artwork file failed its size, format, dimension, or safety checks.</summary>
    AssetRejected,

    /// <summary>The required license or attribution notice was unsafe or unavailable.</summary>
    NoticeRejected
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
///     The single bounded loader for plugin-owned glyph manifests, artwork, control maps, and notices.
/// </summary>
public static class GlyphPackageImporter
{
    private const int MaxProfiles = GlyphProfileLimits.MaxProfiles;
    private const int MaxNoticePathLength = 256;
    private const int MaxJsonDepth = 12;

    private static readonly DeviceJsonContext ReadContext = new(
        new JsonSerializerOptions(DeviceJsonContext.Default.Options)
        {
            MaxDepth = MaxJsonDepth
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
        IReadOnlyList<string> discovered;
        try
        {
            discovered = source.EnumerateProfileIds() ?? [];
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or NotSupportedException)
        {
            errors.Add(new GlyphPackageImportError(
                string.Empty,
                "glyphs/profiles",
                GlyphPackageImportCode.ProfileEnumerationFailed,
                $"The profile directory could not be read ({exception.GetType().Name})."));
            return new GlyphPackageImportResult([], errors);
        }

        foreach (var profileId in discovered.Take(MaxProfiles).Order(StringComparer.Ordinal))
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
            [.. profiles.OrderBy(profile => profile.Manifest.ProfileId, StringComparer.Ordinal)],
            [
                .. errors.OrderBy(error => error.ProfileId, StringComparer.Ordinal)
                    .ThenBy(error => error.Path, StringComparer.Ordinal)
                    .ThenBy(error => error.Code)
            ]);
    }

    private static void LoadProfile(
        string profileId,
        IGlyphPackageSource source,
        List<ImportedGlyphProfile> profiles,
        List<GlyphPackageImportError> errors)
    {
        var profilePath = GlyphPackageLayout.ProfileManifest(profileId);
        if (!source.TryRead(profilePath, GlyphProfileLimits.MaxDocumentBytes, out var manifestBytes)
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
            errors.AddRange(profileErrors);
            return;
        }

        var ordered = OrderManifest(manifest);
        Dictionary<string, ImportedGlyphAsset> importedAssets = new(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var asset in ordered.Assets)
        {
            var assetPath = GlyphPackageLayout.Asset(asset.AssetId, asset.Format);
            if (!source.TryRead(assetPath, GlyphProfileLimits.MaxAssetBytes, out var suppliedBytes)
                || suppliedBytes is not { Length: > 0 }
                || suppliedBytes.Length > GlyphProfileLimits.MaxAssetBytes)
            {
                profileErrors.Add(new GlyphPackageImportError(
                    profileId,
                    assetPath,
                    GlyphPackageImportCode.AssetMissing,
                    "The identified artwork is absent or exceeds its byte budget."));
                continue;
            }

            var bytes = suppliedBytes.ToArray();

            // The aggregate budget is measured against what the package actually supplied. Every read
            // is already capped at MaxAssetBytes and the manifest is capped at MaxAssets, so the worst
            // case before this trips stays bounded.
            totalBytes += bytes.Length;
            if (totalBytes > GlyphProfileLimits.MaxProfileBytes)
            {
                profileErrors.Add(new GlyphPackageImportError(
                    profileId,
                    assetPath,
                    GlyphPackageImportCode.AssetRejected,
                    $"Aggregate artwork exceeds {GlyphProfileLimits.MaxProfileBytes} bytes."));
                break;
            }

            var result = asset.Format switch
            {
                GlyphAssetFormat.Svg => GlyphSvgNormalizer.Normalize(asset, bytes),
                GlyphAssetFormat.Png => GlyphPngInspector.Inspect(asset, bytes),
                _ => AssetImportResult.Failure(
                    asset.AssetId,
                    GlyphAssetImportCode.MalformedAsset,
                    "The artwork format is unsupported.")
            };
            if (result.Asset is not null)
            {
                importedAssets.Add(asset.AssetId, result.Asset);
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
                Assets = importedAssets
            });
        }
        else
        {
            errors.AddRange(profileErrors);
        }
    }

    private static void ValidateManifest(
        string profileId,
        string profilePath,
        GlyphProfileManifest manifest,
        List<GlyphPackageImportError> errors)
    {
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

        var exactDeviceIds = manifest.ExactDeviceIds ?? [];
        if (exactDeviceIds.Count > GlyphProfileLimits.MaxExactDevices)
        {
            Invalid("exactDeviceIds", $"At most {GlyphProfileLimits.MaxExactDevices} entries are accepted.");
        }

        HashSet<string> deviceIds = new(StringComparer.Ordinal);
        for (var index = 0; index < exactDeviceIds.Count; index++)
        {
            var deviceId = exactDeviceIds[index];
            if (!IsIdentifier(deviceId))
            {
                Invalid($"exactDeviceIds[{index}]", "A bounded identifier is required.");
            }
            else if (!deviceIds.Add(deviceId))
            {
                Invalid($"exactDeviceIds[{index}]", "The exact device is declared more than once.");
            }
        }

        var assets = manifest.Assets ?? [];
        if (assets.Count > GlyphProfileLimits.MaxAssets)
        {
            Invalid("assets", $"At most {GlyphProfileLimits.MaxAssets} entries are accepted.");
        }

        Dictionary<string, GlyphAssetEntry> assetsById = new(StringComparer.Ordinal);
        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            var path = $"assets[{index}]";
            if (asset is null)
            {
                Invalid(path, "An asset declaration is required.");
                continue;
            }

            if (!IsIdentifier(asset.AssetId))
            {
                Invalid($"{path}.assetId", "A bounded identifier is required.");
            }
            else if (!assetsById.TryAdd(asset.AssetId, asset))
            {
                Invalid($"{path}.assetId", "The asset identifier is declared more than once.");
            }

            if (!Enum.IsDefined(asset.Format) || !Enum.IsDefined(asset.Role))
            {
                Invalid(path, "The artwork format or role is undefined.");
            }

            ValidateAssetShape(asset, path, Invalid);
        }

        var images = manifest.ControllerImages ?? new GlyphControllerImages();
        ValidateImageReference(
            images.FullAssetId,
            GlyphAssetRole.FullController,
            "controllerImages.fullAssetId",
            assetsById,
            Invalid);
        ValidateImageReference(
            images.LeftAssetId,
            GlyphAssetRole.LeftController,
            "controllerImages.leftAssetId",
            assetsById,
            Invalid);
        ValidateImageReference(
            images.RightAssetId,
            GlyphAssetRole.RightController,
            "controllerImages.rightAssetId",
            assetsById,
            Invalid);

        var controls = manifest.Controls ?? [];
        if (controls.Count > GlyphProfileLimits.MaxControls)
        {
            Invalid("controls", $"At most {GlyphProfileLimits.MaxControls} entries are accepted.");
        }

        Dictionary<GlyphControlId, GlyphControlMapping> controlsById = [];
        for (var index = 0; index < controls.Count; index++)
        {
            var control = controls[index];
            var path = $"controls[{index}]";
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

            if (control.Presence is GlyphControlPresence.Absent && control.AssetId is not null)
            {
                Invalid($"{path}.assetId", "A physically absent control cannot declare artwork.");
            }

            if (control.AssetId is { } assetId
                && (!IsIdentifier(assetId)
                    || !assetsById.TryGetValue(assetId, out var asset)
                    || asset.Role is not GlyphAssetRole.Control))
            {
                Invalid($"{path}.assetId", "Control artwork must resolve to a Control asset.");
            }

            if (control.Presence is GlyphControlPresence.Absent && control.HighlightAssetId is not null)
            {
                Invalid($"{path}.highlightAssetId", "A physically absent control cannot declare a highlight.");
            }

            if (control.HighlightAssetId is { } highlightId
                && (!IsIdentifier(highlightId)
                    || !assetsById.TryGetValue(highlightId, out var highlight)
                    || highlight.Role is not GlyphAssetRole.ControlHighlight))
            {
                Invalid($"{path}.highlightAssetId", "A control highlight must resolve to a ControlHighlight asset.");
            }

            if (control.Presence is GlyphControlPresence.Absent && control.SoftPullAssetId is not null)
            {
                Invalid($"{path}.softPullAssetId", "A physically absent control cannot declare soft-pull artwork.");
            }

            if (control.SoftPullAssetId is { } softPullId
                && (!IsIdentifier(softPullId)
                    || !assetsById.TryGetValue(softPullId, out var softPull)
                    || softPull.Role is not GlyphAssetRole.Control))
            {
                Invalid($"{path}.softPullAssetId", "Soft-pull artwork must resolve to a Control asset.");
            }
        }

        var aliases = manifest.Aliases ?? [];
        if (aliases.Count > GlyphProfileLimits.MaxAliases)
        {
            Invalid("aliases", $"At most {GlyphProfileLimits.MaxAliases} entries are accepted.");
        }

        var aliasSources = aliases
            .Where(alias => alias is not null)
            .Select(alias => alias.LogicalControl)
            .ToHashSet();
        HashSet<GlyphControlId> seenAliases = [];
        for (var index = 0; index < aliases.Count; index++)
        {
            var alias = aliases[index];
            var path = $"aliases[{index}]";
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

            var targetPresent = controlsById.TryGetValue(
                                    alias.PhysicalControl,
                                    out var target)
                                && target.Presence is GlyphControlPresence.Present;
            if (alias.LogicalControl == alias.PhysicalControl
                || aliasSources.Contains(alias.PhysicalControl)
                || !targetPresent)
            {
                Invalid(path, "An alias must directly target a distinct, present physical control.");
            }
        }

        return;

        void Invalid(string path, string message)
        {
            errors.Add(new GlyphPackageImportError(
                profileId,
                profilePath,
                GlyphPackageImportCode.ProfileManifestInvalid,
                $"{path}: {message}"));
        }
    }

    private static void ValidateAssetShape(
        GlyphAssetEntry asset,
        string path,
        Action<string, string> invalid)
    {
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (asset.Format)
        {
            case GlyphAssetFormat.Svg:
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
            case GlyphAssetFormat.Png:
            {
                if (asset.ViewBox is not null || asset is not { PixelWidth: > 0, PixelHeight: > 0 })
                {
                    invalid(path, "PNG artwork requires positive pixel dimensions and no view box.");
                    return;
                }

                if (asset is
                    {
                        PixelWidth: <= GlyphProfileLimits.MaxDimension, PixelHeight: <= GlyphProfileLimits.MaxDimension
                    }
                    && (long)asset.PixelWidth.Value * asset.PixelHeight.Value
                    <= GlyphProfileLimits.MaxRasterPixels)
                {
                    return;
                }

                invalid(path, "PNG dimensions exceed the axis or decoded-pixel budget.");
                break;
            }
        }
    }

    private static void ValidateImageReference(
        string? assetId,
        GlyphAssetRole expectedRole,
        string path,
        Dictionary<string, GlyphAssetEntry> assets,
        Action<string, string> invalid)
    {
        if (assetId is null)
        {
            return;
        }

        if (!IsIdentifier(assetId)
            || !assets.TryGetValue(assetId, out var asset)
            || asset.Role != expectedRole)
        {
            invalid(path, $"The image must resolve to a {expectedRole} asset.");
        }
    }

    private static void ValidateNotice(
        string profileId,
        string noticePath,
        IGlyphPackageSource source,
        List<GlyphPackageImportError> errors)
    {
        if (!source.TryRead(noticePath, GlyphProfileLimits.MaxNoticeBytes, out var supplied)
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

    private static GlyphProfileManifest OrderManifest(GlyphProfileManifest manifest)
    {
        return manifest with
        {
            ExactDeviceIds = [.. (manifest.ExactDeviceIds ?? []).Order(StringComparer.Ordinal)],
            Assets = [.. (manifest.Assets ?? []).OrderBy(asset => asset.AssetId, StringComparer.Ordinal)],
            Controls = [.. (manifest.Controls ?? []).OrderBy(control => control.Control)],
            Aliases =
            [
                .. (manifest.Aliases ?? [])
                .OrderBy(alias => alias.LogicalControl)
                .ThenBy(alias => alias.PhysicalControl)
            ]
        };
    }

    private static bool IsIdentifier(string? value)
    {
        return PlainText.IsIdentifier(value, GlyphProfileLimits.MaxIdentifierLength);
    }

    private static bool IsDisplayText(string? value, int maximumLength)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Length <= maximumLength
               && value.All(character => !char.IsControl(character));
    }

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

        var segments = value.Split('/');
        return segments.All(segment => segment is not "." and not ".."
                                       && PlainText.IsIdentifier(segment, MaxNoticePathLength));
    }

    private static bool IsPlainUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Length > 0 && text.All(character =>
                character is '\r' or '\n' or '\t' || !char.IsControl(character));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
