using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Trials;

/// <summary>Closed validation and review rendering for repository-owned mutation trials.</summary>
public static class MutationTrialMetadataPolicy
{
    /// <summary>Operations deliberately absent from all Device Lab mutation authority.</summary>
    public static IReadOnlyList<string> PermanentlyExcludedOperations { get; } =
    [
        "EEPROM, ROM, or UEFI writes",
        "firmware flashing",
        "provider or registry repair",
        "driver restart or installation",
        "persistent charge-policy writes",
        "blind bus scans",
        "unknown IOCTL, HID, ACPI, MMIO, MSR, or raw-port operations",
        "physical-memory access",
        "test-certificate installation",
        "Windows test-signing changes",
    ];

    /// <summary>Validates every safety field and rejects unsafe trial families by unrepresentability.</summary>
    /// <param name="metadata">Locally compiled catalog metadata.</param>
    /// <returns>Every deterministic validation error.</returns>
    public static IReadOnlyList<string> Validate(MutationTrialMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        List<string> errors = [];
        Required(metadata.Id, "Trial ID", errors);
        Required(metadata.FamilyId, "Family ID", errors);
        Required(metadata.BoardId, "Board ID", errors);
        Required(metadata.EndpointId, "Endpoint ID", errors);
        Required(metadata.ResourceId, "Resource ID", errors);
        Required(metadata.ExpectedEffect, "Expected effect", errors);
        Required(metadata.IndependentObservation, "Independent observation", errors);
        Required(metadata.Rollback, "Rollback", errors);
        Required(metadata.EmergencyAction, "Emergency action", errors);

        if (metadata.Version <= 0 || metadata.ModuleVersion <= 0)
        {
            errors.Add("Trial and module versions must be positive.");
        }

        if (metadata.ImplementationSha256.Length != 64
            || metadata.ImplementationSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            errors.Add("Trial implementation SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        if (metadata.FirmwareIdentities.Count == 0
            || metadata.FirmwareIdentities.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("At least one exact firmware identity is required.");
        }

        if (metadata.Actions.Count == 0 || metadata.Actions.Count > 16
            || metadata.Actions.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("A trial requires between one and sixteen explicit actions.");
        }

        if (metadata.MaximumWrites is < 1 or > 16)
        {
            errors.Add("Maximum writes must be between one and sixteen including recovery.");
        }

        if (metadata.TimeoutMilliseconds is < 100 or > 60_000)
        {
            errors.Add("Trial timeout must be between 100 and 60000 milliseconds.");
        }

        if (metadata.MaximumRetries is < 0 or > 3)
        {
            errors.Add("Trial retry count must be between zero and three.");
        }

        if (metadata.CooldownSeconds is < 1 or > 86_400)
        {
            errors.Add("Trial cooldown must be between one second and one day.");
        }

        if (!metadata.RollbackVerified)
        {
            errors.Add("Rollback must already be verified on the exact target.");
        }

        if (metadata.Family is MutationTrialFamily.VolatileRgbZone && !metadata.DeviceVolatile)
        {
            errors.Add("An RGB trial requires an exact profile already proven device-volatile.");
        }

        return errors;
    }

    /// <summary>Renders every field the operator must review and hashes that exact text.</summary>
    /// <param name="metadata">Locally compiled metadata.</param>
    /// <returns>Stable human-readable review.</returns>
    public static string RenderReview(MutationTrialMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        StringBuilder text = new();
        text.AppendLine($"Trial: {metadata.Id}@{metadata.Version}");
        text.AppendLine($"Identity: family={metadata.FamilyId}; board={metadata.BoardId}; firmware=[{string.Join(",", metadata.FirmwareIdentities.Order(StringComparer.Ordinal))}]; endpoint={metadata.EndpointId}");
        text.AppendLine($"Capability/resource: {metadata.Family} / {metadata.ResourceId}; module={metadata.ModuleVersion}; lease={LeaseKind.Experiment}");
        text.AppendLine($"Maximum writes: {metadata.MaximumWrites}");
        foreach (string action in metadata.Actions)
        {
            text.AppendLine($"Action: {action}");
        }

        text.AppendLine($"Effect: {metadata.ExpectedEffect}");
        text.AppendLine($"Observation: {metadata.IndependentObservation}");
        text.AppendLine($"Rollback: {metadata.Rollback}");
        text.AppendLine($"Emergency: {metadata.EmergencyAction}");
        text.AppendLine($"Bounds: timeout={metadata.TimeoutMilliseconds}ms; retries={metadata.MaximumRetries}; cooldown={metadata.CooldownSeconds}s");
        text.Append($"Implementation SHA-256: {metadata.ImplementationSha256.ToLowerInvariant()}");
        return text.ToString();
    }

    /// <summary>Hashes the exact review text the operator saw.</summary>
    /// <param name="metadata">Locally compiled metadata.</param>
    /// <returns>Lower-case SHA-256.</returns>
    public static string ReviewSha256(MutationTrialMetadata metadata) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RenderReview(metadata))))
            .ToLowerInvariant();

    private static void Required(string value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label} is required.");
        }
    }
}

/// <summary>Creates and revalidates short-lived, state-pinned mutation authorization.</summary>
public static class MutationTrialAuthorizationPolicy
{
    private static readonly TimeSpan MaximumAuthorizationLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Authorizes only one reviewed, local, interactive, non-nested experiment.</summary>
    /// <param name="metadata">Repository-reviewed trial.</param>
    /// <param name="review">Proof of exact local review.</param>
    /// <param name="snapshot">Current preflight, identity, generation, and original-state values.</param>
    /// <returns>Short-lived authorization or the first closed rejection.</returns>
    public static MutationTrialAuthorization Authorize(
        MutationTrialMetadata metadata,
        MutationTrialReviewReceipt review,
        MutationTrialAuthorizationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyList<string> errors = MutationTrialMetadataPolicy.Validate(metadata);
        if (errors.Count != 0)
        {
            return Reject("metadata.invalid", string.Join(" ", errors));
        }

        if (!snapshot.IsInteractive || snapshot.IsUnattended || snapshot.IsContinuousIntegration)
        {
            return Reject("interaction.required", "Mutation trials require a local attended console and refuse CI.");
        }

        if (snapshot.NestedTrialActive)
        {
            return Reject("trial.nested", "Only one mutation trial may be active.");
        }

        if (review.ReviewedFields != MutationTrialReviewField.All
            || !string.Equals(review.ConfirmedTrialId, metadata.Id, StringComparison.Ordinal)
            || !string.Equals(review.ReviewSha256, MutationTrialMetadataPolicy.ReviewSha256(metadata), StringComparison.OrdinalIgnoreCase)
            || snapshot.Now - review.ConfirmedAt > MaximumAuthorizationLifetime
            || review.ConfirmedAt > snapshot.Now)
        {
            return Reject("review.incomplete", "The exact current trial metadata was not fully reviewed and confirmed.");
        }

        if (snapshot.Preflight.Status is DeviceLabDoctorStatus.Blocked
            || snapshot.Preflight.Route is not DeviceLabAccessRoute.ExperimentLease
            || snapshot.Preflight.RequiredLease is not LeaseKind.Experiment
            || !string.Equals(snapshot.Preflight.ResourceId, metadata.ResourceId, StringComparison.Ordinal))
        {
            return Reject("preflight.mismatch", "Preflight did not grant one experiment lease for the exact resource.");
        }

        if (!string.Equals(metadata.FamilyId, snapshot.FamilyId, StringComparison.Ordinal)
            || !string.Equals(metadata.BoardId, snapshot.BoardId, StringComparison.Ordinal)
            || !metadata.FirmwareIdentities.Contains(snapshot.FirmwareIdentity, StringComparer.Ordinal)
            || !string.Equals(metadata.EndpointId, snapshot.EndpointId, StringComparison.Ordinal))
        {
            return Reject("identity.mismatch", "Exact family, board, firmware, or endpoint changed.");
        }

        if (!string.Equals(metadata.ImplementationSha256, snapshot.InstalledSha256, StringComparison.OrdinalIgnoreCase)
            || metadata.ModuleVersion != snapshot.ModuleVersion)
        {
            return Reject("implementation.mismatch", "Trial hash or module version changed.");
        }

        if (snapshot.OriginalStateSha256.Length != 64
            || snapshot.OriginalStateSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            return Reject("original-state.invalid", "Expected original state must be independently read and hashed.");
        }

        if (snapshot.LastCompletedAt is { } last
            && snapshot.Now < last.AddSeconds(metadata.CooldownSeconds))
        {
            return Reject("trial.cooldown", "The reviewed cooldown has not elapsed.");
        }

        DateTimeOffset expiry = snapshot.Now.Add(MaximumAuthorizationLifetime);
        return new MutationTrialAuthorization
        {
            Granted = true,
            Code = "authorized",
            Message = "One exact resource trial is authorized until state changes or the token expires.",
            StateFingerprint = Fingerprint(metadata, snapshot),
            ExpiresAt = expiry,
        };
    }

    /// <summary>Revalidates every pinned field immediately before the first write.</summary>
    /// <param name="authorization">Previously granted token.</param>
    /// <param name="metadata">Current locally compiled metadata.</param>
    /// <param name="snapshot">Fresh current state.</param>
    /// <returns>True only while every safety-relevant value is unchanged.</returns>
    public static bool IsCurrent(
        MutationTrialAuthorization authorization,
        MutationTrialMetadata metadata,
        MutationTrialAuthorizationSnapshot snapshot) =>
        authorization.Granted
        && authorization.ExpiresAt is { } expiry
        && expiry > snapshot.Now
        && string.Equals(
            authorization.StateFingerprint,
            Fingerprint(metadata, snapshot),
            StringComparison.Ordinal);

    private static string Fingerprint(
        MutationTrialMetadata metadata,
        MutationTrialAuthorizationSnapshot snapshot)
    {
        string canonical = string.Join("\n",
            metadata.Id,
            metadata.Version.ToString(CultureInfo.InvariantCulture),
            metadata.ImplementationSha256.ToLowerInvariant(),
            snapshot.FamilyId,
            snapshot.BoardId,
            snapshot.FirmwareIdentity,
            snapshot.EndpointId,
            snapshot.Preflight.ResourceId,
            snapshot.Preflight.Route.ToString(),
            snapshot.Preflight.RequiredLease?.ToString() ?? "none",
            snapshot.Preflight.HostGeneration?.ToString(CultureInfo.InvariantCulture) ?? "none",
            snapshot.Preflight.DeviceGeneration?.ToString(CultureInfo.InvariantCulture) ?? "none",
            snapshot.ModuleVersion.ToString(CultureInfo.InvariantCulture),
            snapshot.OriginalStateSha256.ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static MutationTrialAuthorization Reject(string code, string message) => new()
    {
        Granted = false,
        Code = code,
        Message = message,
    };
}

/// <summary>Pure fault harness for every crash boundary in a mutation transaction.</summary>
public static class MutationTrialFaultHarness
{
    /// <summary>Simulates interruption after one transactional step.</summary>
    /// <param name="resourceId">Only affected resource.</param>
    /// <param name="fault">Injected process-death point.</param>
    /// <returns>Fail-closed dimensions and expected durable journal states.</returns>
    public static MutationTrialOutcome Simulate(string resourceId, MutationTrialFaultPoint fault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        List<JournalEntryStatus> journal = [];

        if (fault is MutationTrialFaultPoint.AfterSnapshot)
        {
            return Outcome(
                ProbeExecution.Cancelled,
                ProbeObservation.NoSignal,
                ProbeMutation.None,
                ProbeCleanup.NotRequired,
                journal,
                resourceId);
        }

        journal.Add(JournalEntryStatus.Planned);
        if (fault is MutationTrialFaultPoint.AfterPlannedJournal)
        {
            return Outcome(
                ProbeExecution.Cancelled,
                ProbeObservation.NoSignal,
                ProbeMutation.None,
                ProbeCleanup.NotRequired,
                journal,
                resourceId);
        }

        journal.Add(JournalEntryStatus.Applying);
        if (fault is MutationTrialFaultPoint.AfterApplyingJournal)
        {
            return Outcome(
                ProbeExecution.Cancelled,
                ProbeObservation.NoSignal,
                ProbeMutation.AppliedUnverified,
                ProbeCleanup.RestoreUnverified,
                journal,
                resourceId);
        }

        journal.Add(JournalEntryStatus.AppliedUnverified);
        if (fault is MutationTrialFaultPoint.AfterApply)
        {
            return Outcome(
                ProbeExecution.Cancelled,
                ProbeObservation.NoSignal,
                ProbeMutation.AppliedUnverified,
                ProbeCleanup.RestoreUnverified,
                journal,
                resourceId);
        }

        journal.Add(JournalEntryStatus.AppliedVerified);
        if (fault is MutationTrialFaultPoint.AfterObservation or MutationTrialFaultPoint.AfterRollbackStarted)
        {
            return Outcome(
                ProbeExecution.Cancelled,
                ProbeObservation.Match,
                ProbeMutation.AppliedVerified,
                ProbeCleanup.RestoreUnverified,
                journal,
                resourceId);
        }

        journal.Add(JournalEntryStatus.RestoredUnverified);
        if (fault is MutationTrialFaultPoint.AfterRestore)
        {
            return Outcome(
                ProbeExecution.Cancelled,
                ProbeObservation.Match,
                ProbeMutation.AppliedVerified,
                ProbeCleanup.RestoreUnverified,
                journal,
                resourceId);
        }

        journal.Add(JournalEntryStatus.RestoredVerified);
        return fault is MutationTrialFaultPoint.AfterRestoreVerified
            ? Outcome(
                ProbeExecution.Cancelled,
                ProbeObservation.Match,
                ProbeMutation.AppliedVerified,
                ProbeCleanup.RestoredVerified,
                journal,
                resourceId)
            : Outcome(
                ProbeExecution.Completed,
                ProbeObservation.Match,
                ProbeMutation.AppliedVerified,
                ProbeCleanup.RestoredVerified,
                journal,
                resourceId);
    }

    private static MutationTrialOutcome Outcome(
        ProbeExecution execution,
        ProbeObservation observation,
        ProbeMutation mutation,
        ProbeCleanup cleanup,
        IReadOnlyList<JournalEntryStatus> journal,
        string resourceId)
    {
        ProbeResult result = new()
        {
            Execution = execution,
            Observation = observation,
            Mutation = mutation,
            Cleanup = cleanup,
        };
        return new MutationTrialOutcome
        {
            Result = result,
            QuarantinedResourceId = result.Verdict is CompatibilityVerdict.Quarantined
                ? resourceId
                : null,
            JournalStates = [.. journal],
        };
    }
}
