using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WSGM.Device.Contracts.Glyphs;

/// <summary>Stable reason a physical glyph profile was rejected.</summary>
public enum GlyphProfileValidationCode
{
    /// <summary>The profile document exceeded its byte budget.</summary>
    DocumentTooLarge,
    /// <summary>The profile document was malformed.</summary>
    MalformedDocument,
    /// <summary>The profile document included a member this schema does not define.</summary>
    UnknownMember,
    /// <summary>The schema version is not supported.</summary>
    UnsupportedSchemaVersion,
    /// <summary>A required value was absent.</summary>
    MissingField,
    /// <summary>An enum carried an undefined numeric value.</summary>
    InvalidEnum,
    /// <summary>A stable identifier was malformed or path-like.</summary>
    InvalidIdentifier,
    /// <summary>A bounded value or collection exceeded its limit.</summary>
    LimitExceeded,
    /// <summary>A SHA-256 value was not canonical lowercase hexadecimal.</summary>
    InvalidHash,
    /// <summary>An asset's media type and conversion metadata disagree.</summary>
    InvalidAssetMetadata,
    /// <summary>An identifier or semantic control was declared more than once.</summary>
    DuplicateEntry,
    /// <summary>A content-hash reference does not resolve inside the profile lock.</summary>
    UnresolvedAsset,
    /// <summary>An alias is cyclic, self-referential, or targets an absent control.</summary>
    InvalidAlias,
    /// <summary>A control's presence and artwork declaration conflict.</summary>
    InvalidControl,
    /// <summary>A profile marked exact-device verified named no exact device.</summary>
    MissingVerificationTarget,
}

/// <summary>One profile validation failure.</summary>
/// <param name="Path">Dotted path of the offending value.</param>
/// <param name="Code">Stable failure reason.</param>
/// <param name="Message">Sanitized human-readable detail.</param>
public sealed record GlyphProfileValidationError(
    string Path,
    GlyphProfileValidationCode Code,
    string Message);

/// <summary>Result of parsing and validating a physical glyph profile document.</summary>
/// <param name="Manifest">Parsed manifest, or null when parsing or validation failed.</param>
/// <param name="Errors">Every discovered failure.</param>
public sealed record GlyphProfileReadResult(
    GlyphProfileManifest? Manifest,
    IReadOnlyList<GlyphProfileValidationError> Errors)
{
    /// <summary>Whether a validated manifest is available.</summary>
    public bool IsValid => Manifest is not null && Errors.Count == 0;
}

/// <summary>Parses deterministic, NativeAOT-safe profile JSON from untrusted package bytes.</summary>
public static class GlyphProfileReader
{
    private static readonly GlyphProfileJsonContext ReadContext = new(
        new JsonSerializerOptions(GlyphProfileJsonContext.Default.Options)
        {
            MaxDepth = 12,
        });

    /// <summary>Parses and validates a profile JSON document.</summary>
    /// <param name="utf8Json">Untrusted UTF-8 package bytes.</param>
    /// <returns>A validated profile or its rejection reasons.</returns>
    public static GlyphProfileReadResult Read(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > GlyphProfileLimits.MaxDocumentBytes)
        {
            return Failure(GlyphProfileValidationCode.DocumentTooLarge,
                $"Profile exceeds {GlyphProfileLimits.MaxDocumentBytes} bytes.");
        }

        if (utf8Json.IsEmpty)
        {
            return Failure(GlyphProfileValidationCode.MalformedDocument, "Profile is empty.");
        }

        try
        {
            GlyphProfileManifest? manifest = JsonSerializer.Deserialize(
                utf8Json,
                ReadContext.GlyphProfileManifest);
            if (manifest is null)
            {
                return Failure(GlyphProfileValidationCode.MalformedDocument,
                    "Profile deserialized to null.");
            }

            IReadOnlyList<GlyphProfileValidationError> errors =
                GlyphProfileValidator.Validate(manifest);
            return errors.Count == 0
                ? new GlyphProfileReadResult(manifest, [])
                : new GlyphProfileReadResult(null, errors);
        }
        catch (JsonException ex)
        {
            GlyphProfileValidationCode code = ex.Message.Contains(
                "could not be mapped",
                StringComparison.Ordinal)
                ? GlyphProfileValidationCode.UnknownMember
                : GlyphProfileValidationCode.MalformedDocument;
            return Failure(code, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return Failure(GlyphProfileValidationCode.MalformedDocument, ex.Message);
        }
    }

    /// <summary>Serializes a profile into its canonical, hashable representation.</summary>
    /// <param name="manifest">Profile to canonicalize.</param>
    /// <returns>Deterministic UTF-8 JSON with all set-like collections ordinally sorted.</returns>
    public static byte[] ToCanonicalUtf8(GlyphProfileManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        GlyphProfileManifest canonical = GlyphProfileValidator.Canonicalize(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(
            canonical,
            GlyphProfileJsonContext.Default.GlyphProfileManifest);
    }

    private static GlyphProfileReadResult Failure(
        GlyphProfileValidationCode code,
        string message) => new(null, [new GlyphProfileValidationError("", code, message)]);
}

/// <summary>Pure semantic validator for plugin-owned glyph metadata.</summary>
public static class GlyphProfileValidator
{
    /// <summary>Returns all profile rule violations in deterministic document order.</summary>
    /// <param name="profile">Profile model to validate.</param>
    /// <returns>Every discovered violation.</returns>
    public static IReadOnlyList<GlyphProfileValidationError> Validate(GlyphProfileManifest profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        List<GlyphProfileValidationError> errors = [];

        if (profile.SchemaVersion != GlyphProfileLimits.CurrentSchemaVersion)
        {
            Add(errors, "schemaVersion", GlyphProfileValidationCode.UnsupportedSchemaVersion,
                $"Schema version {profile.SchemaVersion} is not supported.");
        }

        ValidateIdentifier(errors, "profileId", profile.ProfileId);
        ValidateDisplayText(errors, "displayName", profile.DisplayName,
            GlyphProfileLimits.MaxDisplayNameLength);
        if (profile.Revision <= 0)
        {
            Add(errors, "revision", GlyphProfileValidationCode.MissingField,
                "Profile revision must be positive.");
        }
        if (!Enum.IsDefined(profile.Verification))
        {
            Add(errors, "verification", GlyphProfileValidationCode.InvalidEnum,
                "Profile verification value is not defined by this schema.");
        }

        ValidateProvenance(errors, "provenance", profile.Provenance);
        IReadOnlyList<string> exactDeviceIds = profile.ExactDeviceIds ?? [];
        IReadOnlyList<GlyphAssetLockEntry> profileAssets = profile.Assets ?? [];
        IReadOnlyList<GlyphControlMapping> profileControls = profile.Controls ?? [];
        IReadOnlyList<GlyphControlAlias> profileAliases = profile.Aliases ?? [];
        ValidateCount(errors, "exactDeviceIds", exactDeviceIds.Count,
            GlyphProfileLimits.MaxExactDevices);
        HashSet<string> deviceIds = NewIdSet();
        for (int i = 0; i < exactDeviceIds.Count; i++)
        {
            string path = $"exactDeviceIds[{i}]";
            string deviceId = exactDeviceIds[i];
            ValidateIdentifier(errors, path, deviceId);
            if (!deviceIds.Add(deviceId))
            {
                Add(errors, path, GlyphProfileValidationCode.DuplicateEntry,
                    $"Exact device '{deviceId}' appears more than once.");
            }
        }

        if (profile.Verification is GlyphProfileVerification.ExactDeviceVerified
            && exactDeviceIds.Count == 0)
        {
            Add(errors, "exactDeviceIds", GlyphProfileValidationCode.MissingVerificationTarget,
                "An exact-device verified profile must name at least one verified device definition.");
        }

        ValidateCount(errors, "assets", profileAssets.Count, GlyphProfileLimits.MaxAssets);
        Dictionary<string, GlyphAssetLockEntry> assets = new(StringComparer.Ordinal);
        long totalBytes = 0;
        for (int i = 0; i < profileAssets.Count; i++)
        {
            string path = $"assets[{i}]";
            GlyphAssetLockEntry asset = profileAssets[i];
            if (asset is null)
            {
                Add(errors, path, GlyphProfileValidationCode.MissingField,
                    "Asset lock entry is required.");
                continue;
            }
            ValidateHash(errors, $"{path}.sha256", asset.Sha256);
            if (!Enum.IsDefined(asset.Format)
                || !Enum.IsDefined(asset.Role)
                || !Enum.IsDefined(asset.Conversion))
            {
                Add(errors, path, GlyphProfileValidationCode.InvalidEnum,
                    "Asset format, role, or conversion value is undefined.");
            }
            if (IsCanonicalHash(asset.Sha256) && !assets.TryAdd(asset.Sha256, asset))
            {
                Add(errors, $"{path}.sha256", GlyphProfileValidationCode.DuplicateEntry,
                    "The same content hash appears more than once in the asset lock.");
            }

            if (asset.ByteCount is <= 0 or > GlyphProfileLimits.MaxAssetBytes)
            {
                Add(errors, $"{path}.byteCount", GlyphProfileValidationCode.LimitExceeded,
                    $"Asset size must be between 1 and {GlyphProfileLimits.MaxAssetBytes} bytes.");
            }
            else
            {
                totalBytes += asset.ByteCount;
            }

            if (asset.ImporterVersion <= 0)
            {
                Add(errors, $"{path}.importerVersion", GlyphProfileValidationCode.MissingField,
                    "Importer version must be positive.");
            }

            ValidateProvenance(errors, $"{path}.provenance", asset.Provenance);
            ValidateAssetShape(errors, path, asset);
        }

        if (totalBytes > GlyphProfileLimits.MaxProfileBytes)
        {
            Add(errors, "assets", GlyphProfileValidationCode.LimitExceeded,
                $"Aggregate asset size exceeds {GlyphProfileLimits.MaxProfileBytes} bytes.");
        }

        GlyphControllerImages controllerImages = profile.ControllerImages ?? new();
        ValidateImageReference(errors, "controllerImages.fullSha256",
            controllerImages.FullSha256, GlyphAssetRole.FullController, assets);
        ValidateImageReference(errors, "controllerImages.leftSha256",
            controllerImages.LeftSha256, GlyphAssetRole.LeftController, assets);
        ValidateImageReference(errors, "controllerImages.rightSha256",
            controllerImages.RightSha256, GlyphAssetRole.RightController, assets);

        ValidateCount(errors, "controls", profileControls.Count, GlyphProfileLimits.MaxControls);
        Dictionary<GlyphControlId, GlyphControlMapping> controls = [];
        for (int i = 0; i < profileControls.Count; i++)
        {
            string path = $"controls[{i}]";
            GlyphControlMapping control = profileControls[i];
            if (control is null)
            {
                Add(errors, path, GlyphProfileValidationCode.MissingField,
                    "Control mapping is required.");
                continue;
            }
            if (!controls.TryAdd(control.Control, control))
            {
                Add(errors, $"{path}.control", GlyphProfileValidationCode.DuplicateEntry,
                    $"Control '{control.Control}' appears more than once.");
            }
            if (!Enum.IsDefined(control.Control)
                || !Enum.IsDefined(control.Presence)
                || !Enum.IsDefined(control.Side))
            {
                Add(errors, path, GlyphProfileValidationCode.InvalidEnum,
                    "Control, presence, or side value is undefined.");
            }

            if (control.PhysicalLabel is { } label)
            {
                ValidateDisplayText(errors, $"{path}.physicalLabel", label,
                    GlyphProfileLimits.MaxPhysicalLabelLength);
            }

            if (control.Presence is GlyphControlPresence.Absent
                && control.AssetSha256 is not null)
            {
                Add(errors, $"{path}.assetSha256", GlyphProfileValidationCode.InvalidControl,
                    "A physically absent control cannot declare artwork.");
            }

            if (control.AssetSha256 is { } hash)
            {
                ValidateHash(errors, $"{path}.assetSha256", hash);
                if (!assets.TryGetValue(hash, out GlyphAssetLockEntry? asset)
                    || asset.Role is not GlyphAssetRole.Control)
                {
                    Add(errors, $"{path}.assetSha256", GlyphProfileValidationCode.UnresolvedAsset,
                        "Control artwork must resolve to a locked Control asset by hash.");
                }
            }
        }

        ValidateCount(errors, "aliases", profileAliases.Count, GlyphProfileLimits.MaxAliases);
        HashSet<GlyphControlId> aliasSources = profileAliases
            .Where(alias => alias is not null)
            .Select(alias => alias.LogicalControl)
            .ToHashSet();
        HashSet<GlyphControlId> aliases = [];
        for (int i = 0; i < profileAliases.Count; i++)
        {
            string path = $"aliases[{i}]";
            GlyphControlAlias alias = profileAliases[i];
            if (alias is null)
            {
                Add(errors, path, GlyphProfileValidationCode.MissingField,
                    "Control alias is required.");
                continue;
            }
            if (!Enum.IsDefined(alias.LogicalControl) || !Enum.IsDefined(alias.PhysicalControl))
            {
                Add(errors, path, GlyphProfileValidationCode.InvalidEnum,
                    "Alias control value is undefined.");
            }
            if (!aliases.Add(alias.LogicalControl))
            {
                Add(errors, $"{path}.logicalControl", GlyphProfileValidationCode.DuplicateEntry,
                    $"Logical control '{alias.LogicalControl}' is aliased more than once.");
            }

            bool targetPresent = controls.TryGetValue(
                alias.PhysicalControl,
                out GlyphControlMapping? target)
                && target.Presence is GlyphControlPresence.Present;
            if (alias.LogicalControl == alias.PhysicalControl
                || aliasSources.Contains(alias.PhysicalControl)
                || !targetPresent)
            {
                Add(errors, path, GlyphProfileValidationCode.InvalidAlias,
                    "An alias must directly target a distinct, present physical control.");
            }
        }

        return errors;
    }

    /// <summary>Orders every set-like collection for deterministic serialization and import.</summary>
    /// <param name="profile">Profile to canonicalize.</param>
    /// <returns>A copy with ordinally stable collections.</returns>
    public static GlyphProfileManifest Canonicalize(GlyphProfileManifest profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with
        {
            ExactDeviceIds = (profile.ExactDeviceIds ?? [])
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            Assets = (profile.Assets ?? [])
                .OrderBy(asset => asset.Sha256, StringComparer.Ordinal)
                .ToArray(),
            Controls = (profile.Controls ?? [])
                .OrderBy(control => control.Control)
                .ToArray(),
            Aliases = (profile.Aliases ?? [])
                .OrderBy(alias => alias.LogicalControl)
                .ThenBy(alias => alias.PhysicalControl)
                .ToArray(),
        };
    }

    private static void ValidateAssetShape(
        List<GlyphProfileValidationError> errors,
        string path,
        GlyphAssetLockEntry asset)
    {
        if (asset.Format is GlyphAssetFormat.Svg)
        {
            if (asset.Conversion is not GlyphConversionKind.NormalizedVector
                || asset.ViewBox is not { } viewBox
                || asset.PixelWidth is not null
                || asset.PixelHeight is not null)
            {
                Add(errors, path, GlyphProfileValidationCode.InvalidAssetMetadata,
                    "SVG requires NormalizedVector and a view box, with no raster dimensions.");
                return;
            }

            ValidateViewBox(errors, $"{path}.viewBox", viewBox);
            return;
        }

        if (asset.Format is GlyphAssetFormat.Png
            && (asset.Conversion is not GlyphConversionKind.ReviewedRaster
                || asset.ViewBox is not null
                || asset.PixelWidth is not > 0
                || asset.PixelHeight is not > 0))
        {
            Add(errors, path, GlyphProfileValidationCode.InvalidAssetMetadata,
                "PNG requires ReviewedRaster and positive pixel dimensions, with no view box.");
        }

        if (asset.PixelWidth > GlyphProfileLimits.MaxDimension
            || asset.PixelHeight > GlyphProfileLimits.MaxDimension
            || (asset.PixelWidth is int width
                && asset.PixelHeight is int height
                && (long)width * height > GlyphProfileLimits.MaxRasterPixels))
        {
            Add(errors, path, GlyphProfileValidationCode.LimitExceeded,
                "Raster dimensions exceed the per-axis or decoded-pixel budget.");
        }
    }

    private static void ValidateViewBox(
        List<GlyphProfileValidationError> errors,
        string path,
        GlyphViewBox viewBox)
    {
        if (viewBox.Width <= 0 || viewBox.Height <= 0
            || viewBox.Width > GlyphProfileLimits.MaxDimension
            || viewBox.Height > GlyphProfileLimits.MaxDimension
            || viewBox.X < -GlyphProfileLimits.MaxDimension
            || viewBox.X > GlyphProfileLimits.MaxDimension
            || viewBox.Y < -GlyphProfileLimits.MaxDimension
            || viewBox.Y > GlyphProfileLimits.MaxDimension)
        {
            Add(errors, path, GlyphProfileValidationCode.LimitExceeded,
                "SVG view box is empty or exceeds the coordinate budget.");
        }
    }

    private static void ValidateImageReference(
        List<GlyphProfileValidationError> errors,
        string path,
        string? hash,
        GlyphAssetRole role,
        IReadOnlyDictionary<string, GlyphAssetLockEntry> assets)
    {
        if (hash is null)
        {
            return;
        }

        ValidateHash(errors, path, hash);
        if (!assets.TryGetValue(hash, out GlyphAssetLockEntry? asset) || asset.Role != role)
        {
            Add(errors, path, GlyphProfileValidationCode.UnresolvedAsset,
                $"Image must resolve to a locked {role} asset by hash.");
        }
    }

    private static void ValidateProvenance(
        List<GlyphProfileValidationError> errors,
        string path,
        GlyphProfileProvenance provenance)
    {
        if (provenance is null)
        {
            Add(errors, path, GlyphProfileValidationCode.MissingField, "Provenance is required.");
            return;
        }

        ValidateIdentifier(errors, $"{path}.sourceId", provenance.SourceId);
        ValidateIdentifier(errors, $"{path}.sourceRevision", provenance.SourceRevision);
        ValidateDisplayText(errors, $"{path}.license", provenance.License, 128);
        ValidateHash(errors, $"{path}.licenseNoticeSha256", provenance.LicenseNoticeSha256);
    }

    private static void ValidateIdentifier(
        List<GlyphProfileValidationError> errors,
        string path,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, GlyphProfileValidationCode.MissingField, "Identifier is required.");
            return;
        }

        if (value.Length > GlyphProfileLimits.MaxIdentifierLength)
        {
            Add(errors, path, GlyphProfileValidationCode.LimitExceeded,
                $"Identifier exceeds {GlyphProfileLimits.MaxIdentifierLength} characters.");
            return;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_')
            {
                Add(errors, path, GlyphProfileValidationCode.InvalidIdentifier,
                    "Identifier may contain only ASCII letters, digits, dots, hyphens, and underscores.");
                return;
            }
        }
    }

    private static void ValidateDisplayText(
        List<GlyphProfileValidationError> errors,
        string path,
        string value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, GlyphProfileValidationCode.MissingField, "Text is required.");
            return;
        }

        if (value.Length > maximumLength)
        {
            Add(errors, path, GlyphProfileValidationCode.LimitExceeded,
                $"Text exceeds {maximumLength} characters.");
            return;
        }

        if (value.Any(char.IsControl))
        {
            Add(errors, path, GlyphProfileValidationCode.InvalidIdentifier,
                "Text contains a control character.");
        }
    }

    private static void ValidateHash(
        List<GlyphProfileValidationError> errors,
        string path,
        string value)
    {
        if (!IsCanonicalHash(value))
        {
            Add(errors, path, GlyphProfileValidationCode.InvalidHash,
                "SHA-256 must be exactly 64 lowercase hexadecimal characters.");
        }
    }

    private static bool IsCanonicalHash(string value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!((character is >= '0' and <= '9') || (character is >= 'a' and <= 'f')))
            {
                return false;
            }
        }
        return true;
    }

    private static void ValidateCount(
        List<GlyphProfileValidationError> errors,
        string path,
        int count,
        int maximum)
    {
        if (count > maximum)
        {
            Add(errors, path, GlyphProfileValidationCode.LimitExceeded,
                $"{count} entries exceeds the limit of {maximum}.");
        }
    }

    private static HashSet<string> NewIdSet() => new(StringComparer.Ordinal);

    private static void Add(
        List<GlyphProfileValidationError> errors,
        string path,
        GlyphProfileValidationCode code,
        string message) => errors.Add(new GlyphProfileValidationError(path, code, message));
}
