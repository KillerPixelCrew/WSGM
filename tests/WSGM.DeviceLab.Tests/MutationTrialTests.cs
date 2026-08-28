using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Preflight;
using WSGM.DeviceLab.Core.Trials;

namespace WSGM.DeviceLab.Tests;

/// <summary>Mutation authority is exact, interactive, state-pinned, journalled, and fail-closed.</summary>
public class MutationTrialTests
{
    [Fact]
    public void Authorization_RequiresExactLocalReviewAndExperimentLease()
    {
        MutationTrialMetadata metadata = Metadata();
        MutationTrialAuthorizationSnapshot snapshot = Snapshot();

        MutationTrialAuthorization authorized = MutationTrialAuthorizationPolicy.Authorize(
            metadata,
            Review(metadata),
            snapshot);
        MutationTrialAuthorization unattended = MutationTrialAuthorizationPolicy.Authorize(
            metadata,
            Review(metadata),
            snapshot with { IsUnattended = true });
        MutationTrialAuthorization nested = MutationTrialAuthorizationPolicy.Authorize(
            metadata,
            Review(metadata),
            snapshot with { NestedTrialActive = true });
        MutationTrialAuthorization wrongLease = MutationTrialAuthorizationPolicy.Authorize(
            metadata,
            Review(metadata),
            snapshot with
            {
                Preflight = snapshot.Preflight with
                {
                    Route = DeviceLabAccessRoute.DirectReadOnly,
                    RequiredLease = null,
                },
            });

        Assert.True(authorized.Granted);
        Assert.Equal("interaction.required", unattended.Code);
        Assert.Equal("trial.nested", nested.Code);
        Assert.Equal("preflight.mismatch", wrongLease.Code);
    }

    [Fact]
    public void Authorization_ExpiresWhenAnyPinnedSafetyStateChanges()
    {
        MutationTrialMetadata metadata = Metadata();
        MutationTrialAuthorizationSnapshot snapshot = Snapshot();
        MutationTrialAuthorization authorization = MutationTrialAuthorizationPolicy.Authorize(
            metadata,
            Review(metadata),
            snapshot);

        Assert.True(MutationTrialAuthorizationPolicy.IsCurrent(authorization, metadata, snapshot));
        Assert.False(MutationTrialAuthorizationPolicy.IsCurrent(
            authorization,
            metadata,
            snapshot with { FirmwareIdentity = "firmware-2" }));
        Assert.False(MutationTrialAuthorizationPolicy.IsCurrent(
            authorization,
            metadata,
            snapshot with { ModuleVersion = 2 }));
        Assert.False(MutationTrialAuthorizationPolicy.IsCurrent(
            authorization,
            metadata,
            snapshot with { OriginalStateSha256 = new string('c', 64) }));
        Assert.False(MutationTrialAuthorizationPolicy.IsCurrent(
            authorization,
            metadata,
            snapshot with
            {
                Preflight = snapshot.Preflight with { DeviceGeneration = 12 },
            }));
        Assert.False(MutationTrialAuthorizationPolicy.IsCurrent(
            authorization,
            metadata,
            snapshot with { Now = snapshot.Now.AddMinutes(3) }));
    }

    [Fact]
    public void ReviewReceipt_CannotUseGenericYesOrOmitARequiredField()
    {
        MutationTrialMetadata metadata = Metadata();
        MutationTrialReviewReceipt genericYes = Review(metadata) with { ConfirmedTrialId = "yes" };
        MutationTrialReviewReceipt incomplete = Review(metadata) with
        {
            ReviewedFields = MutationTrialReviewField.All & ~MutationTrialReviewField.Recovery,
        };

        Assert.Equal(
            "review.incomplete",
            MutationTrialAuthorizationPolicy.Authorize(metadata, genericYes, Snapshot()).Code);
        Assert.Equal(
            "review.incomplete",
            MutationTrialAuthorizationPolicy.Authorize(metadata, incomplete, Snapshot()).Code);
    }

    [Fact]
    public void Metadata_RequiresOneBoundedRecoverableCapabilityAndNamesPermanentExclusions()
    {
        IReadOnlyList<string> errors = MutationTrialMetadataPolicy.Validate(Metadata() with
        {
            MaximumWrites = 20,
            MaximumRetries = 4,
            RollbackVerified = false,
        });

        Assert.Equal(3, errors.Count);
        Assert.Contains(MutationTrialMetadataPolicy.PermanentlyExcludedOperations, operation =>
            operation.Contains("firmware flashing", StringComparison.Ordinal));
        Assert.Contains(MutationTrialMetadataPolicy.PermanentlyExcludedOperations, operation =>
            operation.Contains("blind bus", StringComparison.Ordinal));
        Assert.Contains(MutationTrialMetadataPolicy.PermanentlyExcludedOperations, operation =>
            operation.Contains("test-signing", StringComparison.Ordinal));
    }

    [Fact]
    public void FaultHarness_NeverOverstatesCleanupAcrossAnyTransactionalDeathPoint()
    {
        foreach (MutationTrialFaultPoint point in Enum.GetValues<MutationTrialFaultPoint>())
        {
            MutationTrialOutcome outcome = MutationTrialFaultHarness.Simulate("power", point);

            if (point is MutationTrialFaultPoint.AfterApplyingJournal
                or MutationTrialFaultPoint.AfterApply
                or MutationTrialFaultPoint.AfterObservation
                or MutationTrialFaultPoint.AfterRollbackStarted
                or MutationTrialFaultPoint.AfterRestore)
            {
                Assert.Equal(CompatibilityVerdict.Quarantined, outcome.Result.Verdict);
                Assert.Equal("power", outcome.QuarantinedResourceId);
            }
            else
            {
                Assert.Null(outcome.QuarantinedResourceId);
            }
        }
    }

    [Fact]
    public void FaultHarness_RecordsOriginalBeforeAnyAmbiguousWriteAndVerifiesFinalRestore()
    {
        MutationTrialOutcome ambiguous = MutationTrialFaultHarness.Simulate(
            "power",
            MutationTrialFaultPoint.AfterApplyingJournal);
        MutationTrialOutcome success = MutationTrialFaultHarness.Simulate(
            "power",
            MutationTrialFaultPoint.None);

        Assert.Equal(
            [JournalEntryStatus.Planned, JournalEntryStatus.Applying],
            ambiguous.JournalStates);
        Assert.Equal(JournalEntryStatus.RestoredVerified, success.JournalStates[^1]);
        Assert.Equal(CompatibilityVerdict.Compatible, success.Result.Verdict);
    }

    [Fact]
    public async Task Journal_FlushesImmutableStatesAndReconcilesOnlyItsNamedResource()
    {
        using TestDirectory temp = new();
        string session = Path.Combine(temp.Path, "trial-session");
        MutationTrialJournal journal = new(session, temp.Boundaries());
        RecoveryJournalEntry planned = Entry(1, JournalEntryStatus.Planned);
        RecoveryJournalEntry applying = Entry(2, JournalEntryStatus.Applying);

        await journal.AppendAsync(planned, CancellationToken.None);
        await journal.AppendAsync(applying, CancellationToken.None);
        MutationTrialJournalReadResult read = journal.Read();

        Assert.Equal([1L, 2L], read.Entries.Select(entry => entry.Sequence));
        Assert.Equal(42, read.Entries[0].OriginalValue?.IntegerValue);
        Assert.Empty(read.CorruptFiles);
        Assert.Equal(
            ReconciliationAction.Restore,
            MutationTrialJournalReconciliation.Decide(Metadata(), read, "firmware-1"));
        Assert.Equal(
            ReconciliationAction.ReportOnly,
            MutationTrialJournalReconciliation.Decide(Metadata(), read, "changed-firmware"));
    }

    [Fact]
    public async Task CorruptOrCrossResourceJournal_QuarantinesOnlyTheTrialResource()
    {
        using TestDirectory temp = new();
        MutationTrialJournal journal = new(
            Path.Combine(temp.Path, "trial-session"),
            temp.Boundaries());
        await journal.AppendAsync(
            Entry(1, JournalEntryStatus.Applying) with { ResourceId = "fan" },
            CancellationToken.None);

        Assert.Equal(
            ReconciliationAction.Quarantine,
            MutationTrialJournalReconciliation.Decide(Metadata(), journal.Read(), "firmware-1"));
    }

    private static MutationTrialMetadata Metadata() => new()
    {
        Id = "trial.power.one-step",
        Version = 1,
        ImplementationSha256 = new string('a', 64),
        FamilyId = "family-exact",
        BoardId = "board-exact",
        FirmwareIdentities = ["firmware-1"],
        EndpointId = "wmi-power",
        ResourceId = "power",
        Family = MutationTrialFamily.TemporaryPowerPair,
        ModuleVersion = 1,
        MaximumWrites = 4,
        Actions = ["read exact pair", "apply one step", "read back", "restore exact pair"],
        ExpectedEffect = "Temporary one-step power pair change.",
        IndependentObservation = "Read back the exact pair from the reviewed getter.",
        Rollback = "Write the captured exact pair and verify it.",
        EmergencyAction = "Write the captured exact pair through the independent recovery entry point.",
        TimeoutMilliseconds = 10_000,
        MaximumRetries = 1,
        CooldownSeconds = 30,
        RollbackVerified = true,
        DeviceVolatile = true,
    };

    private static MutationTrialReviewReceipt Review(MutationTrialMetadata metadata) => new()
    {
        ConfirmedTrialId = metadata.Id,
        ReviewedFields = MutationTrialReviewField.All,
        ReviewSha256 = MutationTrialMetadataPolicy.ReviewSha256(metadata),
        ConfirmedAt = DateTimeOffset.UnixEpoch,
    };

    private static MutationTrialAuthorizationSnapshot Snapshot() => new()
    {
        Preflight = new DeviceLabPreflightDecision
        {
            ResourceId = "power",
            Status = DeviceLabDoctorStatus.Pass,
            Route = DeviceLabAccessRoute.ExperimentLease,
            RequiredLease = LeaseKind.Experiment,
            HostGeneration = 7,
            DeviceGeneration = 11,
            ResourceState = ResourceState.Idle,
        },
        FamilyId = "family-exact",
        BoardId = "board-exact",
        FirmwareIdentity = "firmware-1",
        EndpointId = "wmi-power",
        InstalledSha256 = new string('a', 64),
        ModuleVersion = 1,
        OriginalStateSha256 = new string('b', 64),
        IsInteractive = true,
        IsUnattended = false,
        IsContinuousIntegration = false,
        NestedTrialActive = false,
        Now = DateTimeOffset.UnixEpoch.AddSeconds(30),
    };

    private static RecoveryJournalEntry Entry(long sequence, JournalEntryStatus status) => new()
    {
        Sequence = sequence,
        PackageId = "wsgm-device-lab",
        DeviceId = "board-exact",
        HostGeneration = 7,
        DeviceGeneration = 11,
        ResourceId = "power",
        CapabilityId = "power.primary-limit",
        FirmwareIdentity = "firmware-1",
        OriginalValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = 42,
        },
        PlannedValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = 43,
        },
        Status = status,
        OpenedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());

        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                _tempRoot,
                $"wsgm-trial-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public DeviceLabPathBoundaries Boundaries() => new()
        {
            LiveDataDirectory = System.IO.Path.Combine(_tempRoot, "never-live-wsgm"),
            RepositoryRoot = System.IO.Path.Combine(_tempRoot, "never-repository"),
            BroadHomeDirectories = [],
        };

        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Test cleanup escaped the system temporary directory.");
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
