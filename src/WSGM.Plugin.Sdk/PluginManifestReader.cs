using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WSGM.Device.Sdk.Capabilities;
using ManifestLimits = WSGM.Device.Sdk.Packaging.ManifestLimits;
using ManifestRules = WSGM.Device.Sdk.Packaging.ManifestRules;

namespace WSGM.Plugin.Sdk;

/// <summary>Deterministic manifest admission before any plugin code is loaded.</summary>
public static class PluginManifestReader
{
    // Generated init-only object construction assigns zero/null to omitted optional members.
    // Reflection metadata preserves the manifest's declared defaults without making it mutable.
    private static readonly JsonSerializerOptions ReadOptions = new(PluginJsonContext.Default.Options)
    {
        MaxDepth = ManifestLimits.MaxDepth,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    ///     Reads strict camel-case JSON and checks identity, paths, API range, dependencies, display adapters
    ///     and capabilities. Display adapter vendor ids come back uppercase.
    /// </summary>
    /// <param name="json">UTF-8 manifest bytes.</param>
    /// <param name="manifest">Validated metadata on true; null on every false result.</param>
    /// <param name="errors">Empty on success; human-readable rejection reasons on failure.</param>
    /// <returns>True for compatible metadata; false for malformed, oversized, unknown-member or invalid JSON.</returns>
    /// <remarks>
    ///     Applies <see cref="ManifestLimits.MaxDocumentBytes" /> and <see cref="ManifestLimits.MaxDepth" />.
    ///     Does not inspect package files, load assemblies, resolve dependencies or establish code trust.
    /// </remarks>
    public static bool TryRead(ReadOnlySpan<byte> json, out PluginManifest? manifest, out IReadOnlyList<string> errors)
    {
        manifest = null;
        if (json.Length > ManifestLimits.MaxDocumentBytes)
        {
            errors = [$"Manifest is above the {ManifestLimits.MaxDocumentBytes}-byte limit."];
            return false;
        }

        if (json.Length == 0)
        {
            errors = ["The manifest is empty."];
            return false;
        }

        try
        {
            var candidate = JsonSerializer.Deserialize<PluginManifest>(json, ReadOptions);
            errors = Validate(candidate);
            if (candidate is null || errors.Count != 0)
            {
                return false;
            }

            manifest = candidate with
            {
                FrontendModules = Array.AsReadOnly(candidate.FrontendModules.ToArray()),
                DisplayAdapters =
                [
                    .. candidate.DisplayAdapters.Select(adapter =>
                        new DisplayAdapterMatch(adapter.PciVendorId.ToUpperInvariant()))
                ]
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            errors = ["Manifest JSON is malformed or contains unknown members."];
            return false;
        }
    }

    /// <summary>Validates metadata without loading code or inspecting the filesystem.</summary>
    /// <param name="manifest">Candidate metadata.</param>
    /// <returns>Validation errors, or an empty collection.</returns>
    public static IReadOnlyList<string> Validate(PluginManifest? manifest)
    {
        if (manifest is null)
        {
            return ["Manifest is absent."];
        }

        List<string> errors = [];
        if (!Identifier(manifest.Id))
        {
            errors.Add("Invalid plugin identity.");
        }

        if (!Identifier(manifest.Category))
        {
            errors.Add("Invalid category identity.");
        }

        if (!ManifestRules.TryValidateName(manifest.Name, out _))
        {
            errors.Add("Invalid display name.");
        }

        if (!ManifestRules.IsCanonicalVersion(manifest.Version))
        {
            errors.Add("The package version must be a canonical dotted numeric version.");
        }

        if (manifest.MinimumApiVersion < 1 || manifest.MaximumApiVersion < manifest.MinimumApiVersion
                                           || PluginApi.Version < manifest.MinimumApiVersion ||
                                           PluginApi.Version > manifest.MaximumApiVersion)
        {
            errors.Add("Incompatible common Plugin SDK version range.");
        }

        if (!ManifestRules.IsRootAssemblyFileName(manifest.EntryAssembly))
        {
            errors.Add("Entry assembly must be a DLL filename at the package root.");
        }

        if (!ManifestRules.IsEntryTypeName(manifest.EntryType))
        {
            errors.Add("Invalid entry type.");
        }

        if (manifest.Dependencies is null)
        {
            errors.Add("Invalid dependency list.");
        }
        else
        {
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (var dependency in manifest.Dependencies)
            {
                if (dependency is not null && Identifier(dependency.Id) && dependency.Id != manifest.Id &&
                    ids.Add(dependency.Id)
                    && Version.TryParse(dependency.MinimumVersion, out var minimum)
                    && (dependency.MaximumVersionExclusive is not { } upper
                        || (Version.TryParse(upper, out var maximum) && maximum > minimum)))
                {
                    continue;
                }

                errors.Add("Invalid, duplicate or self-referencing dependency.");
                break;
            }
        }

        if (manifest.Permissions is null
            || manifest.Permissions.Any(permission => !Identifier(permission))
            || manifest.Permissions.Distinct(StringComparer.Ordinal).Count() != manifest.Permissions.Count)
        {
            errors.Add("Invalid permission declarations.");
        }

        if (manifest.WsgmVersion is { } wsgmVersion && !ManifestRules.IsCanonicalVersion(wsgmVersion))
        {
            errors.Add("The WSGM version must be a canonical dotted numeric version.");
        }

        ValidateCapabilityDeclarations(manifest, errors);
        if (manifest.FrontendModules is null
            || manifest.FrontendModules.Any(module => module is null || !Identifier(module.Id)
                                                                     || !FrontendPath(module.Script, ".js")
                                                                     || (module.Style is not null &&
                                                                         !FrontendPath(module.Style, ".css")))
            || manifest.FrontendModules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Count()
            != manifest.FrontendModules.Count
            || (manifest.FrontendModules.Count > 0 && !manifest.SteamCef))
        {
            errors.Add(
                "Frontend modules require Steam CEF opt-in, unique identities and package-relative JS/CSS files.");
        }

        return errors;
    }

    /// <summary>
    ///     A <c>wsgm.gpu</c> package must name the display adapters it serves and the roles it publishes.
    ///     Only that category publishes capabilities today, so every other category leaves both lists
    ///     empty. The controller, motion, haptic and OEM roles belong to the device package alone.
    /// </summary>
    private static void ValidateCapabilityDeclarations(PluginManifest manifest, List<string> errors)
    {
        var gpu = manifest.Category == PluginCategories.Gpu;
        var adapters = manifest.DisplayAdapters;
        if (adapters is null
            || adapters.Any(adapter => adapter is null || !PciId(adapter.PciVendorId))
            || adapters.Select(adapter => adapter.PciVendorId.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal).Count() != adapters.Count)
        {
            errors.Add("Display adapters must be distinct four-digit hexadecimal PCI vendor ids.");
        }
        else if (gpu && adapters.Count == 0)
        {
            errors.Add("A graphics package must declare at least one display adapter.");
        }
        else if (!gpu && adapters.Count != 0)
        {
            errors.Add("Only a graphics package declares display adapters.");
        }

        var roles = manifest.Capabilities;
        if (roles is null
            || roles.Any(role => !Enum.IsDefined(role) || DeviceOnly(role))
            || roles.Distinct().Count() != roles.Count)
        {
            errors.Add("Capabilities must be distinct roles a common plugin may publish.");
        }
        else if (gpu && roles.Count == 0)
        {
            errors.Add("A graphics package must declare at least one capability.");
        }
        else if (!gpu && roles.Count != 0)
        {
            errors.Add("Only a graphics package declares capabilities.");
        }
    }

    private static bool DeviceOnly(CapabilityRole role)
    {
        return role is CapabilityRole.ControllerSource or CapabilityRole.MotionSource or CapabilityRole.HapticSink
            or CapabilityRole.OemControl;
    }

    private static bool PciId(string? value)
    {
        return value is { Length: 4 } && value.All(char.IsAsciiHexDigit);
    }

    private static bool Identifier(string? value)
    {
        return ManifestRules.IsPackageIdentifier(value);
    }

    private static bool FrontendPath(string? value, string extension)
    {
        return value is { Length: > 0 } && value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                                        && !value.StartsWith('/') && !value.Contains('\\') && !value.Contains(':')
                                        && value.Split('/').All(part =>
                                            part.Length > 0 && part is not "." and not "..");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginManifest))]
internal partial class PluginJsonContext : JsonSerializerContext;
