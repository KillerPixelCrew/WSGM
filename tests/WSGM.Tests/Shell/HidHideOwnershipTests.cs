using WSGM.Device.Sdk.Input;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class HidHideOwnershipTests
{
    [Fact]
    public async Task ApplyAndCleanupPreserveEveryExternalEntryAndItsOrdering()
    {
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["HC.exe", "external.exe"],
            devices: ["HID\\PRE-A", "HID\\PRE-B"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);

        var activation = await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);
        Assert.True(activation.Activated);

        adapter.ExternalReplace(
            applications: ["external-new.exe", "HC.exe", "external.exe", "WSGM.exe"],
            devices: ["HID\\PRE-B", "HID\\NEW", "HID\\PRE-A", "HID\\OWN"]);

        var cleanup = await manager.CleanupAsync(
            CancellationToken.None);
        var final = await adapter.ReadAsync(CancellationToken.None);

        Assert.True(cleanup.Verified);
        Assert.Equal(["external-new.exe", "HC.exe", "external.exe"], final.Applications);
        Assert.Equal(["HID\\PRE-B", "HID\\NEW", "HID\\PRE-A"], final.Devices);
        Assert.True(final.Active);
        Assert.Null(store.Ledger);
    }

    [Fact]
    public async Task PreexistingEquivalentEntriesAreNeverClaimedOrRemoved()
    {
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["wsgm.EXE"],
            devices: ["hid\\own"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);

        var activation = await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);
        var cleanup = await manager.CleanupAsync(
            CancellationToken.None);
        var final = await adapter.ReadAsync(CancellationToken.None);

        Assert.True(activation.Activated);
        Assert.True(cleanup.Verified);
        Assert.Equal(["wsgm.EXE"], final.Applications);
        Assert.Equal(["hid\\own"], final.Devices);
        Assert.Equal(0, adapter.MutationCount);
    }

    [Fact]
    public async Task AmbiguousDuplicateOwnedValueIsPreservedForExplicitRecovery()
    {
        DeterministicFakeHidHideAdapter adapter = new();
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);
        await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);
        adapter.ExternalReplace(
            applications: ["WSGM.exe", "WSGM.exe"],
            devices: ["HID\\OWN"]);

        var cleanup = await manager.CleanupAsync(
            CancellationToken.None);
        var final = await adapter.ReadAsync(CancellationToken.None);

        Assert.False(cleanup.Verified);
        Assert.Equal(["WSGM.exe", "WSGM.exe"], final.Applications);
        Assert.Empty(final.Devices);
        Assert.NotNull(store.Ledger);
        Assert.Contains("Application:WSGM.exe", cleanup.Detail);
    }

    [Fact]
    public async Task PartialActivationFailureRollsBackOnlyAppliedOwnedDeltas()
    {
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["external.exe"],
            devices: ["HID\\EXTERNAL"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);
        adapter.FailMutationAttempt = 2;

        var activation = await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);

        var final = await adapter.ReadAsync(CancellationToken.None);
        Assert.False(activation.Activated);
        Assert.Equal(["external.exe"], final.Applications);
        Assert.Equal(["HID\\EXTERNAL"], final.Devices);
        Assert.Null(store.Ledger);
    }

    [Fact]
    public async Task InactiveGlobalStateFailsWithoutChangingIt()
    {
        DeterministicFakeHidHideAdapter adapter = new(active: false);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);

        var activation = await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);

        Assert.False(activation.Activated);
        Assert.Equal(0, adapter.MutationCount);
        var final = await adapter.ReadAsync(CancellationToken.None);
        Assert.False(final.Active);
    }

    [Fact]
    public async Task AnOrphanedLedgerIsRecoveredRatherThanBlockingForever()
    {
        // The ledger exists precisely for "WSGM died holding HidHide entries", so finding one from
        // a previous run is the case it was written for. Refusing it instead would cost controller
        // management for good after one crash.
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["HC.exe"],
            devices: ["HID\\PRE"]);
        InMemoryHidHideOwnershipStore store = new();

        // A first session hides a device and then vanishes, leaving its ledger behind.
        HidHideOwnedDeltaManager crashed = new(adapter, store);
        Assert.True((await crashed.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None)).Activated);
        Assert.NotNull(store.Ledger);

        // A new session finds it.
        HidHideOwnedDeltaManager restarted = new(adapter, store);
        var result = await restarted.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);

        Assert.True(result.Activated);

        // And the recovery actually restored the previous run's entry rather than stacking on it:
        // the external device is still hidden exactly once, alongside this session's own.
        var snapshot = await adapter.ReadAsync(CancellationToken.None);
        Assert.Equal(["HID\\PRE", "HID\\OWN"], snapshot.Devices);
    }

    [Fact]
    public async Task WsgmAllowsItselfBeforeItNeedsToReadDevicesSomethingElseHid()
    {
        // The ordering that mattered on real hardware: another tool had already hidden the pad, so
        // the plugin could not see the device it was being asked to discover, and the allowlisting
        // that would have fixed it only ran later as part of WSGM's own hiding transaction.
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["HC.exe"],
            devices: ["HID\\SOMEONE-ELSES-PAD"]);
        HidHideOwnedDeltaManager manager = new(adapter, new InMemoryHidHideOwnershipStore());

        var detail = await manager.EnsureReadableAsync(
            controllerManagementEnabled: true,
            "WSGM.exe",
            CancellationToken.None);

        var snapshot = await adapter.ReadAsync(CancellationToken.None);
        Assert.Contains("WSGM.exe", snapshot.Applications);
        Assert.Contains("allowlist", detail, StringComparison.Ordinal);

        // It grants WSGM sight; it must never hide anything or disturb another owner's entries.
        Assert.Equal(["HID\\SOMEONE-ELSES-PAD"], snapshot.Devices);
        Assert.Contains("HC.exe", snapshot.Applications);
    }

    [Fact]
    public async Task NothingHiddenMeansNothingToAllow()
    {
        // The normal machine. WSGM must not add itself to an allowlist that is guarding nothing.
        DeterministicFakeHidHideAdapter adapter = new();
        HidHideOwnedDeltaManager manager = new(adapter, new InMemoryHidHideOwnershipStore());

        await manager.EnsureReadableAsync(true, "WSGM.exe", CancellationToken.None);

        Assert.Equal(0, adapter.MutationCount);
    }

    [Fact]
    public async Task ManagementOffNeverConsultsHidHideForReadability()
    {
        DeterministicFakeHidHideAdapter adapter = new(devices: ["HID\\PRE"]);
        HidHideOwnedDeltaManager manager = new(adapter, new InMemoryHidHideOwnershipStore());

        await manager.EnsureReadableAsync(false, "WSGM.exe", CancellationToken.None);

        Assert.Equal(0, adapter.ReadCount);
        Assert.Equal(0, adapter.MutationCount);
    }

    private static PhysicalDeviceIdentity Physical(string path) => new()
    {
        InstancePath = path,
        RequiresHiding = true
    };

    // WSGM has to recognise its own HidHide entries in the notation HidHide stores them in.
    // HidHide keeps application entries as NT device paths — \Device\HarddiskVolume3\… — while
    // WSGM knows its executables by drive letter. A plain string compare therefore never matched, so
    // WSGM added a second entry for a path that was already present: the allowlist grew on every
    // activation, and cleanup, which matches what it wrote, would leave the other notation behind.
    // Device-observed on the reference Claw, 2026-08-29.
    private const string DosPath = @"C:\Program Files\WSGM\WSGM.exe";

    private const string DevicePath =
        @"\Device\HarddiskVolume3\Program Files\WSGM\WSGM.exe";

    [Fact]
    public void AnEntryStoredAsADevicePathIsRecognisedFromItsDriveLetterForm()
    {
        // The exact case that produced the duplicate.
        Assert.True(HidHideOwnedDeltaManager.Contains([DevicePath], DosPath));
    }

    [Fact]
    public void AndTheOtherWayRound()
    {
        Assert.True(HidHideOwnedDeltaManager.Contains([DosPath], DevicePath));
    }

    [Fact]
    public void AnExactMatchStillMatches()
    {
        Assert.True(HidHideOwnedDeltaManager.Contains([DosPath], DosPath));
        Assert.True(HidHideOwnedDeltaManager.Contains([DevicePath], DevicePath));
    }

    [Fact]
    public void TheVolumeNumberIsNotWhatIdentifiesTheFile()
    {
        // Volume numbering is assigned by Windows and is not stable across machines or boots, so it
        // must not be part of the comparison.
        Assert.True(HidHideOwnedDeltaManager.Contains(
            [@"\Device\HarddiskVolume7\Program Files\WSGM\WSGM.exe"],
            DosPath));
    }

    [Fact]
    public void ADifferentProgramIsNotMatched()
    {
        Assert.False(HidHideOwnedDeltaManager.Contains(
            [@"\Device\HarddiskVolume3\Program Files\Handheld Companion\HandheldCompanion.exe"],
            DosPath));
    }

    [Fact]
    public void ADifferentPathToASameNamedProgramIsNotMatched()
    {
        // Only the volume prefix is ignored. Everything that identifies the file still has to agree.
        Assert.False(HidHideOwnedDeltaManager.Contains(
            [@"C:\Other\WSGM\WSGM.exe"],
            DosPath));
    }

    [Fact]
    public void DeviceInstancePathsAreLeftAlone()
    {
        // The device list never had this problem: instance paths carry no volume prefix, so they
        // must pass through untouched and keep comparing exactly.
        const string instance = @"HID\VID_0DB0&PID_1902&MI_00&COL01\7&3222ED46&0&0000";

        Assert.Equal(instance, HidHideOwnedDeltaManager.NormalizePath(instance));
        Assert.True(HidHideOwnedDeltaManager.Contains([instance], instance));
        Assert.False(HidHideOwnedDeltaManager.Contains(
            [@"HID\VID_0DB0&PID_1901&IG_00\8&1717EFAA&0&0000"],
            instance));
    }

    [Fact]
    public void AUncPathKeepsItsServerAndShare()
    {
        // There is no volume to strip, and the server and share are part of what identifies it.
        const string unc = @"\\build\tools\WSGM.exe";

        Assert.Equal(unc, HidHideOwnedDeltaManager.NormalizePath(unc));
        Assert.False(HidHideOwnedDeltaManager.Contains([unc], DosPath));
    }

    [Fact]
    public void EmptyEntriesMatchNothing()
    {
        Assert.False(HidHideOwnedDeltaManager.Contains([], DosPath));
        Assert.Equal(string.Empty, HidHideOwnedDeltaManager.NormalizePath("   "));
    }
}

internal sealed class DeterministicFakeHidHideAdapter : IHidHideAdapter
{
    private readonly object _gate = new();
    private List<string> _applications;
    private List<string> _devices;

    internal DeterministicFakeHidHideAdapter(
        IEnumerable<string>? applications = null,
        IEnumerable<string>? devices = null,
        bool active = true)
    {
        _applications = applications?.ToList() ?? [];
        _devices = devices?.ToList() ?? [];
        Active = active;
        Health = active ? HidHideHealthState.Ready : HidHideHealthState.Inactive;
    }

    internal HidHideHealthState Health { get; set; }

    internal bool Active { get; set; }

    internal Exception? NextReadFailure { get; set; }

    internal Exception? NextMutationFailure { get; set; }

    internal int? FailMutationAttempt { get; set; }

    internal Action<DeterministicFakeHidHideAdapter>? BeforeNextMutation { get; set; }

    internal int ReadCount { get; private set; }

    internal int MutationCount { get; private set; }

    internal int MutationAttemptCount { get; private set; }

    public Task<HidHideExactSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ReadCount++;
            if (NextReadFailure is { } failure)
            {
                NextReadFailure = null;
                throw failure;
            }

            return Task.FromResult(SnapshotUnderGate());
        }
    }

    public Task<HidHideMutationResult> TryMutateAsync(
        HidHideExactSnapshot expected,
        HidHideEntryMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            MutationAttemptCount++;
            BeforeNextMutation?.Invoke(this);
            BeforeNextMutation = null;
            if (FailMutationAttempt == MutationAttemptCount)
            {
                FailMutationAttempt = null;
                throw new IOException("Injected HidHide mutation failure.");
            }

            if (NextMutationFailure is { } failure)
            {
                NextMutationFailure = null;
                throw failure;
            }

            var current = SnapshotUnderGate();
            if (!current.ExactStateEquals(expected))
            {
                return Task.FromResult(new HidHideMutationResult(
                    false,
                    current,
                    "HidHide changed before the conditional mutation."));
            }

            var entries = mutation.EntryKind is HidHideEntryKind.Application
                ? _applications
                : _devices;
            if (mutation.Mutation is HidHideMutationKind.Add)
            {
                entries.Add(mutation.Value);
            }
            else
            {
                var index = entries.FindIndex(value =>
                    string.Equals(value, mutation.Value, StringComparison.Ordinal));
                if (index < 0)
                {
                    return Task.FromResult(new HidHideMutationResult(
                        false,
                        current,
                        "The exact entry is absent."));
                }

                entries.RemoveAt(index);
            }

            MutationCount++;
            return Task.FromResult(new HidHideMutationResult(
                true,
                SnapshotUnderGate(),
                "Applied."));
        }
    }

    internal void ExternalReplace(
        IEnumerable<string>? applications = null,
        IEnumerable<string>? devices = null,
        bool? active = null)
    {
        lock (_gate)
        {
            if (applications is not null)
            {
                _applications = applications.ToList();
            }

            if (devices is not null)
            {
                _devices = devices.ToList();
            }

            if (active is { } activeValue)
            {
                Active = activeValue;
                Health = activeValue ? HidHideHealthState.Ready : HidHideHealthState.Inactive;
            }
        }
    }

    private HidHideExactSnapshot SnapshotUnderGate() => new(
        Health,
        Active,
        _applications,
        _devices,
        Health.ToString());
}

internal sealed class InMemoryHidHideOwnershipStore : IHidHideOwnershipStore
{
    internal HidHideOwnershipLedger? Ledger { get; private set; }

    internal int SaveCount { get; private set; }

    public Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Ledger);
    }

    public Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = ledger;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = null;
        return Task.CompletedTask;
    }
}
