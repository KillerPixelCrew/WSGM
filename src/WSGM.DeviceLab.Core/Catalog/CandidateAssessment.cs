using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.DeviceLab.Core.Evidence;

namespace WSGM.DeviceLab.Core.Catalog;

/// <summary>
/// How much a candidate module could be reused on a device, expressed as three values that are
/// deliberately computed apart.
/// </summary>
/// <remarks>
/// Keeping them separate is the whole design. Reuse rank answers "how similar is this hardware",
/// evidence grade answers "how well established is the claim", and write eligibility answers "may
/// generated code write". Collapsing any two produces the failure this system exists to prevent: a
/// module that looks similar acquiring permission to write, which is how one board's power limits end
/// up applied to another.
/// <para>
/// A top-ranked candidate with no hardware evidence stays read-only, and nothing about its rank can
/// change that.
/// </para>
/// </remarks>
public sealed record CandidateAssessment
{
    /// <summary>The module being assessed.</summary>
    public required string ModuleId { get; init; }

    /// <summary>Module version considered.</summary>
    public required int ModuleVersion { get; init; }

    /// <summary>
    /// How well the device's observations match what this module expects. Ordering only.
    /// </summary>
    public required int ReuseRank { get; init; }

    /// <summary>
    /// How well established the module's claims are for this exact device.
    /// </summary>
    /// <remarks>
    /// Derived from the claims themselves, never from <see cref="ReuseRank"/>. Similarity is not
    /// evidence; it is a reason to go looking for some.
    /// </remarks>
    public required EvidenceGrade EvidenceGrade { get; init; }

    /// <summary>Whether generated code may write to hardware through this module.</summary>
    public required WriteEligibility WriteEligibility { get; init; }

    /// <summary>Why each hard constraint passed or failed, in evaluation order.</summary>
    public IReadOnlyList<string> Explanations { get; init; } = [];

    /// <summary>
    /// Device-specific values this module holds that must not be inherited.
    /// </summary>
    /// <remarks>
    /// Listed explicitly so a developer reusing a transport sees exactly what does not come with it:
    /// ranges, offsets, tables, persistence assumptions, and recovery policy.
    /// </remarks>
    public IReadOnlyList<string> NonInheritableValues { get; init; } = [];
}

/// <summary>How well established a candidate's claims are for the target device.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvidenceGrade>))]
public enum EvidenceGrade
{
    /// <summary>No evidence for this device.</summary>
    None,

    /// <summary>Claims exist but only as candidates or correlations.</summary>
    Weak,

    /// <summary>Claims are corroborated by repeated or independent evidence.</summary>
    Corroborated,

    /// <summary>Claims are verified on this exact device.</summary>
    HardwareVerified,
}

/// <summary>Whether generated code may write through a module.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WriteEligibility>))]
public enum WriteEligibility
{
    /// <summary>Reads only.</summary>
    ReadOnly,

    /// <summary>May be exercised by an explicitly authorized bounded trial.</summary>
    TrialOnly,

    /// <summary>May be generated as a production write path.</summary>
    Production,

    /// <summary>Blocked: a restoration failed, and the resource is quarantined.</summary>
    Quarantined,
}

/// <summary>
/// Derives the three candidate values without letting one contaminate another.
/// </summary>
public static class CandidateGrading
{
    /// <summary>
    /// Derives the evidence grade from a module's claims for one device.
    /// </summary>
    /// <param name="claims">Claims scoped to the target device.</param>
    /// <returns>The grade the weakest link supports.</returns>
    /// <remarks>
    /// The <em>weakest</em> claim decides, not the best or the average. A module is only as usable as
    /// its least established constant: one unverified offset in an otherwise verified fan layout still
    /// writes to the wrong place.
    /// </remarks>
    public static EvidenceGrade GradeFor(IReadOnlyList<EvidenceClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        if (claims.Count == 0)
        {
            return EvidenceGrade.None;
        }

        EvidenceGrade weakest = EvidenceGrade.HardwareVerified;

        foreach (EvidenceClaim claim in claims)
        {
            EvidenceGrade grade = claim.State switch
            {
                ClaimState.HardwareVerified or ClaimState.RetailApproved => EvidenceGrade.HardwareVerified,
                ClaimState.Corroborated => EvidenceGrade.Corroborated,
                ClaimState.Candidate or ClaimState.Correlated => EvidenceGrade.Weak,
                _ => EvidenceGrade.None,
            };

            if (grade < weakest)
            {
                weakest = grade;
            }
        }

        return weakest;
    }

    /// <summary>
    /// Derives write eligibility from the evidence grade and resource health.
    /// </summary>
    /// <param name="grade">The module's evidence grade for this device.</param>
    /// <param name="resourceQuarantined">Whether the target resource is quarantined.</param>
    /// <returns>What generated code may do.</returns>
    /// <remarks>
    /// Takes the grade and nothing else — no rank parameter exists, so similarity cannot reach this
    /// decision even by accident. Quarantine overrides everything: a failed restoration means the
    /// device is in a state nobody established, and writing more into it is the worst available move.
    /// </remarks>
    public static WriteEligibility EligibilityFor(EvidenceGrade grade, bool resourceQuarantined)
    {
        if (resourceQuarantined)
        {
            return WriteEligibility.Quarantined;
        }

        return grade switch
        {
            EvidenceGrade.HardwareVerified => WriteEligibility.Production,
            EvidenceGrade.Corroborated => WriteEligibility.TrialOnly,
            _ => WriteEligibility.ReadOnly,
        };
    }
}
