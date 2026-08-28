using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Evidence;

namespace WSGM.DeviceLab.Tests;

/// <summary>
/// The executable specification of the rule the whole catalog exists to enforce: similarity
/// nominates a candidate, evidence authorizes it, and neither can be derived from the other.
/// </summary>
public class CandidateGradingTests
{
    [Fact]
    public void EligibilityCannotBeReachedFromSimilarity()
    {
        // The signature takes a grade and a quarantine flag. There is no rank parameter, so a
        // high-ranking candidate has no path to write eligibility even by accident. This test exists
        // to fail if someone adds one.
        System.Reflection.ParameterInfo[] parameters = typeof(CandidateGrading)
            .GetMethod(nameof(CandidateGrading.EligibilityFor))!
            .GetParameters();

        Assert.DoesNotContain(parameters, p =>
            p.Name!.Contains("rank", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("similar", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("score", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ATopRankedCandidateWithNoEvidence_StaysReadOnly()
    {
        CandidateAssessment assessment = new()
        {
            ModuleId = "MsiClawA1MPowerPolicy",
            ModuleVersion = 1,
            ReuseRank = int.MaxValue,
            EvidenceGrade = EvidenceGrade.None,
            WriteEligibility = CandidateGrading.EligibilityFor(EvidenceGrade.None, false),
        };

        Assert.Equal(WriteEligibility.ReadOnly, assessment.WriteEligibility);
    }

    [Fact]
    public void TheWeakestClaimDecidesTheGrade()
    {
        // A module is only as usable as its least established constant: one unverified offset in an
        // otherwise verified fan layout still writes to the wrong place.
        EvidenceGrade grade = CandidateGrading.GradeFor(
        [
            Claim(ClaimState.HardwareVerified),
            Claim(ClaimState.HardwareVerified),
            Claim(ClaimState.Correlated),
        ]);

        Assert.Equal(EvidenceGrade.Weak, grade);
    }

    [Fact]
    public void AllVerifiedClaims_GradeHardwareVerified()
    {
        Assert.Equal(EvidenceGrade.HardwareVerified, CandidateGrading.GradeFor(
            [Claim(ClaimState.HardwareVerified), Claim(ClaimState.RetailApproved)]));
    }

    [Fact]
    public void NoClaimsAtAll_GradesNone()
    {
        Assert.Equal(EvidenceGrade.None, CandidateGrading.GradeFor([]));
    }

    [Fact]
    public void ARejectedClaim_DropsTheGradeToNone()
    {
        Assert.Equal(EvidenceGrade.None, CandidateGrading.GradeFor(
            [Claim(ClaimState.HardwareVerified), Claim(ClaimState.Rejected)]));
    }

    [Theory]
    [InlineData(EvidenceGrade.None, WriteEligibility.ReadOnly)]
    [InlineData(EvidenceGrade.Weak, WriteEligibility.ReadOnly)]
    [InlineData(EvidenceGrade.Corroborated, WriteEligibility.TrialOnly)]
    [InlineData(EvidenceGrade.HardwareVerified, WriteEligibility.Production)]
    public void EligibilityFollowsTheGrade(EvidenceGrade grade, WriteEligibility expected)
    {
        Assert.Equal(expected, CandidateGrading.EligibilityFor(grade, resourceQuarantined: false));
    }

    [Theory]
    [InlineData(EvidenceGrade.None)]
    [InlineData(EvidenceGrade.Corroborated)]
    [InlineData(EvidenceGrade.HardwareVerified)]
    public void QuarantineOverridesEveryGrade(EvidenceGrade grade)
    {
        // A failed restoration means the device is in a state nobody established. Writing more into
        // it is the worst available move, however good the evidence looked beforehand.
        Assert.Equal(WriteEligibility.Quarantined,
            CandidateGrading.EligibilityFor(grade, resourceQuarantined: true));
    }

    [Theory]
    [InlineData(ClaimState.Candidate)]
    [InlineData(ClaimState.Correlated)]
    [InlineData(ClaimState.Corroborated)]
    [InlineData(ClaimState.Rejected)]
    public void GeneratedWrites_RequireHardwareProof(ClaimState state)
    {
        // Agreement between three independent projects is still not evidence about this board.
        Assert.False(ClaimStatePolicy.MayGenerateWrite(state));
    }

    [Theory]
    [InlineData(ClaimState.HardwareVerified)]
    [InlineData(ClaimState.RetailApproved)]
    public void OnlyProvenClaimsGenerateWrites(ClaimState state)
    {
        Assert.True(ClaimStatePolicy.MayGenerateWrite(state));
    }

    [Fact]
    public void CorroboratedIsTheEntryPointForATrial()
    {
        // Enough evidence to be worth testing on hardware, not enough to ship. Anything weaker would
        // point a write at an address suggested by a single observation.
        Assert.True(ClaimStatePolicy.MayAttemptTrial(ClaimState.Corroborated));
        Assert.False(ClaimStatePolicy.MayAttemptTrial(ClaimState.Correlated));
        Assert.False(ClaimStatePolicy.MayAttemptTrial(ClaimState.Candidate));
    }

    [Fact]
    public void ReadsArePermittedEarlierThanWrites()
    {
        // A wrong read reports a wrong number; a wrong write changes hardware.
        Assert.True(ClaimStatePolicy.MayGenerateRead(ClaimState.Candidate));
        Assert.False(ClaimStatePolicy.MayGenerateRead(ClaimState.Rejected));
    }

    private static EvidenceClaim Claim(ClaimState state) => new()
    {
        ClaimId = $"claim-{state}",
        Scope = new ClaimScope { BaseboardProduct = "MS-1T52" },
        Transport = "msi-wmi",
        Selector = "Get_Data",
        ProposedMeaning = "sustained power limit",
        State = state,
        Provenance = new ClaimProvenance
        {
            Source = "reference unit capture",
            Kind = ProvenanceKind.IndependentCapture,
        },
    };
}
