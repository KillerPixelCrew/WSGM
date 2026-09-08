using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.Plugin.Sdk;

/// <summary>Bounded, deterministic manifest admission before any plugin code is loaded.</summary>
public static class PluginManifestReader
{
    /// <summary>Maximum encoded manifest size.</summary>
    public const int MaximumBytes = 65536;

    /// <summary>Reads strict camel-case JSON and checks identity, paths, API range and dependencies.</summary>
    /// <param name="json">UTF-8 manifest bytes.</param>
    /// <param name="manifest">Validated manifest, or null.</param>
    /// <param name="errors">Bounded reasons for rejection.</param>
    /// <returns>Whether metadata is admissible; this does not establish code trust.</returns>
    public static bool TryRead(ReadOnlySpan<byte> json, out PluginManifest? manifest, out IReadOnlyList<string> errors)
    {
        manifest = null;
        if (json.Length is 0 or > MaximumBytes) { errors = ["Manifest size is outside the supported bounds."]; return false; }
        try
        {
            var candidate = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
            errors = Validate(candidate);
            if (errors.Count != 0) { return false; }
            manifest = candidate;
            return true;
        }
        catch (JsonException) { errors = ["Manifest JSON is malformed or contains unknown members."]; return false; }
    }

    /// <summary>Validates metadata without loading code or inspecting the filesystem.</summary>
    /// <param name="manifest">Candidate metadata.</param>
    /// <returns>Validation errors, or an empty collection.</returns>
    public static IReadOnlyList<string> Validate(PluginManifest? manifest)
    {
        if (manifest is null) { return ["Manifest is absent."]; }
        List<string> errors = [];
        if (!Identifier(manifest.Id)) { errors.Add("Invalid plugin identity."); }
        if (!Identifier(manifest.Category)) { errors.Add("Invalid category identity."); }
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128 || manifest.Name.Any(char.IsControl))
        { errors.Add("Invalid display name."); }
        if (!Version.TryParse(manifest.Version, out _)) { errors.Add("Invalid numeric package version."); }
        if (manifest.MinimumApiVersion < 1 || manifest.MaximumApiVersion < manifest.MinimumApiVersion
            || PluginApi.Version < manifest.MinimumApiVersion || PluginApi.Version > manifest.MaximumApiVersion)
        { errors.Add("Incompatible common Plugin SDK version range."); }
        if (string.IsNullOrEmpty(manifest.EntryAssembly) || manifest.EntryAssembly.Length > 128
            || !manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || manifest.EntryAssembly.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            || manifest.EntryAssembly.StartsWith(".", StringComparison.Ordinal))
        { errors.Add("Entry assembly must be a bounded DLL filename at the package root."); }
        if (string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 256
            || manifest.EntryType.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+')))
        { errors.Add("Invalid entry type."); }
        if (manifest.Dependencies is null || manifest.Dependencies.Count > 32) { errors.Add("Invalid dependency list."); }
        else
        {
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (var dependency in manifest.Dependencies)
            {
                if (dependency is null || !Identifier(dependency.Id) || dependency.Id == manifest.Id || !ids.Add(dependency.Id)
                    || !Version.TryParse(dependency.MinimumVersion, out var minimum)
                    || (dependency.MaximumVersionExclusive is { } upper
                        && (!Version.TryParse(upper, out var maximum) || maximum <= minimum)))
                { errors.Add("Invalid, duplicate or self-referencing dependency."); break; }
            }
        }
        if (manifest.Permissions is null || manifest.Permissions.Count > 32 || manifest.Permissions.Any(permission => !Identifier(permission))
            || manifest.Permissions.Distinct(StringComparer.Ordinal).Count() != manifest.Permissions.Count)
        { errors.Add("Invalid permission declarations."); }
        return errors;
    }

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 128 }
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginManifest))]
internal partial class PluginJsonContext : JsonSerializerContext;
