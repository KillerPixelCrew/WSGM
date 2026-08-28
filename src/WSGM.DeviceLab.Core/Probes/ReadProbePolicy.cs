using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Probes;

/// <summary>Validates inert probe metadata before it can participate in selection or execution.</summary>
public static class ReadProbeMetadataPolicy
{
    /// <summary>Validates identity, hash, rate, deadline, structure, and cross-check bounds.</summary>
    /// <param name="metadata">Catalog metadata to inspect.</param>
    /// <returns>Every defect in deterministic field order.</returns>
    public static IReadOnlyList<string> Validate(ReadProbeMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        List<string> errors = [];

        Required(metadata.Id, "Probe ID", errors);
        Required(metadata.FamilyId, "Family ID", errors);
        Required(metadata.EndpointId, "Endpoint ID", errors);
        Required(metadata.ResourceId, "Resource ID", errors);
        Required(metadata.EvidenceOutputId, "Evidence output ID", errors);
        Required(metadata.CrossCheck.Id, "Cross-check ID", errors);

        if (metadata.Version <= 0)
        {
            errors.Add("Probe version must be positive.");
        }

        if (metadata.ImplementationSha256.Length != 64
            || metadata.ImplementationSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            errors.Add("Implementation SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        if (metadata.MaximumReadsPerSecond is < 1 or > 20)
        {
            errors.Add("Read rate must be between 1 and 20 calls per second.");
        }

        if (metadata.TimeoutMilliseconds is < 50 or > 30_000)
        {
            errors.Add("Probe deadline must be between 50 and 30000 milliseconds.");
        }

        if (metadata.Repetitions is < 1 or > 10)
        {
            errors.Add("Probe repetitions must be between 1 and 10.");
        }

        ReadProbeResponseExpectation expected = metadata.ExpectedResponse;
        if (expected.MinimumLength < 0
            || expected.MaximumLength < expected.MinimumLength
            || expected.MaximumLength > 65_536)
        {
            errors.Add("Expected response length must be ordered and no larger than 65536 bytes.");
        }

        if (expected.AllowedStatusCodes.Count == 0)
        {
            errors.Add("At least one response status code must be allowlisted.");
        }

        if (expected.MinimumValue is { } minimum
            && expected.MaximumValue is { } maximum
            && minimum > maximum)
        {
            errors.Add("Expected numeric range is reversed.");
        }

        if (metadata.CrossCheck.Kind is ReadProbeCrossCheckKind.InRange
            && (metadata.CrossCheck.MinimumValue is null
                || metadata.CrossCheck.MaximumValue is null
                || metadata.CrossCheck.MinimumValue > metadata.CrossCheck.MaximumValue))
        {
            errors.Add("An in-range cross-check requires an ordered numeric range.");
        }

        return errors;
    }

    private static void Required(string value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label} is required.");
        }
    }
}

/// <summary>Admits only exact, local, hash-pinned probe code under its trust policy.</summary>
public static class ReadProbeAdmission
{
    /// <summary>Evaluates admission without opening the target device endpoint.</summary>
    /// <param name="metadata">Reviewed catalog metadata.</param>
    /// <param name="context">Current machine, install, and operator state.</param>
    /// <returns>The first fail-closed decision.</returns>
    public static ReadProbeAdmissionDecision Evaluate(
        ReadProbeMetadata metadata,
        ReadProbeAdmissionContext context)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<string> metadataErrors = ReadProbeMetadataPolicy.Validate(metadata);
        if (metadataErrors.Count != 0)
        {
            return Reject("metadata.invalid", string.Join(" ", metadataErrors));
        }

        if (!string.Equals(metadata.FamilyId, context.FamilyId, StringComparison.Ordinal)
            || !string.Equals(metadata.EndpointId, context.EndpointId, StringComparison.Ordinal))
        {
            return Reject("identity.mismatch", "The exact catalog family and endpoint did not match.");
        }

        if (!context.IsLocallyInstalled)
        {
            return Reject("install.missing", "The reviewed probe assembly is not installed locally.");
        }

        if (!string.Equals(
            metadata.ImplementationSha256,
            context.InstalledSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            return Reject("hash.mismatch", "The installed probe assembly does not match its pinned SHA-256.");
        }

        bool reviewed = metadata.Origin is DeviceLabOperationOrigin.ReviewedBuiltInCatalog;
        bool developer = metadata.Origin is DeviceLabOperationOrigin.SignedExternalPackage
            or DeviceLabOperationOrigin.SideloadedPackage
            or DeviceLabOperationOrigin.DeveloperSourceBuild;

        if (!reviewed && !developer)
        {
            return Reject("authority.imported", "Imported artifacts cannot supply executable probe authority.");
        }

        if (context.AutomaticSweep && !reviewed)
        {
            return Reject("authority.automatic", "Automatic sweeps run only WSGM-reviewed built-in probes.");
        }

        if (developer && (!context.DeveloperModeEnabled || !context.ExplicitDeveloperAction))
        {
            return Reject(
                "authority.developer-mode",
                "Developer probes require Developer Mode and an explicit action for this run.");
        }

        return new ReadProbeAdmissionDecision
        {
            Allowed = true,
            Code = "allowed",
            Message = "Exact locally installed hash-pinned probe admitted.",
        };
    }

    private static ReadProbeAdmissionDecision Reject(string code, string message) => new()
    {
        Allowed = false,
        Code = code,
        Message = message,
    };
}

/// <summary>An offline recommendation for resolving a top-rank candidate tie.</summary>
public sealed record ReadProbeSelection
{
    /// <summary>Selected inert probe metadata, or null when no safe discriminator exists.</summary>
    public ReadProbeMetadata? Probe { get; init; }

    /// <summary>Candidate module that the probe discriminates.</summary>
    public string? ModuleId { get; init; }

    /// <summary>Human-readable deterministic explanation.</summary>
    public required string Explanation { get; init; }
}

/// <summary>Selects the safest discriminating probe without touching hardware.</summary>
public static class ReadProbeSelector
{
    /// <summary>
    /// Selects among probes unique to one of the equally ranked leading candidates.
    /// </summary>
    /// <param name="assessments">Offline candidate results.</param>
    /// <param name="catalog">Inert known-implementation catalog.</param>
    /// <returns>A deterministic recommendation; this method never executes the probe.</returns>
    public static ReadProbeSelection Select(
        IReadOnlyList<CandidateAssessment> assessments,
        IReadOnlyList<CatalogEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(assessments);
        ArgumentNullException.ThrowIfNull(catalog);

        int bestRank = assessments.Count == 0 ? 0 : assessments.Max(item => item.ReuseRank);
        string[] ambiguous = [.. assessments
            .Where(item => bestRank > 0 && item.ReuseRank == bestRank)
            .Select(item => item.ModuleId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        if (ambiguous.Length < 2)
        {
            return new ReadProbeSelection
            {
                Explanation = "No equal top-rank candidate ambiguity requires a discriminating probe.",
            };
        }

        var candidates = catalog
            .Where(entry => ambiguous.Contains(entry.Module.Id, StringComparer.Ordinal))
            .SelectMany(entry => entry.ReadProbes.Select(probe => new { entry.Module.Id, Probe = probe }))
            .Where(item => ReadProbeMetadataPolicy.Validate(item.Probe).Count == 0)
            .GroupBy(item => $"{item.Probe.Id}\0{item.Probe.Version}", StringComparer.Ordinal)
            .Where(group => group.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() < ambiguous.Length)
            .SelectMany(group => group)
            .OrderBy(item => Risk(item.Probe.Family))
            .ThenBy(item => item.Probe.RequiresElevation)
            .ThenBy(item => item.Probe.MaximumReadsPerSecond)
            .ThenBy(item => item.Probe.Repetitions)
            .ThenBy(item => item.Probe.TimeoutMilliseconds)
            .ThenBy(item => item.Probe.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new ReadProbeSelection
            {
                Explanation = $"Candidates [{string.Join(", ", ambiguous)}] are tied, but no valid probe distinguishes them.",
            };
        }

        var selected = candidates[0];
        return new ReadProbeSelection
        {
            Probe = selected.Probe,
            ModuleId = selected.Id,
            Explanation = $"Probe '{selected.Probe.Id}' is the lowest-risk catalog discriminator for module '{selected.Id}'.",
        };
    }

    private static int Risk(ReadProbeFamily family) => family switch
    {
        ReadProbeFamily.NativeLibraryMetadata => 0,
        ReadProbeFamily.Version => 1,
        ReadProbeFamily.WmiStatus => 2,
        ReadProbeFamily.ControllerMode => 3,
        ReadProbeFamily.ChargeState => 4,
        ReadProbeFamily.FanRpm => 5,
        ReadProbeFamily.HidFeature => 6,
        ReadProbeFamily.EmbeddedController => 7,
        _ => int.MaxValue,
    };
}

/// <summary>Validates every structural and semantic dimension of a host response.</summary>
public static class ReadProbeResponseValidator
{
    /// <summary>Validates response identity, mutation, count, type, length, status, range, timing, stability, and cross-check.</summary>
    /// <param name="metadata">Catalog contract.</param>
    /// <param name="response">Disposable-host response.</param>
    /// <returns>Accepted only when every invariant holds.</returns>
    public static ReadProbeValidationResult Validate(
        ReadProbeMetadata metadata,
        ReadProbeHostResponse response)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(response);

        if (response.SchemaVersion != 1
            || !string.Equals(response.ProbeId, metadata.Id, StringComparison.Ordinal)
            || response.ProbeVersion != metadata.Version)
        {
            return Reject("response.identity", "Response schema or probe identity did not match the request.");
        }

        if (response.HardwareMutationObserved)
        {
            return Reject("response.mutation", "A read probe reported hardware mutation and is rejected.");
        }

        if (response.Status is not ReadProbeHostStatus.Completed)
        {
            return Reject($"host.{response.Status.ToString().ToLowerInvariant()}", response.Error ?? "Probe host did not complete.");
        }

        if (response.Samples.Count != metadata.Repetitions)
        {
            return Reject("response.repetitions", "Response did not contain the required repetitions.");
        }

        string? stableValue = null;
        foreach (ReadProbeSample sample in response.Samples)
        {
            ReadProbeResponseExpectation expected = metadata.ExpectedResponse;
            if (sample.ValueKind != expected.ValueKind)
            {
                return Reject("response.type", "Response value type was not the cataloged type.");
            }

            if (sample.Length < expected.MinimumLength || sample.Length > expected.MaximumLength)
            {
                return Reject("response.length", "Response length was outside the cataloged bounds.");
            }

            if (!expected.AllowedStatusCodes.Contains(sample.StatusCode))
            {
                return Reject("response.status", "Response status was not allowlisted.");
            }

            if (sample.ElapsedMilliseconds < 0 || sample.ElapsedMilliseconds > metadata.TimeoutMilliseconds)
            {
                return Reject("response.timing", "A response exceeded the whole-probe deadline.");
            }

            if (expected.ValueKind is ReadProbeValueKind.Integer
                && (sample.NumericValue is null
                    || expected.MinimumValue is { } minimum && sample.NumericValue.Value < minimum
                    || expected.MaximumValue is { } maximum && sample.NumericValue.Value > maximum))
            {
                return Reject("response.range", "Numeric response was absent or outside the cataloged range.");
            }

            ReadProbeValidationResult crossCheck = ValidateCrossCheck(metadata.CrossCheck, sample);
            if (!crossCheck.Accepted)
            {
                return crossCheck;
            }

            if (expected.MustBeStable
                && stableValue is not null
                && !string.Equals(stableValue, sample.NormalizedValue, StringComparison.Ordinal))
            {
                return Reject("response.unstable", "Repeated responses were not stable.");
            }

            stableValue ??= sample.NormalizedValue;
        }

        return new ReadProbeValidationResult
        {
            Accepted = true,
            Code = "accepted",
            Message = $"Validated {response.Samples.Count} response repetition(s) and their independent cross-checks.",
        };
    }

    private static ReadProbeValidationResult ValidateCrossCheck(
        ReadProbeCrossCheck crossCheck,
        ReadProbeSample sample)
    {
        bool accepted = crossCheck.Kind switch
        {
            ReadProbeCrossCheckKind.Equal => string.Equals(
                sample.NormalizedValue,
                sample.CrossCheckValue,
                StringComparison.Ordinal),
            ReadProbeCrossCheckKind.SameStatus => string.Equals(
                sample.NormalizedValue,
                sample.CrossCheckValue,
                StringComparison.OrdinalIgnoreCase),
            ReadProbeCrossCheckKind.InRange => sample.CrossCheckNumericValue is { } numeric
                && crossCheck.MinimumValue is { } minimum
                && crossCheck.MaximumValue is { } maximum
                && numeric >= minimum
                && numeric <= maximum,
            _ => false,
        };

        return accepted
            ? new ReadProbeValidationResult
            {
                Accepted = true,
                Code = "cross-check.accepted",
                Message = "Independent cross-check accepted.",
            }
            : Reject("response.cross-check", $"Independent cross-check '{crossCheck.Id}' did not corroborate the response.");
    }

    private static ReadProbeValidationResult Reject(string code, string message) => new()
    {
        Accepted = false,
        Code = code,
        Message = message,
    };
}
