using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Device.Sdk.Packaging;

/// <summary>Validates the bounded six-field plugin manifest.</summary>
internal static class PluginManifestValidator
{
    /// <summary>Returns every deterministic validation failure.</summary>
    /// <param name="manifest">Parsed manifest.</param>
    /// <returns>All validation failures, or an empty list.</returns>
    internal static IReadOnlyList<ManifestValidationError> Validate(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        List<ManifestValidationError> errors = [];
        ValidateIdentifier(errors, "id", manifest.Id);
        ValidateText(errors, "name", manifest.Name);
        ValidateVersion(errors, manifest.Version);
        if (manifest.ApiVersion != DeviceApi.Version)
        {
            Add(errors, "apiVersion", ManifestValidationCode.InvalidApiVersion,
                $"Plugin API {manifest.ApiVersion} does not equal runtime API {DeviceApi.Version}.");
        }

        ValidateRelativeAssemblyPath(errors, manifest.EntryAssembly);
        ValidateEntryType(errors, manifest.EntryType);
        return errors;
    }

    private static void ValidateIdentifier(
        ICollection<ManifestValidationError> errors,
        string path,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, ManifestValidationCode.MissingField, "A package identifier is required.");
            return;
        }

        if (value.Length > ManifestLimits.MaxIdLength)
        {
            Add(errors, path, ManifestValidationCode.LimitExceeded, "The package identifier is too long.");
        }

        if (!value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_'))
        {
            Add(errors, path, ManifestValidationCode.InvalidIdentifier,
                "Package identifiers may contain only ASCII letters, digits, '.', '-', and '_'.");
        }
    }

    private static void ValidateText(
        ICollection<ManifestValidationError> errors,
        string path,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, ManifestValidationCode.MissingField, "A package name is required.");
        }
        else if (value.Length > ManifestLimits.MaxDisplayTextLength)
        {
            Add(errors, path, ManifestValidationCode.LimitExceeded, "The package name is too long.");
        }
    }

    private static void ValidateVersion(
        ICollection<ManifestValidationError> errors,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, "version", ManifestValidationCode.MissingField, "A package version is required.");
            return;
        }

        if (!Version.TryParse(value, out Version? parsed)
            || parsed.ToString(parsed.Revision >= 0 ? 4 : parsed.Build >= 0 ? 3 : 2) != value)
        {
            Add(errors, "version", ManifestValidationCode.InvalidVersion,
                "Package versions must be canonical dotted numeric versions.");
        }
    }

    private static void ValidateRelativeAssemblyPath(
        ICollection<ManifestValidationError> errors,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, "entryAssembly", ManifestValidationCode.MissingField,
                "An entry assembly is required.");
            return;
        }

        if (value.Length > ManifestLimits.MaxPathLength
            || Path.IsPathRooted(value)
            || value.Contains(':', StringComparison.Ordinal)
            || value.Split('/', '\\').Any(segment => segment is "" or "." or "..")
            || !string.Equals(Path.GetExtension(value), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            Add(errors, "entryAssembly", ManifestValidationCode.UnsafePath,
                "The entry assembly must be a bounded relative DLL path without traversal.");
        }
    }

    private static void ValidateEntryType(
        ICollection<ManifestValidationError> errors,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, "entryType", ManifestValidationCode.MissingField, "An entry type is required.");
            return;
        }

        if (value.Length > ManifestLimits.MaxDisplayTextLength
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '+' or '`')))
        {
            Add(errors, "entryType", ManifestValidationCode.InvalidIdentifier,
                "The entry type must be a bounded namespace-qualified CLR type name.");
        }
    }

    private static void Add(
        ICollection<ManifestValidationError> errors,
        string path,
        ManifestValidationCode code,
        string message) => errors.Add(new ManifestValidationError(path, code, message));
}
