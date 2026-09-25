using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;

namespace WSGM.Device.Sdk.Packaging;

/// <summary>Validates the bounded plugin manifest.</summary>
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
        ValidateHardware(errors, manifest.Hardware);
        ValidateCapabilities(errors, manifest.Capabilities);
        if (manifest.WsgmVersion is not null)
        {
            ValidateDottedVersion(errors, "wsgmVersion", manifest.WsgmVersion);
        }

        return errors;
    }

    private static void ValidateHardware(
        ICollection<ManifestValidationError> errors,
        IReadOnlyList<HardwareMatchRule>? rules)
    {
        if (rules is null)
        {
            Add(errors, "hardware", ManifestValidationCode.MissingField, "The hardware list cannot be null.");
            return;
        }

        if (rules.Count > HardwareMatchRule.MaxRules)
        {
            Add(errors, "hardware", ManifestValidationCode.LimitExceeded,
                $"A manifest may declare at most {HardwareMatchRule.MaxRules} hardware rules.");
        }

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            if (rule is null || rule.Fields().All(field => string.IsNullOrWhiteSpace(field.Value)))
            {
                Add(errors, $"hardware[{index}]", ManifestValidationCode.MissingField,
                    "A hardware rule must set at least one field.");
                continue;
            }

            foreach (var (name, value) in rule.Fields())
            {
                if (value is not null && (value.Length > HardwareMatchRule.MaxFieldLength
                                          || string.IsNullOrWhiteSpace(value)
                                          || value.Any(char.IsControl)))
                {
                    Add(errors, $"hardware[{index}].{name}", ManifestValidationCode.LimitExceeded,
                        "Hardware rule fields must be non-empty, bounded plain text.");
                }
            }
        }
    }

    private static void ValidateCapabilities(
        ICollection<ManifestValidationError> errors,
        IReadOnlyList<CapabilityRole>? roles)
    {
        if (roles is null)
        {
            Add(errors, "capabilities", ManifestValidationCode.MissingField,
                "The capability list cannot be null.");
            return;
        }

        if (roles.Any(role => !Enum.IsDefined(role)) || roles.Distinct().Count() != roles.Count)
        {
            Add(errors, "capabilities", ManifestValidationCode.InvalidIdentifier,
                "Capabilities must be distinct, known capability roles.");
        }
    }

    private static void ValidateDottedVersion(
        ICollection<ManifestValidationError> errors,
        string path,
        string value)
    {
        if (!Version.TryParse(value, out var parsed)
            || parsed.ToString(parsed.Revision >= 0 ? 4 : parsed.Build >= 0 ? 3 : 2) != value)
        {
            Add(errors, path, ManifestValidationCode.InvalidVersion,
                "Versions must be canonical dotted numeric versions.");
        }
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

        if (!PlainText.IsIdentifier(value, int.MaxValue))
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

        if (!Version.TryParse(value, out var parsed)
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
        string message)
    {
        errors.Add(new ManifestValidationError(path, code, message));
    }
}
