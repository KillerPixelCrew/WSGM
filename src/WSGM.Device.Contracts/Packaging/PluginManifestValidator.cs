using System;
using System.Collections.Generic;
using System.Globalization;
using WSGM.Device.Contracts.Identity;

namespace WSGM.Device.Contracts.Packaging;

/// <summary>
/// Validates a parsed <see cref="PluginManifest"/> against the package rules.
/// </summary>
/// <remarks>
/// Validation is a pure function returning every problem it found, not an exception on the first one:
/// a package author fixing a manifest should see the whole list, and Device Lab reports them together.
/// Parsing enforces shape and size; this enforces meaning.
/// </remarks>
public static class PluginManifestValidator
{
    /// <summary>Lowest manifest schema version this build understands.</summary>
    public const int MinSupportedSchemaVersion = 1;

    /// <summary>Highest manifest schema version this build understands.</summary>
    public const int MaxSupportedSchemaVersion = 1;

    /// <summary>
    /// Returns every rule violation in <paramref name="manifest"/>. An empty result means the
    /// manifest is structurally acceptable — it says nothing about package trust, privilege,
    /// hardware verification, or retail approval, all of which are assigned elsewhere.
    /// </summary>
    /// <param name="manifest">The parsed manifest to check.</param>
    /// <returns>All violations found, in document order.</returns>
    public static IReadOnlyList<ManifestValidationError> Validate(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        List<ManifestValidationError> errors = [];

        if (manifest.SchemaVersion is < MinSupportedSchemaVersion or > MaxSupportedSchemaVersion)
        {
            Add(errors, "schemaVersion", ManifestValidationCode.UnsupportedSchemaVersion,
                $"Schema version {manifest.SchemaVersion} is outside the supported range "
                    + $"{MinSupportedSchemaVersion}-{MaxSupportedSchemaVersion}.");
        }

        ValidateIdentifier(errors, "id", manifest.Id);
        ValidateVersion(errors, "version", manifest.Version);
        ValidateDisplayText(errors, "displayName", manifest.DisplayName);
        ValidateDisplayText(errors, "publisher", manifest.Publisher);
        ValidateRelativePath(errors, "entryPoint", manifest.EntryPoint);

        // An inverted or empty range would make the package unselectable rather than unsafe, but it
        // is always an authoring mistake and is far cheaper to catch here than during selection.
        if (manifest.MaxApiVersion < manifest.MinApiVersion)
        {
            Add(errors, "maxApiVersion", ManifestValidationCode.InvalidApiRange,
                $"maxApiVersion {manifest.MaxApiVersion} is below minApiVersion {manifest.MinApiVersion}.");
        }

        ValidateCount(errors, "devices", manifest.Devices.Count, ManifestLimits.MaxDevices);
        ValidateCount(errors, "dependencies", manifest.Dependencies.Count, ManifestLimits.MaxDependencies);
        ValidateCount(errors, "riskDeclarations", manifest.RiskDeclarations.Count,
            ManifestLimits.MaxRiskDeclarations);

        if (manifest.Devices.Count == 0)
        {
            Add(errors, "devices", ManifestValidationCode.MissingField,
                "A package must define at least one device.");
        }

        ValidateProvenance(errors, "provenance", manifest.Provenance);

        HashSet<string> deviceIds = NewIdSet();
        for (int i = 0; i < manifest.Devices.Count; i++)
        {
            string path = $"devices[{i}]";
            DeviceDefinition device = manifest.Devices[i];

            if (!deviceIds.Add(device.Id))
            {
                Add(errors, $"{path}.id", ManifestValidationCode.DuplicateIdentifier,
                    $"Device definition ID '{device.Id}' is used more than once.");
            }

            ValidateDevice(errors, path, device);
        }

        HashSet<string> dependencyIds = NewIdSet();
        for (int i = 0; i < manifest.Dependencies.Count; i++)
        {
            string path = $"dependencies[{i}]";
            DependencyDeclaration dependency = manifest.Dependencies[i];

            ValidateIdentifier(errors, $"{path}.id", dependency.Id);
            if (!dependencyIds.Add(dependency.Id))
            {
                Add(errors, $"{path}.id", ManifestValidationCode.DuplicateIdentifier,
                    $"Dependency ID '{dependency.Id}' is used more than once.");
            }
        }

        return errors;
    }

    private static void ValidateDevice(
        List<ManifestValidationError> errors,
        string path,
        DeviceDefinition device)
    {
        ValidateIdentifier(errors, $"{path}.id", device.Id);
        ValidateDisplayText(errors, $"{path}.displayName", device.DisplayName);

        ValidateCount(errors, $"{path}.identity", device.Identity.Count,
            ManifestLimits.MaxIdentityObservations);
        ValidateCount(errors, $"{path}.usbEndpoints", device.UsbEndpoints.Count,
            ManifestLimits.MaxUsbEndpoints);
        ValidateCount(errors, $"{path}.resources", device.Resources.Count, ManifestLimits.MaxResources);
        ValidateCount(errors, $"{path}.modules", device.Modules.Count, ManifestLimits.MaxModules);
        ValidateCount(errors, $"{path}.capabilities", device.Capabilities.Count,
            ManifestLimits.MaxCapabilities);

        HashSet<string> endpointIds = NewIdSet();
        for (int i = 0; i < device.UsbEndpoints.Count; i++)
        {
            string endpointPath = $"{path}.usbEndpoints[{i}]";
            UsbEndpointDeclaration endpoint = device.UsbEndpoints[i];

            ValidateIdentifier(errors, $"{endpointPath}.id", endpoint.Id);
            if (!endpointIds.Add(endpoint.Id))
            {
                Add(errors, $"{endpointPath}.id", ManifestValidationCode.DuplicateIdentifier,
                    $"Endpoint ID '{endpoint.Id}' is used more than once.");
            }

            ValidateHexIdentifier(errors, $"{endpointPath}.vendorId", endpoint.VendorId);
            for (int p = 0; p < endpoint.ProductIds.Count; p++)
            {
                ValidateHexIdentifier(errors, $"{endpointPath}.productIds[{p}]", endpoint.ProductIds[p]);
            }
        }

        ValidateIdentity(errors, path, device, endpointIds);

        HashSet<string> resourceIds = NewIdSet();
        for (int i = 0; i < device.Resources.Count; i++)
        {
            string resourcePath = $"{path}.resources[{i}]";
            ResourceDeclaration resource = device.Resources[i];

            ValidateIdentifier(errors, $"{resourcePath}.id", resource.Id);
            if (!resourceIds.Add(resource.Id))
            {
                Add(errors, $"{resourcePath}.id", ManifestValidationCode.DuplicateIdentifier,
                    $"Resource ID '{resource.Id}' is used more than once.");
            }

            if (resource.EndpointId is { Length: > 0 } && !endpointIds.Contains(resource.EndpointId))
            {
                Add(errors, $"{resourcePath}.endpointId", ManifestValidationCode.UnresolvedReference,
                    $"Resource references endpoint '{resource.EndpointId}', which this device does not declare.");
            }
        }

        HashSet<string> moduleIds = NewIdSet();
        for (int i = 0; i < device.Modules.Count; i++)
        {
            string modulePath = $"{path}.modules[{i}]";
            ModuleReference module = device.Modules[i];

            ValidateIdentifier(errors, $"{modulePath}.id", module.Id);

            // Two versions of the same module in one composition would make the effective layout,
            // limits, and recovery policy depend on load order.
            if (!moduleIds.Add(module.Id))
            {
                Add(errors, $"{modulePath}.id", ManifestValidationCode.DuplicateIdentifier,
                    $"Module '{module.Id}' is composed more than once.");
            }

            if (module.Version <= 0)
            {
                Add(errors, $"{modulePath}.version", ManifestValidationCode.InvalidVersion,
                    "Module version must be a positive integer, so composition is always pinned.");
            }
        }

        HashSet<string> capabilityIds = NewIdSet();
        for (int i = 0; i < device.Capabilities.Count; i++)
        {
            string capabilityPath = $"{path}.capabilities[{i}]";
            ValidateIdentifier(errors, capabilityPath, device.Capabilities[i]);
            if (!capabilityIds.Add(device.Capabilities[i]))
            {
                Add(errors, capabilityPath, ManifestValidationCode.DuplicateIdentifier,
                    $"Capability '{device.Capabilities[i]}' is declared more than once.");
            }
        }
    }

    private static void ValidateIdentity(
        List<ManifestValidationError> errors,
        string path,
        DeviceDefinition device,
        HashSet<string> endpointIds)
    {
        bool hasHardConstraint = false;

        for (int i = 0; i < device.Identity.Count; i++)
        {
            string observationPath = $"{path}.identity[{i}]";
            IdentityObservation observation = device.Identity[i];

            if (observation.Strength is IdentityStrength.Required or IdentityStrength.Excluded)
            {
                hasHardConstraint = true;

                // Marketing text is chosen by marketing: it varies by region and SKU, is not stable
                // across firmware revisions, and duplicates across models. Machine-readable signals
                // exist for exactly this reason, so a hard gate must use one.
                if (observation.Signal is IdentitySignal.SmbiosSystemProduct
                    or IdentitySignal.SmbiosSystemFamily)
                {
                    Add(errors, $"{observationPath}.strength",
                        ManifestValidationCode.MarketingNameAsHardGate,
                        $"{observation.Signal} is marketing text and may only be Weighted or "
                            + "Informational. Gate on the baseboard product, SKU, or firmware version.");
                }
            }

            if (observation.Strength is IdentityStrength.Weighted
                && observation.Weight is <= 0 or > ManifestLimits.MaxObservationWeight)
            {
                Add(errors, $"{observationPath}.weight",
                    ManifestValidationCode.InvalidObservationWeight,
                    $"Weight must be between 1 and {ManifestLimits.MaxObservationWeight}.");
            }

            if (observation.Strength is not IdentityStrength.Informational
                && observation.Values.Count == 0)
            {
                Add(errors, $"{observationPath}.values",
                    ManifestValidationCode.MissingObservationValues,
                    "A matching observation must list the values it accepts.");
            }

            foreach (string value in observation.Values)
            {
                ValidateDisplayText(errors, $"{observationPath}.values", value);
            }

            if (observation.EndpointId is { Length: > 0 }
                && !endpointIds.Contains(observation.EndpointId))
            {
                Add(errors, $"{observationPath}.endpointId",
                    ManifestValidationCode.UnresolvedReference,
                    $"Observation references endpoint '{observation.EndpointId}', which this device does not declare.");
            }
        }

        // Without a hard constraint a definition matches by score alone, which is how a package ends
        // up selected for hardware it was never written for. Identity similarity nominates a
        // candidate; it must never be the whole gate.
        if (!hasHardConstraint)
        {
            Add(errors, $"{path}.identity", ManifestValidationCode.NoHardIdentityConstraint,
                "A device definition needs at least one Required or Excluded identity observation. "
                    + "Weighted signals order candidates; they cannot select one.");
        }
    }

    private static void ValidateProvenance(
        List<ManifestValidationError> errors,
        string path,
        PackageProvenance provenance)
    {
        ValidateDisplayText(errors, $"{path}.source", provenance.Source);
        ValidateDisplayText(errors, $"{path}.license", provenance.License);

        if (provenance.LicenseNoticePath is { Length: > 0 })
        {
            ValidateRelativePath(errors, $"{path}.licenseNoticePath", provenance.LicenseNoticePath);
        }

        bool needsApproval = provenance.ProvenanceClass
            is ProvenanceClass.CopiedCode or ProvenanceClass.RedistributedBinary;

        if (needsApproval && string.IsNullOrWhiteSpace(provenance.ApprovalReference))
        {
            Add(errors, $"{path}.approvalReference",
                ManifestValidationCode.MissingApprovalReference,
                $"{provenance.ProvenanceClass} ships someone else's expression and requires a "
                    + "recorded approval reference.");
        }
    }

    private static void ValidateIdentifier(
        List<ManifestValidationError> errors,
        string path,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, ManifestValidationCode.MissingField, "Identifier is required.");
            return;
        }

        if (value.Length > ManifestLimits.MaxIdLength)
        {
            Add(errors, path, ManifestValidationCode.LimitExceeded,
                $"Identifier exceeds {ManifestLimits.MaxIdLength} characters.");
            return;
        }

        // Identifiers reach diagnostics, log lines, and directory names. Restricting them to this
        // set keeps a package from expressing a path separator, a control character, or a
        // right-to-left override that renders as a different identifier than it matches as.
        foreach (char c in value)
        {
            bool allowed = (c is >= 'a' and <= 'z')
                || (c is >= 'A' and <= 'Z')
                || (c is >= '0' and <= '9')
                || c is '.' or '-' or '_';

            if (!allowed)
            {
                Add(errors, path, ManifestValidationCode.InvalidIdentifier,
                    $"Identifier '{value}' contains '{c}'. Allowed: letters, digits, '.', '-', '_'.");
                return;
            }
        }
    }

    private static void ValidateHexIdentifier(
        List<ManifestValidationError> errors,
        string path,
        string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Add(errors, path, ManifestValidationCode.MissingField, "Hexadecimal identifier is required.");
            return;
        }

        // Exactly four uppercase hex digits: "0DB0", never "0xDB0", "db0" or "0DB0 ". One canonical
        // form means comparison is ordinal and a manifest cannot near-miss a match by formatting.
        bool valid = value.Length == 4;
        if (valid)
        {
            foreach (char c in value)
            {
                if (!((c is >= '0' and <= '9') || (c is >= 'A' and <= 'F')))
                {
                    valid = false;
                    break;
                }
            }
        }

        if (!valid)
        {
            Add(errors, path, ManifestValidationCode.InvalidHexIdentifier,
                $"'{value}' must be exactly four uppercase hexadecimal digits, for example '0DB0'.");
        }
    }

    private static void ValidateDisplayText(
        List<ManifestValidationError> errors,
        string path,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, ManifestValidationCode.MissingField, "Value is required.");
            return;
        }

        if (value.Length > ManifestLimits.MaxDisplayTextLength)
        {
            Add(errors, path, ManifestValidationCode.LimitExceeded,
                $"Value exceeds {ManifestLimits.MaxDisplayTextLength} characters.");
            return;
        }

        // Control characters would corrupt log lines and diagnostics that echo this text.
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                Add(errors, path, ManifestValidationCode.InvalidIdentifier,
                    "Value contains a control character.");
                return;
            }
        }
    }

    private static void ValidateVersion(
        List<ManifestValidationError> errors,
        string path,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, ManifestValidationCode.MissingField, "Version is required.");
            return;
        }

        string[] parts = value.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            Add(errors, path, ManifestValidationCode.InvalidVersion,
                $"Version '{value}' must have two to four dotted numeric components.");
            return;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int component)
                || component < 0)
            {
                Add(errors, path, ManifestValidationCode.InvalidVersion,
                    $"Version '{value}' has a non-numeric or negative component.");
                return;
            }
        }
    }

    private static void ValidateRelativePath(
        List<ManifestValidationError> errors,
        string path,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, ManifestValidationCode.MissingField, "Path is required.");
            return;
        }

        if (value.Length > ManifestLimits.MaxPathLength)
        {
            Add(errors, path, ManifestValidationCode.LimitExceeded,
                $"Path exceeds {ManifestLimits.MaxPathLength} characters.");
            return;
        }

        // Every manifest path is resolved inside the package directory, so anything that can escape
        // it is rejected outright rather than normalized: rooted paths, drive-qualified paths, UNC
        // paths, and any traversal segment. Normalizing instead of rejecting is how traversal bugs
        // survive - the check and the later resolution disagree about what the string means.
        string normalized = value.Replace('\\', '/');

        if (normalized.StartsWith('/') || normalized.Contains("//", StringComparison.Ordinal))
        {
            Add(errors, path, ManifestValidationCode.UnsafePath,
                $"Path '{value}' must be relative to the package directory.");
            return;
        }

        if (value.Length >= 2 && value[1] == ':')
        {
            Add(errors, path, ManifestValidationCode.UnsafePath,
                $"Path '{value}' is drive-qualified.");
            return;
        }

        foreach (string segment in normalized.Split('/'))
        {
            if (segment is ".." or ".")
            {
                Add(errors, path, ManifestValidationCode.UnsafePath,
                    $"Path '{value}' contains a '{segment}' segment.");
                return;
            }
        }

        foreach (char c in value)
        {
            if (char.IsControl(c) || c is '?' or '*' or '|' or '<' or '>' or '"')
            {
                Add(errors, path, ManifestValidationCode.UnsafePath,
                    $"Path '{value}' contains an illegal character.");
                return;
            }
        }
    }

    private static void ValidateCount(
        List<ManifestValidationError> errors,
        string path,
        int count,
        int limit)
    {
        if (count > limit)
        {
            Add(errors, path, ManifestValidationCode.LimitExceeded,
                $"{count} entries exceeds the limit of {limit}.");
        }
    }

    private static HashSet<string> NewIdSet() => new(StringComparer.OrdinalIgnoreCase);

    private static void Add(
        List<ManifestValidationError> errors,
        string path,
        ManifestValidationCode code,
        string message) => errors.Add(new ManifestValidationError(path, code, message));
}
