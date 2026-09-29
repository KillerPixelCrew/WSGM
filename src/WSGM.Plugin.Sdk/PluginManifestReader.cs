using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Plugin.Sdk;

/// <summary>Bounded, deterministic manifest admission before any plugin code is loaded.</summary>
public static class PluginManifestReader
{
    /// <summary>Most display adapter rules one manifest may declare.</summary>
    public const int MaximumDisplayAdapters = 16;

    /// <summary>Most capability roles one manifest may declare.</summary>
    public const int MaximumCapabilities = 32;

    /// <summary>
    ///     Reads strict camel-case JSON and checks identity, paths, API range, dependencies, display adapters
    ///     and capabilities. Display adapter vendor ids come back uppercase.
    /// </summary>
    /// <param name="json">UTF-8 manifest bytes.</param>
    /// <param name="manifest">Validated manifest, or null.</param>
    /// <param name="errors">Bounded reasons for rejection.</param>
    /// <returns>Whether metadata is admissible; this does not establish code trust.</returns>
    public static bool TryRead(ReadOnlySpan<byte> json, out PluginManifest? manifest, out IReadOnlyList<string> errors)
    {
        manifest = null;
        if (json.Length == 0)
        {
            errors = ["The manifest is empty."];
            return false;
        }

        try
        {
            var candidate = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
            errors = Validate(candidate);
            if (candidate is null || errors.Count != 0)
            {
                return false;
            }

            manifest = candidate with
            {
                DisplayAdapters =
                [
                    .. candidate.DisplayAdapters.Select(adapter =>
                        new DisplayAdapterMatch(adapter.PciVendorId.ToUpperInvariant()))
                ]
            };
            return true;
        }
        catch (JsonException)
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

        if (!PluginText.TryValidate(manifest.Name, 128, "display name", out _))
        {
            errors.Add("Invalid display name.");
        }

        if (!Version.TryParse(manifest.Version, out _))
        {
            errors.Add("Invalid numeric package version.");
        }

        if (manifest.MinimumApiVersion < 1 || manifest.MaximumApiVersion < manifest.MinimumApiVersion
                                           || PluginApi.Version < manifest.MinimumApiVersion ||
                                           PluginApi.Version > manifest.MaximumApiVersion)
        {
            errors.Add("Incompatible common Plugin SDK version range.");
        }

        if (string.IsNullOrEmpty(manifest.EntryAssembly)
            || !manifest.EntryAssembly.EndsWith(".dll",
                StringComparison.OrdinalIgnoreCase)
            || manifest.EntryAssembly.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '_' or '-'))
            || manifest.EntryAssembly.StartsWith('.'))
        {
            errors.Add("Entry assembly must be a DLL filename at the package root.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType)
            || manifest.EntryType.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '_' or '+')))
        {
            errors.Add("Invalid entry type.");
        }

        if (manifest.Dependencies is null || manifest.Dependencies.Count > 32)
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

        if (manifest.Permissions is null || manifest.Permissions.Count > 32 ||
            manifest.Permissions.Any(permission => !Identifier(permission))
            || manifest.Permissions.Distinct(StringComparer.Ordinal).Count() != manifest.Permissions.Count)
        {
            errors.Add("Invalid permission declarations.");
        }

        if (manifest.WsgmVersion is { } wsgmVersion
            && (!Version.TryParse(wsgmVersion, out var parsed)
                || parsed.ToString(parsed.Revision >= 0 ? 4 : parsed.Build >= 0 ? 3 : 2) != wsgmVersion))
        {
            errors.Add("The WSGM version must be a canonical dotted numeric version.");
        }

        ValidateCapabilityDeclarations(manifest, errors);
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
        if (adapters is null || adapters.Count > MaximumDisplayAdapters
                             || adapters.Any(adapter => adapter is null || !PciId(adapter.PciVendorId))
                             || adapters.Select(adapter => adapter.PciVendorId.ToUpperInvariant())
                                 .Distinct(StringComparer.Ordinal).Count() != adapters.Count)
        {
            errors.Add(
                $"Display adapters must be at most {MaximumDisplayAdapters} distinct four-digit hexadecimal PCI vendor ids.");
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
        if (roles is null || roles.Count > MaximumCapabilities
                          || roles.Any(role => !Enum.IsDefined(role) || DeviceOnly(role))
                          || roles.Distinct().Count() != roles.Count)
        {
            errors.Add(
                $"Capabilities must be at most {MaximumCapabilities} distinct roles a common plugin may publish.");
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
        return value is { Length: > 0 and <= 128 }
               && char.IsAsciiLetterOrDigit(value[0]) && value.All(character =>
                   character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginManifest))]
internal partial class PluginJsonContext : JsonSerializerContext;
