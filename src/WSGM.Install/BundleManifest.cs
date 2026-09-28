using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;

namespace WSGM.Install;

/// <summary>One plugin a WSGM release bundles, as <c>eng/build-bundle.ps1</c> describes it.</summary>
/// <remarks>
///     The lists read as empty when a bundle.json written before they existed leaves them out: the
///     source-generated reader sets an absent init-only property to null rather than keeping its
///     initializer, and a 2.0.3 bundle has no <c>testedHardware</c> or <c>replaces</c>, which is
///     what closed Settings on 2026-09-28.
/// </remarks>
public sealed record BundledPlugin
{
    /// <summary>The device category, as bundle.json writes it for a device package.</summary>
    public const string DeviceCategory = "wsgm.device";

    /// <summary>Plugin id.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Plugin version.</summary>
    public required string Version { get; init; }

    /// <summary><see cref="DeviceCategory" /> for a device package, otherwise the common category.</summary>
    public required string Category { get; init; }

    /// <summary><c>first-party</c> or <c>community</c>, set by the maintainer.</summary>
    public required string Origin { get; init; }

    /// <summary><c>hardware-tested</c> or <c>blind</c>, set by the maintainer.</summary>
    public required string Validation { get; init; }

    /// <summary>
    ///     The baseboard products a hardware-tested device package was tested on. Empty means every
    ///     machine its rules match; a package that covers untested models lists the tested ones.
    /// </summary>
    public IReadOnlyList<string> TestedHardware { get => field ?? []; init; } = [];

    /// <summary>Developer contact for a community plugin.</summary>
    public string? Contact { get; init; }

    /// <summary>Retired ids this plugin took over. Setup removes their packages when it installs this one.</summary>
    public IReadOnlyList<string> Replaces { get => field ?? []; init; } = [];

    /// <summary>The hardware rules of a device package.</summary>
    public IReadOnlyList<HardwareMatchRule> Hardware { get => field ?? []; init; } = [];

    /// <summary>The capability roles a device package declares.</summary>
    public IReadOnlyList<CapabilityRole> Capabilities { get => field ?? []; init; } = [];

    /// <summary>Package file name.</summary>
    public required string File { get; init; }

    /// <summary>Package size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Lowercase SHA-256 of the package file.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Whether this is the device-category package.</summary>
    [JsonIgnore]
    public bool IsDevice => Category == DeviceCategory;

    /// <summary>Whether the maintainer tested it on hardware, on at least one machine.</summary>
    [JsonIgnore]
    public bool HardwareTested => Validation == "hardware-tested";

    /// <summary>Whether the maintainer tested it on this machine's hardware.</summary>
    /// <param name="identity">The machine, or null to answer for the package as a whole.</param>
    /// <returns>True when tested and either unrestricted or tested on this baseboard.</returns>
    public bool HardwareTestedOn(DeviceIdentitySnapshot? identity)
    {
        return HardwareTested
               && (identity is null
                   || TestedHardware.Count == 0
                   || TestedHardware.Contains(identity.BaseboardProduct?.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether it is a reviewed third-party plugin.</summary>
    [JsonIgnore]
    public bool Community => Origin == "community";
}

/// <summary>A community plugin whose pinned commit did not build for this release.</summary>
public sealed record OutdatedPlugin
{
    /// <summary>Plugin id.</summary>
    public required string Id { get; init; }

    /// <summary>Developer contact.</summary>
    public string? Contact { get; init; }

    /// <summary>Where the failed build can be read.</summary>
    public string? Log { get; init; }
}

/// <summary>What a WSGM release bundles: <c>bundle.json</c>.</summary>
public sealed record BundleManifest
{
    /// <summary>Largest accepted manifest.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The only schema this build reads.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>The WSGM release the bundle belongs to.</summary>
    public required string WsgmVersion { get; init; }

    /// <summary>Bundled plugins.</summary>
    public IReadOnlyList<BundledPlugin> Plugins { get; init; } = [];

    /// <summary>Community plugins left out because they no longer build.</summary>
    public IReadOnlyList<OutdatedPlugin> Outdated { get; init; } = [];

    /// <summary>Finds the bundled plugin whose package file has this hash.</summary>
    /// <param name="sha256">Lowercase or uppercase hex SHA-256.</param>
    /// <returns>The plugin, or null when the file is not one this bundle shipped.</returns>
    public BundledPlugin? ByHash(string sha256)
    {
        return Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses a bundle manifest.</summary>
    /// <param name="utf8Json">The document.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">The document is too large, malformed or of another schema.</exception>
    public static BundleManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaxBytes)
        {
            throw new InvalidDataException("bundle.json is empty or too large.");
        }

        BundleManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(utf8Json, BundleJsonContext.Default.BundleManifest);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("bundle.json is malformed: " + ex.Message, ex);
        }

        return manifest switch
        {
            null => throw new InvalidDataException("bundle.json is empty."),
            { SchemaVersion: not CurrentSchema } => throw new InvalidDataException(
                $"bundle.json uses schema {manifest.SchemaVersion}; this build reads {CurrentSchema}."),
            _ => manifest
        };
    }

    /// <summary>Reads a bundle manifest file, or returns null when it does not exist.</summary>
    /// <param name="path">File path.</param>
    /// <returns>The manifest, or null.</returns>
    public static BundleManifest? TryRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes)
        {
            throw new InvalidDataException("bundle.json is too large.");
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    /// <summary>Serializes the manifest, for recording the installed bundle.</summary>
    /// <returns>UTF-8 JSON.</returns>
    public byte[] ToUtf8Json()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this, BundleJsonContext.Default.BundleManifest);
    }
}

/// <summary>Source-generated JSON metadata for bundle.json.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(BundleManifest))]
internal sealed partial class BundleJsonContext : JsonSerializerContext;
