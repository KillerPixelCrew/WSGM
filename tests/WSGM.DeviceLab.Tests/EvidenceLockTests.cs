using WSGM.DeviceLab.Core.Evidence;

namespace WSGM.DeviceLab.Tests;

/// <summary>
/// The executable specification of the evidence lock: which changes are invisible, which demand
/// review, and why a claim's prose is not one of them.
/// </summary>
public class EvidenceLockTests
{
    [Fact]
    public void BuildIsDeterministicRegardlessOfInputOrder()
    {
        // A diff has to show what changed about the hardware, not how the inputs were enumerated.
        EvidenceLock forward = Build([Claim("a"), Claim("b"), Claim("c")]);
        EvidenceLock reversed = Build([Claim("c"), Claim("b"), Claim("a")]);

        Assert.Equal(
            forward.Claims.Select(c => c.ClaimId),
            reversed.Claims.Select(c => c.ClaimId));
        Assert.Empty(EvidenceLockBuilder.Diff(forward, reversed));
    }

    [Fact]
    public void ChangingAnOffset_IsFlaggedAsAConstantChange()
    {
        // The dangerous change: a constant moves under a claim ID that generated code already cites,
        // and nothing about the generated file's shape would reveal it.
        EvidenceLock before = Build([Claim("power", offset: 4)]);
        EvidenceLock after = Build([Claim("power", offset: 8)]);

        IReadOnlyList<EvidenceLockChange> changes = EvidenceLockBuilder.Diff(before, after);

        Assert.Contains(changes, c => c.Kind == EvidenceChangeKind.ConstantChanged);
        Assert.False(EvidenceLockBuilder.MayAcceptWithoutReview(changes));
    }

    [Theory]
    [InlineData("Documentation only")]
    [InlineData("A much longer explanation of the same field")]
    public void EditingAClaimsProse_ChangesNothing(string meaning)
    {
        // Prose, supporting observations, and limitations do not change a single byte the generated
        // code writes. Invalidating a lock over them would train reviewers to approve diffs blindly.
        EvidenceLock before = Build([Claim("power")]);
        EvidenceLock after = Build([Claim("power") with
        {
            ProposedMeaning = meaning,
            Limitations = ["only observed on AC power"],
            SupportingObservations = ["event-1", "event-2"],
            Repetitions = 12,
            Analyzer = "differ@3",
        }]);

        Assert.Empty(EvidenceLockBuilder.Diff(before, after));
    }

    [Fact]
    public void ChangingTheFirmwareScope_IsAConstantChange()
    {
        // Same offset, different firmware, is a different claim about different hardware.
        EvidenceLock before = Build([Claim("power")]);
        EvidenceLock after = Build([Claim("power") with
        {
            Scope = new ClaimScope
            {
                BaseboardProduct = "MS-1T52",
                EcFirmwareVersion = "1T52EMS1.200",
            },
        }]);

        Assert.Contains(EvidenceLockBuilder.Diff(before, after),
            c => c.Kind == EvidenceChangeKind.ConstantChanged);
    }

    [Fact]
    public void AWeakenedClaim_DemandsReview()
    {
        // A firmware resweep may downgrade a previously verified module. Accepting that silently
        // would leave generated writes in place for evidence that no longer supports them.
        EvidenceLock before = Build([Claim("power", state: ClaimState.HardwareVerified)]);
        EvidenceLock after = Build([Claim("power", state: ClaimState.Correlated)]);

        IReadOnlyList<EvidenceLockChange> changes = EvidenceLockBuilder.Diff(before, after);

        Assert.Contains(changes, c => c.Kind == EvidenceChangeKind.ClaimWeakened);
        Assert.False(EvidenceLockBuilder.MayAcceptWithoutReview(changes));
    }

    [Fact]
    public void ARejectedClaim_CountsAsWeakenedEvenFromALowState()
    {
        EvidenceLock before = Build([Claim("power", state: ClaimState.Candidate)]);
        EvidenceLock after = Build([Claim("power", state: ClaimState.Rejected)]);

        Assert.Contains(EvidenceLockBuilder.Diff(before, after),
            c => c.Kind == EvidenceChangeKind.ClaimWeakened);
    }

    [Fact]
    public void AStrengthenedClaim_IsTheOnlyChangeAcceptedUnattended()
    {
        // It cannot make generated code do something new; write eligibility is granted elsewhere with
        // its own gate.
        EvidenceLock before = Build([Claim("power", state: ClaimState.Corroborated)]);
        EvidenceLock after = Build([Claim("power", state: ClaimState.HardwareVerified)]);

        IReadOnlyList<EvidenceLockChange> changes = EvidenceLockBuilder.Diff(before, after);

        Assert.Contains(changes, c => c.Kind == EvidenceChangeKind.ClaimStrengthened);
        Assert.True(EvidenceLockBuilder.MayAcceptWithoutReview(changes));
    }

    [Fact]
    public void ARemovedClaim_IsReported()
    {
        // Generated code cites claim IDs. One vanishing means the code citing it is now unanchored.
        EvidenceLock before = Build([Claim("power"), Claim("fan")]);
        EvidenceLock after = Build([Claim("power")]);

        Assert.Contains(EvidenceLockBuilder.Diff(before, after),
            c => c.Kind == EvidenceChangeKind.ClaimRemoved && c.Id == "fan");
    }

    [Fact]
    public void ANewClaim_IsReported()
    {
        EvidenceLock before = Build([Claim("power")]);
        EvidenceLock after = Build([Claim("power"), Claim("fan")]);

        Assert.Contains(EvidenceLockBuilder.Diff(before, after),
            c => c.Kind == EvidenceChangeKind.ClaimAdded && c.Id == "fan");
    }

    [Fact]
    public void AModuleVersionBump_DemandsReview()
    {
        EvidenceLock before = EvidenceLockBuilder.Build(
            "ms-1t52", "gen@1", [Claim("power")], [("MsiClawMcu", 1)]);
        EvidenceLock after = EvidenceLockBuilder.Build(
            "ms-1t52", "gen@1", [Claim("power")], [("MsiClawMcu", 2)]);

        IReadOnlyList<EvidenceLockChange> changes = EvidenceLockBuilder.Diff(before, after);

        Assert.Contains(changes, c => c.Kind == EvidenceChangeKind.ModuleVersionChanged);
        Assert.False(EvidenceLockBuilder.MayAcceptWithoutReview(changes));
    }

    [Fact]
    public void AnIdenticalRegeneration_ProducesNoChanges()
    {
        Assert.Empty(EvidenceLockBuilder.Diff(Build([Claim("power")]), Build([Claim("power")])));
        Assert.True(EvidenceLockBuilder.MayAcceptWithoutReview([]));
    }

    [Fact]
    public void FieldsAreHashedPositionallyRatherThanConcatenated()
    {
        // Without a separator, moving a character between adjacent fields would hash identically -
        // a selector "Get_" with endpoint "Data" versus "Get_Data" with no endpoint.
        EvidenceClaim split = Claim("x") with { Selector = "Get_", Endpoint = "Data" };
        EvidenceClaim joined = Claim("x") with { Selector = "Get_Data", Endpoint = null };

        Assert.NotEqual(
            EvidenceLockBuilder.HashClaim(split),
            EvidenceLockBuilder.HashClaim(joined));
    }

    private static EvidenceLock Build(IReadOnlyList<EvidenceClaim> claims) =>
        EvidenceLockBuilder.Build("ms-1t52", "gen@1", claims, []);

    private static EvidenceClaim Claim(
        string id,
        int offset = 4,
        ClaimState state = ClaimState.HardwareVerified) => new()
        {
            ClaimId = id,
            Scope = new ClaimScope
            {
                BaseboardProduct = "MS-1T52",
                EcFirmwareVersion = "1T52EMS1.109",
            },
            Transport = "msi-wmi",
            Selector = "Get_Data",
            Offset = offset,
            WidthBits = 8,
            Endian = Endianness.Little,
            Unit = "W",
            ProposedMeaning = "sustained power limit",
            State = state,
            Provenance = new ClaimProvenance
            {
                Source = "reference unit capture",
                Kind = ProvenanceKind.IndependentCapture,
            },
        };
}
