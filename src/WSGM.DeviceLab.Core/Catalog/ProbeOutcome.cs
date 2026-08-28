using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace WSGM.DeviceLab.Core.Catalog;

/// <summary>Whether a probe ran to completion, and why not when it did not.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProbeExecution>))]
public enum ProbeExecution
{
    /// <summary>The probe ran to completion.</summary>
    Completed,

    /// <summary>It exceeded its deadline.</summary>
    Timeout,

    /// <summary>The process lacked rights to open the resource.</summary>
    AccessDenied,

    /// <summary>Another owner held the resource.</summary>
    Conflict,

    /// <summary>The device disappeared mid-probe.</summary>
    Disconnected,

    /// <summary>A required provider, driver, or library was absent.</summary>
    PrerequisiteMissing,

    /// <summary>The operator or the tool cancelled it.</summary>
    Cancelled,
}

/// <summary>What the probe's independent observation showed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProbeObservation>))]
public enum ProbeObservation
{
    /// <summary>The observation matched what the module predicted.</summary>
    Match,

    /// <summary>The observation contradicted the prediction.</summary>
    Mismatch,

    /// <summary>Nothing was observed at all.</summary>
    NoSignal,

    /// <summary>The observation varied between repetitions.</summary>
    Unstable,

    /// <summary>The device re-enumerated during observation.</summary>
    TopologyChanged,
}

/// <summary>What the probe actually changed on the device.</summary>
/// <remarks>
/// Tracked separately from execution because a probe can complete and change nothing, or fail after
/// changing something. Merging them would lose exactly the case that matters: a timeout after a write
/// was issued.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ProbeMutation>))]
public enum ProbeMutation
{
    /// <summary>Nothing was written.</summary>
    None,

    /// <summary>A write was made and confirmed by readback.</summary>
    AppliedVerified,

    /// <summary>A write was issued but could not be confirmed.</summary>
    AppliedUnverified,

    /// <summary>A write was planned but never reached the device.</summary>
    NotApplied,
}

/// <summary>Whether the device was returned to its original state.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProbeCleanup>))]
public enum ProbeCleanup
{
    /// <summary>Nothing was changed, so nothing needed restoring.</summary>
    NotRequired,

    /// <summary>The original state was restored and confirmed.</summary>
    RestoredVerified,

    /// <summary>A restore was written but could not be confirmed.</summary>
    RestoreUnverified,

    /// <summary>Restoration failed.</summary>
    RestoreFailed,
}

/// <summary>The verdict derived from the four independent dimensions.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CompatibilityVerdict>))]
public enum CompatibilityVerdict
{
    /// <summary>The module works on this device.</summary>
    Compatible,

    /// <summary>The module does not work on this device.</summary>
    Incompatible,

    /// <summary>The probe did not establish either.</summary>
    Inconclusive,

    /// <summary>The probe could not run.</summary>
    Blocked,

    /// <summary>Restoration failed. The resource is blocked from further use.</summary>
    Quarantined,
}

/// <summary>
/// The four independently recorded dimensions of one probe run, and the verdict derived from them.
/// </summary>
/// <remarks>
/// Recorded as four values rather than one status because they answer different questions and fail
/// independently. "Timed out after writing, restoration unverified" and "timed out before writing"
/// are the same execution result and completely different situations.
/// </remarks>
public sealed record ProbeResult
{
    /// <summary>Whether the probe ran.</summary>
    public required ProbeExecution Execution { get; init; }

    /// <summary>What the independent observation showed.</summary>
    public required ProbeObservation Observation { get; init; }

    /// <summary>What was changed on the device.</summary>
    public required ProbeMutation Mutation { get; init; }

    /// <summary>Whether the device was put back.</summary>
    public required ProbeCleanup Cleanup { get; init; }

    /// <summary>
    /// The verdict these four dimensions support.
    /// </summary>
    /// <remarks>
    /// Order matters. Failed cleanup is checked first and wins over everything, including a probe
    /// that otherwise succeeded: a module whose write worked but whose restore did not is more
    /// dangerous than one that never worked, because the successful part invites using it again.
    /// </remarks>
    public CompatibilityVerdict Verdict
    {
        get
        {
            if (Cleanup is ProbeCleanup.RestoreFailed or ProbeCleanup.RestoreUnverified)
            {
                return CompatibilityVerdict.Quarantined;
            }

            if (Execution is not ProbeExecution.Completed)
            {
                // A probe that changed something and then failed to run to completion cannot be
                // dismissed as merely blocked - the device may not be as it was found. Any
                // unverified restore was already quarantined above.
                return Mutation is ProbeMutation.AppliedUnverified
                    || Cleanup is ProbeCleanup.RestoreUnverified
                        ? CompatibilityVerdict.Inconclusive
                        : CompatibilityVerdict.Blocked;
            }

            return Observation switch
            {
                ProbeObservation.Match => CompatibilityVerdict.Compatible,
                ProbeObservation.Mismatch => CompatibilityVerdict.Incompatible,
                _ => CompatibilityVerdict.Inconclusive,
            };
        }
    }
}

/// <summary>One probe result scoped to exactly one independently owned resource.</summary>
public sealed record ResourceProbeResult
{
    /// <summary>Resource whose evidence changed.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Module assessed on that resource.</summary>
    public required string ModuleId { get; init; }

    /// <summary>Independent probe dimensions.</summary>
    public required ProbeResult Result { get; init; }
}

/// <summary>Final evidence verdict for one resource, independent of every other resource.</summary>
public sealed record ResourceCompatibilityAssessment
{
    /// <summary>Resource assessed.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Strictest supported verdict for that resource.</summary>
    public required CompatibilityVerdict Verdict { get; init; }

    /// <summary>Modules whose results contributed.</summary>
    public IReadOnlyList<string> ModuleIds { get; init; } = [];
}

/// <summary>Derives only closed compatibility verdicts without spreading failure across resources.</summary>
public static class ResourceCompatibility
{
    /// <summary>Assesses each named resource independently.</summary>
    /// <param name="results">Resource-scoped probe results.</param>
    /// <returns>Stable resource-ID ordered assessments.</returns>
    public static IReadOnlyList<ResourceCompatibilityAssessment> Assess(
        IReadOnlyList<ResourceProbeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return [.. results
            .GroupBy(result => result.ResourceId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ResourceCompatibilityAssessment
            {
                ResourceId = group.Key,
                Verdict = group.Select(result => result.Result.Verdict)
                    .OrderByDescending(Severity)
                    .First(),
                ModuleIds = [.. group.Select(result => result.ModuleId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)],
            })];
    }

    private static int Severity(CompatibilityVerdict verdict) => verdict switch
    {
        CompatibilityVerdict.Quarantined => 4,
        CompatibilityVerdict.Incompatible => 3,
        CompatibilityVerdict.Inconclusive => 2,
        CompatibilityVerdict.Blocked => 1,
        CompatibilityVerdict.Compatible => 0,
        _ => int.MaxValue,
    };
}
