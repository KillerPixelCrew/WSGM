using WSGM.Device.Contracts.Modules;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Preflight;
using WSGM.DeviceLab.Core.Probes;

namespace WSGM.DeviceLab.Tests;

/// <summary>Reviewed read probes remain exact, bounded, offline-selected, and fully validated.</summary>
public class ReadProbeTests
{
    [Fact]
    public void AutomaticAdmission_RequiresReviewedLocalExactHashPinnedCode()
    {
        ReadProbeMetadata metadata = Metadata();
        ReadProbeAdmissionContext valid = Admission();

        Assert.True(ReadProbeAdmission.Evaluate(metadata, valid).Allowed);
        Assert.Equal(
            "identity.mismatch",
            ReadProbeAdmission.Evaluate(metadata, valid with { EndpointId = "wrong" }).Code);
        Assert.Equal(
            "install.missing",
            ReadProbeAdmission.Evaluate(metadata, valid with { IsLocallyInstalled = false }).Code);
        Assert.Equal(
            "hash.mismatch",
            ReadProbeAdmission.Evaluate(metadata, valid with { InstalledSha256 = new string('b', 64) }).Code);
    }

    [Fact]
    public void DeveloperProbe_RequiresDeveloperModeAndExplicitCurrentAction()
    {
        ReadProbeMetadata metadata = Metadata() with
        {
            Origin = DeviceLabOperationOrigin.SideloadedPackage,
        };
        ReadProbeAdmissionContext context = Admission() with { AutomaticSweep = false };

        Assert.Equal(
            "authority.developer-mode",
            ReadProbeAdmission.Evaluate(metadata, context).Code);
        Assert.Equal(
            "authority.developer-mode",
            ReadProbeAdmission.Evaluate(
                metadata,
                context with { DeveloperModeEnabled = true }).Code);
        Assert.True(ReadProbeAdmission.Evaluate(
            metadata,
            context with
            {
                DeveloperModeEnabled = true,
                ExplicitDeveloperAction = true,
            }).Allowed);
        Assert.Equal(
            "authority.automatic",
            ReadProbeAdmission.Evaluate(
                metadata,
                context with
                {
                    AutomaticSweep = true,
                    DeveloperModeEnabled = true,
                    ExplicitDeveloperAction = true,
                }).Code);
    }

    [Fact]
    public void ImportedArtifact_CannotBecomeExecutableProbeAuthority()
    {
        ReadProbeMetadata metadata = Metadata() with
        {
            Origin = DeviceLabOperationOrigin.ImportedPluginPackage,
        };

        Assert.Equal("authority.imported", ReadProbeAdmission.Evaluate(metadata, Admission()).Code);
    }

    [Fact]
    public void MetadataPolicy_RejectsUnboundedOrStructurallyEmptyDefinitions()
    {
        ReadProbeMetadata invalid = Metadata() with
        {
            Id = "",
            ImplementationSha256 = "not-a-hash",
            MaximumReadsPerSecond = 100,
            TimeoutMilliseconds = 60_000,
            Repetitions = 100,
            ExpectedResponse = Metadata().ExpectedResponse with
            {
                MinimumLength = 20,
                MaximumLength = 10,
                AllowedStatusCodes = [],
            },
        };

        IReadOnlyList<string> errors = ReadProbeMetadataPolicy.Validate(invalid);

        Assert.Contains(errors, error => error.Contains("Probe ID", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SHA-256", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("rate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("deadline", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("repetitions", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("length", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OfflineSelector_ChoosesLowestRiskUniqueProbeWithoutExecutingIt()
    {
        ReadProbeMetadata highRisk = Metadata() with
        {
            Id = "probe.ec",
            Family = ReadProbeFamily.EmbeddedController,
        };
        ReadProbeMetadata lowRisk = Metadata() with
        {
            Id = "probe.native-version",
            Family = ReadProbeFamily.NativeLibraryMetadata,
        };
        IReadOnlyList<CandidateAssessment> assessments =
        [
            Assessment("candidate-a"),
            Assessment("candidate-b"),
        ];
        IReadOnlyList<CatalogEntry> catalog =
        [
            Entry("candidate-a", highRisk, lowRisk),
            Entry("candidate-b", highRisk),
        ];

        ReadProbeSelection selected = ReadProbeSelector.Select(assessments, catalog);

        Assert.Equal("probe.native-version", selected.Probe?.Id);
        Assert.Equal("candidate-a", selected.ModuleId);
    }

    [Fact]
    public void OfflineSelector_DoesNotRecommendAProbeForOneClearLeader()
    {
        ReadProbeSelection selected = ReadProbeSelector.Select(
            [Assessment("candidate-a"), Assessment("candidate-b") with { ReuseRank = 9 }],
            [Entry("candidate-a", Metadata()), Entry("candidate-b", Metadata())]);

        Assert.Null(selected.Probe);
    }

    [Fact]
    public void ResponseValidator_AcceptsOnlyEveryRequiredDimension()
    {
        ReadProbeMetadata metadata = Metadata();
        ReadProbeHostResponse valid = Response();

        Assert.True(ReadProbeResponseValidator.Validate(metadata, valid).Accepted);

        ReadProbeHostResponse[] invalid =
        [
            valid with { ProbeId = "wrong" },
            valid with { HardwareMutationObserved = true },
            valid with { Status = ReadProbeHostStatus.Rejected },
            valid with { Samples = [] },
            valid with { Samples = [Sample() with { ValueKind = ReadProbeValueKind.Text }, Sample()] },
            valid with { Samples = [Sample() with { Length = 9 }, Sample()] },
            valid with { Samples = [Sample() with { StatusCode = 5 }, Sample()] },
            valid with { Samples = [Sample() with { NumericValue = 101 }, Sample()] },
            valid with { Samples = [Sample() with { ElapsedMilliseconds = 1_001 }, Sample()] },
            valid with { Samples = [Sample() with { CrossCheckNumericValue = 101 }, Sample()] },
            valid with { Samples = [Sample(), Sample() with { NormalizedValue = "51" }] },
        ];

        Assert.All(invalid, response =>
            Assert.False(ReadProbeResponseValidator.Validate(metadata, response).Accepted));
    }

    [Fact]
    public void OutcomeClassifier_DistinguishesCrashHangAndMissingOutput()
    {
        ReadProbeRunResult? launch = ReadProbeOutcomeClassifier.ClassifyProcess(Process(started: false));
        ReadProbeRunResult? hang = ReadProbeOutcomeClassifier.ClassifyProcess(Process(timedOut: true));
        ReadProbeRunResult? crash = ReadProbeOutcomeClassifier.ClassifyProcess(Process(exitCode: -1));
        ReadProbeRunResult? missing = ReadProbeOutcomeClassifier.ClassifyProcess(Process(resultProduced: false));
        ReadProbeRunResult? ready = ReadProbeOutcomeClassifier.ClassifyProcess(Process());

        Assert.Equal(ReadProbeRunStatus.LaunchFailed, launch?.Status);
        Assert.Equal(ReadProbeRunStatus.HostHung, hang?.Status);
        Assert.Equal(ReadProbeRunStatus.HostCrashed, crash?.Status);
        Assert.Equal(ReadProbeRunStatus.MalformedResponse, missing?.Status);
        Assert.Null(ready);
    }

    [Fact]
    public void OutcomeClassifier_DistinguishesAccessDeniedDisconnectAndMalformedPayload()
    {
        ReadProbeRunResult access = ReadProbeOutcomeClassifier.ClassifyResponse(
            Metadata(),
            Response() with { Status = ReadProbeHostStatus.AccessDenied });
        ReadProbeRunResult disconnected = ReadProbeOutcomeClassifier.ClassifyResponse(
            Metadata(),
            Response() with { Status = ReadProbeHostStatus.Disconnected });
        ReadProbeRunResult malformed = ReadProbeOutcomeClassifier.ClassifyResponse(
            Metadata(),
            Response() with { Samples = [] });

        Assert.Equal(ReadProbeRunStatus.AccessDenied, access.Status);
        Assert.Equal(ReadProbeRunStatus.Disconnected, disconnected.Status);
        Assert.Equal(ReadProbeRunStatus.MalformedResponse, malformed.Status);
    }

    private static ReadProbeMetadata Metadata() => new()
    {
        Id = "probe.status",
        Version = 1,
        FamilyId = "family.exact",
        EndpointId = "endpoint.exact",
        ResourceId = "power",
        Family = ReadProbeFamily.WmiStatus,
        Origin = DeviceLabOperationOrigin.ReviewedBuiltInCatalog,
        ImplementationSha256 = new string('a', 64),
        MaximumReadsPerSecond = 2,
        TimeoutMilliseconds = 1_000,
        Repetitions = 2,
        ExpectedResponse = new ReadProbeResponseExpectation
        {
            ValueKind = ReadProbeValueKind.Integer,
            MinimumLength = 1,
            MaximumLength = 4,
            AllowedStatusCodes = [0],
            MinimumValue = 0,
            MaximumValue = 100,
            MustBeStable = true,
        },
        CrossCheck = new ReadProbeCrossCheck
        {
            Id = "independent.status",
            Kind = ReadProbeCrossCheckKind.InRange,
            MinimumValue = 0,
            MaximumValue = 100,
        },
        EvidenceOutputId = "probe.status.observations",
    };

    private static ReadProbeAdmissionContext Admission() => new()
    {
        FamilyId = "family.exact",
        EndpointId = "endpoint.exact",
        IsLocallyInstalled = true,
        InstalledSha256 = new string('a', 64),
        DeveloperModeEnabled = false,
        ExplicitDeveloperAction = false,
        AutomaticSweep = true,
    };

    private static ReadProbeSample Sample() => new()
    {
        ValueKind = ReadProbeValueKind.Integer,
        StatusCode = 0,
        Length = 1,
        NumericValue = 50,
        NormalizedValue = "50",
        ElapsedMilliseconds = 5,
        CrossCheckValue = "51",
        CrossCheckNumericValue = 51,
    };

    private static ReadProbeHostResponse Response() => new()
    {
        SchemaVersion = 1,
        ProbeId = "probe.status",
        ProbeVersion = 1,
        Status = ReadProbeHostStatus.Completed,
        Samples = [Sample(), Sample()],
        HardwareMutationObserved = false,
    };

    private static ReadProbeProcessOutcome Process(
        bool started = true,
        bool timedOut = false,
        int? exitCode = 0,
        bool resultProduced = true) => new()
    {
        Started = started,
        TimedOut = timedOut,
        ExitCode = exitCode,
        ResultProduced = resultProduced,
    };

    private static CandidateAssessment Assessment(string id) => new()
    {
        ModuleId = id,
        ModuleVersion = 1,
        ReuseRank = 10,
        EvidenceGrade = EvidenceGrade.Weak,
        WriteEligibility = WriteEligibility.ReadOnly,
    };

    private static CatalogEntry Entry(string id, params ReadProbeMetadata[] probes) => new()
    {
        Module = new ImplementationModule
        {
            Id = id,
            Version = 1,
            Layer = ModuleLayer.Transport,
            DisplayName = id,
            Safety = new ModuleSafety
            {
                Persistence = PersistenceClass.Unknown,
            },
            Recovery = new ModuleRecovery(),
            Provenance = new PackageProvenance
            {
                Source = "test",
                License = "test",
                ProvenanceClass = ProvenanceClass.IndependentCapture,
            },
        },
        ReadProbes = probes,
    };
}
