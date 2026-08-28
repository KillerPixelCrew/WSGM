using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WSGM.Device.Contracts.Glyphs;

namespace WSGM.Device.Sdk.Authoring;

/// <summary>A deterministic authoring result ready for Device Lab packaging.</summary>
public sealed record AuthoredGlyphProfile
{
    /// <summary>Canonical validated profile metadata.</summary>
    public required GlyphProfileManifest Manifest { get; init; }

    /// <summary>Exact source bytes keyed only by lowercase SHA-256.</summary>
    public required IReadOnlyDictionary<string, byte[]> Assets { get; init; }

    /// <summary>Canonical UTF-8 profile JSON suitable for the package lock.</summary>
    public required byte[] CanonicalManifestUtf8 { get; init; }

    /// <summary>AOT-safe normalized outputs generated from the exact source assets.</summary>
    public required IReadOnlyDictionary<string, ImportedGlyphAsset> GeneratedAssets { get; init; }
}

/// <summary>Makes hash-pinned, validated glyph-profile authoring the default SDK path.</summary>
/// <remarks>
/// This helper owns no path, network, Steam selector, CSS, script, or injection API. Device Lab
/// decides the fixed package layout after this builder has reduced artwork to content hashes.
/// </remarks>
public sealed class GlyphProfileBuilder
{
    private readonly string _profileId;
    private readonly string _displayName;
    private readonly int _revision;
    private readonly GlyphProfileProvenance _provenance;
    private readonly Dictionary<string, byte[]> _assetBytes = new(StringComparer.Ordinal);
    private readonly List<GlyphAssetLockEntry> _assets = [];
    private readonly List<GlyphControlMapping> _controls = [];
    private readonly List<GlyphControlAlias> _aliases = [];
    private readonly List<string> _exactDeviceIds = [];
    private GlyphControllerImages _controllerImages = new();
    private GlyphProfileVerification _verification = GlyphProfileVerification.Unverified;

    /// <summary>Creates an unverified profile builder.</summary>
    /// <param name="profileId">Stable package-scoped profile identifier.</param>
    /// <param name="displayName">Human-readable profile name.</param>
    /// <param name="revision">Positive package-authored revision.</param>
    /// <param name="provenance">Reviewed profile-level source and licensing identity.</param>
    public GlyphProfileBuilder(
        string profileId,
        string displayName,
        int revision,
        GlyphProfileProvenance provenance)
    {
        _profileId = profileId;
        _displayName = displayName;
        _revision = revision;
        _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    /// <summary>Adds exact source bytes and returns their only valid asset reference.</summary>
    /// <param name="bytes">Exact reviewed source asset.</param>
    /// <param name="format">Declared media type.</param>
    /// <param name="role">Reviewed semantic use.</param>
    /// <param name="provenance">Per-asset source and license chain.</param>
    /// <param name="viewBox">Reviewed SVG view box.</param>
    /// <param name="pixelWidth">Reviewed PNG width.</param>
    /// <param name="pixelHeight">Reviewed PNG height.</param>
    /// <returns>Canonical lowercase SHA-256 used by control and image mappings.</returns>
    public string AddAsset(
        ReadOnlySpan<byte> bytes,
        GlyphAssetFormat format,
        GlyphAssetRole role,
        GlyphProfileProvenance provenance,
        GlyphViewBox? viewBox = null,
        int? pixelWidth = null,
        int? pixelHeight = null)
    {
        if (bytes.IsEmpty || bytes.Length > GlyphProfileLimits.MaxAssetBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes),
                $"Asset size must be between 1 and {GlyphProfileLimits.MaxAssetBytes} bytes.");
        }
        ArgumentNullException.ThrowIfNull(provenance);

        byte[] ownedBytes = bytes.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(ownedBytes)).ToLowerInvariant();
        if (_assetBytes.ContainsKey(hash))
        {
            throw new InvalidOperationException("The same asset bytes were added more than once.");
        }

        GlyphAssetLockEntry entry = new()
        {
            Sha256 = hash,
            Format = format,
            ByteCount = ownedBytes.Length,
            Role = role,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            ViewBox = viewBox,
            Conversion = format is GlyphAssetFormat.Svg
                ? GlyphConversionKind.NormalizedVector
                : GlyphConversionKind.ReviewedRaster,
            ImporterVersion = GlyphProfileImporter.CurrentImporterVersion,
            Provenance = provenance,
        };
        _assetBytes.Add(hash, ownedBytes);
        _assets.Add(entry);
        return hash;
    }

    /// <summary>Adds an explicit physical control declaration.</summary>
    /// <param name="control">Semantic control.</param>
    /// <param name="presence">Exact physical presence.</param>
    /// <param name="side">Physical side.</param>
    /// <param name="physicalLabel">Bounded plain-text device label.</param>
    /// <param name="assetSha256">Reviewed control-art hash, or null for generic fallback.</param>
    /// <returns>This builder.</returns>
    public GlyphProfileBuilder AddControl(
        GlyphControlId control,
        GlyphControlPresence presence,
        GlyphControlSide side = GlyphControlSide.None,
        string? physicalLabel = null,
        string? assetSha256 = null)
    {
        _controls.Add(new GlyphControlMapping
        {
            Control = control,
            Presence = presence,
            Side = side,
            PhysicalLabel = physicalLabel,
            AssetSha256 = assetSha256,
        });
        return this;
    }

    /// <summary>Adds one direct logical-to-physical presentation alias.</summary>
    /// <param name="logicalControl">Logical surface control.</param>
    /// <param name="physicalControl">Distinct present physical control.</param>
    /// <returns>This builder.</returns>
    public GlyphProfileBuilder AddAlias(
        GlyphControlId logicalControl,
        GlyphControlId physicalControl)
    {
        _aliases.Add(new GlyphControlAlias
        {
            LogicalControl = logicalControl,
            PhysicalControl = physicalControl,
        });
        return this;
    }

    /// <summary>Sets reviewed controller-level image hashes.</summary>
    /// <param name="fullSha256">Full-controller image hash.</param>
    /// <param name="leftSha256">Left-controller image hash.</param>
    /// <param name="rightSha256">Right-controller image hash.</param>
    /// <returns>This builder.</returns>
    public GlyphProfileBuilder SetControllerImages(
        string? fullSha256,
        string? leftSha256,
        string? rightSha256)
    {
        _controllerImages = new GlyphControllerImages
        {
            FullSha256 = fullSha256,
            LeftSha256 = leftSha256,
            RightSha256 = rightSha256,
        };
        return this;
    }

    /// <summary>Marks artwork reviewed but not exact-device verified.</summary>
    /// <returns>This builder.</returns>
    public GlyphProfileBuilder MarkReviewed()
    {
        _verification = GlyphProfileVerification.Reviewed;
        _exactDeviceIds.Clear();
        return this;
    }

    /// <summary>Records exact devices whose physical artwork comparison was accepted.</summary>
    /// <param name="deviceIds">Exact package device-definition IDs.</param>
    /// <returns>This builder.</returns>
    public GlyphProfileBuilder MarkExactDeviceVerified(params string[] deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        _verification = GlyphProfileVerification.ExactDeviceVerified;
        _exactDeviceIds.Clear();
        _exactDeviceIds.AddRange(deviceIds);
        return this;
    }

    /// <summary>Validates and canonicalizes the complete profile and every source asset.</summary>
    /// <returns>A deterministic package-authoring result.</returns>
    /// <exception cref="InvalidDataException">Metadata or source assets fail any safety rule.</exception>
    public AuthoredGlyphProfile Build()
    {
        GlyphProfileManifest manifest = new()
        {
            SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
            ProfileId = _profileId,
            DisplayName = _displayName,
            Revision = _revision,
            Verification = _verification,
            ExactDeviceIds = _exactDeviceIds.ToArray(),
            Provenance = _provenance,
            Assets = _assets.ToArray(),
            ControllerImages = _controllerImages,
            Controls = _controls.ToArray(),
            Aliases = _aliases.ToArray(),
        };

        GlyphProfileImportResult imported = GlyphProfileImporter.Import(
            manifest,
            new MemoryAssetSource(_assetBytes));
        if (!imported.IsValid)
        {
            throw new InvalidDataException(string.Join(
                "; ",
                imported.Errors.Select(error => $"{error.Sha256}: {error.Code} {error.Message}")));
        }

        ImportedGlyphProfile generated = imported.Profile!;
        GlyphProfileManifest canonical = generated.Manifest;
        return new AuthoredGlyphProfile
        {
            Manifest = canonical,
            Assets = _assetBytes
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.Ordinal),
            CanonicalManifestUtf8 = GlyphProfileReader.ToCanonicalUtf8(canonical),
            GeneratedAssets = generated.Assets,
        };
    }

    private sealed class MemoryAssetSource(IReadOnlyDictionary<string, byte[]> assets)
        : IGlyphAssetSource
    {
        public bool TryRead(string sha256, int maximumBytes, out byte[] bytes)
        {
            if (assets.TryGetValue(sha256, out byte[]? stored) && stored.Length <= maximumBytes)
            {
                bytes = stored.ToArray();
                return true;
            }
            bytes = [];
            return false;
        }
    }
}
