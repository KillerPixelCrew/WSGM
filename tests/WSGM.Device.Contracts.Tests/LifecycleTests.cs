using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of fault handling, deactivation budgets, and what may be restored
/// after a crash.
/// </summary>
public class LifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_TheFirstFault_RestartsAfterTheInitialBackoff()
    {
        FaultResponse response = RestartPolicy.Default.Evaluate(0, out TimeSpan backoff);

        Assert.Equal(FaultResponse.Restart, response);
        Assert.Equal(RestartPolicy.Default.InitialBackoff, backoff);
    }

    [Fact]
    public void Evaluate_BackoffGrowsWithEachFault()
    {
        RestartPolicy.Default.Evaluate(0, out TimeSpan first);
        RestartPolicy.Default.Evaluate(1, out TimeSpan second);
        RestartPolicy.Default.Evaluate(2, out TimeSpan third);

        Assert.True(first < second);
        Assert.True(second < third);
    }

    [Fact]
    public void Evaluate_BackoffIsCapped()
    {
        RestartPolicy policy = new() { MaxRestarts = 100 };

        policy.Evaluate(50, out TimeSpan backoff);

        Assert.Equal(policy.MaxBackoff, backoff);
    }

    [Fact]
    public void Evaluate_ExhaustingTheBudget_Quarantines()
    {
        // An unrecoverable fault repeats. Without a budget the plugin would reacquire hardware,
        // crash, and reacquire again several times a second, touching the device each time.
        FaultResponse response = RestartPolicy.Default
            .Evaluate(RestartPolicy.Default.MaxRestarts, out TimeSpan backoff);

        Assert.Equal(FaultResponse.Quarantine, response);
        Assert.Equal(TimeSpan.Zero, backoff);
    }

    [Fact]
    public void DeactivationBudget_IsBoundedAndOrdered()
    {
        // Session end gets less of everything: Windows will not wait as long as a user quitting.
        Assert.True(DeactivationBudget.SessionEnd.Total < DeactivationBudget.Normal.Total);
        Assert.True(DeactivationBudget.Normal.Total <= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void MayStartWrite_AWriteThatCannotFinishInTheRemainingTime_IsRefused()
    {
        // Starting one anyway is how a device is left half-configured with a journal entry that says
        // only "applying".
        Assert.False(DeactivationBudget.MayStartWrite(
            remaining: TimeSpan.FromMilliseconds(100),
            expectedDuration: TimeSpan.FromMilliseconds(500)));

        Assert.True(DeactivationBudget.MayStartWrite(
            remaining: TimeSpan.FromSeconds(2),
            expectedDuration: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void Decide_AnAppliedChangeWithACapturedOriginal_IsRestored()
    {
        ReconciliationAction action = JournalReconciliation.Decide(
            Entry(JournalEntryStatus.AppliedVerified), "1T52EMS1.109");

        Assert.Equal(ReconciliationAction.Restore, action);
    }

    [Fact]
    public void Decide_AnEntryWithNoCapturedOriginal_IsReportedNotRestored()
    {
        // Substituting a plausible factory default would invent a value the device may never have had.
        RecoveryJournalEntry entry = Entry(JournalEntryStatus.AppliedUnverified) with
        {
            OriginalValue = null,
        };

        Assert.Equal(ReconciliationAction.ReportOnly,
            JournalReconciliation.Decide(entry, "1T52EMS1.109"));
    }

    [Fact]
    public void Decide_AnEntryFromDifferentFirmware_IsReportedNotRestored()
    {
        // Offsets and ranges may have moved, so putting an old value back could write it somewhere
        // else entirely.
        Assert.Equal(ReconciliationAction.ReportOnly,
            JournalReconciliation.Decide(Entry(JournalEntryStatus.AppliedVerified), "1T52EMS1.200"));
    }

    [Fact]
    public void Decide_APreviouslyFailedRestore_StaysQuarantined()
    {
        // Retrying automatically is how one failure becomes a loop of them.
        Assert.Equal(ReconciliationAction.Quarantine,
            JournalReconciliation.Decide(Entry(JournalEntryStatus.RestoreFailed), "1T52EMS1.109"));
    }

    [Fact]
    public void Decide_APlannedChangeThatNeverGotWritten_NeedsNothing()
    {
        Assert.Equal(ReconciliationAction.None,
            JournalReconciliation.Decide(Entry(JournalEntryStatus.Planned), "1T52EMS1.109"));
    }

    [Fact]
    public void Decide_AVerifiedRestore_NeedsNothing()
    {
        Assert.Equal(ReconciliationAction.None,
            JournalReconciliation.Decide(Entry(JournalEntryStatus.RestoredVerified), "1T52EMS1.109"));
    }

    [Fact]
    public void Decide_AnAbandonedEntry_IsRestoredWhenItsOriginalAndFirmwareStillHold()
    {
        // The host died between writing and restoring. This is the case the journal exists for.
        Assert.Equal(ReconciliationAction.Restore,
            JournalReconciliation.Decide(Entry(JournalEntryStatus.Abandoned), "1T52EMS1.109"));
    }

    [Fact]
    public void Outstanding_ReturnsOnlyEntriesNeedingAttention_NewestFirst()
    {
        RecoveryJournalEntry[] entries =
        [
            Entry(JournalEntryStatus.RestoredVerified) with { Sequence = 1 },
            Entry(JournalEntryStatus.AppliedUnverified) with { Sequence = 2 },
            Entry(JournalEntryStatus.Planned) with { Sequence = 3 },
            Entry(JournalEntryStatus.RestoreFailed) with { Sequence = 4 },
        ];

        long[] outstanding = JournalReconciliation.Outstanding(entries)
            .Select(e => e.Sequence)
            .ToArray();

        Assert.Equal([4L, 2L], outstanding);
    }

    private static RecoveryJournalEntry Entry(JournalEntryStatus status) => new()
    {
        Sequence = 1,
        PackageId = "wsgm.device.msi.claw-8-a2vm",
        DeviceId = "ms-1t52",
        HostGeneration = 3,
        DeviceGeneration = 7,
        ResourceId = "msi-acpi",
        CapabilityId = "power.primary-limit",
        FirmwareIdentity = "1T52EMS1.109",
        OriginalValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = 30,
        },
        PlannedValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = 18,
        },
        Status = status,
        OpenedAt = Now,
    };
}
