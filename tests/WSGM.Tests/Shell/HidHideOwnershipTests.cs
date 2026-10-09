using WSGM.Shell;
using WSGM.Testing;
using PhysicalDeviceIdentity = LibHandheld.Contracts.PhysicalDeviceIdentity;

namespace WSGM.Tests.Shell;

public sealed class HidHideOwnershipTests
{
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
    public async Task ACorruptLedgerStillTurnsOffTheCloakAndKeepsItsBytes()
    {
        using TemporaryDirectory temporary = new();
        var ledger = temporary.GetPath("ownership.json");
        byte[] bytes = [0x7b, 0x78, 0x79];
        await File.WriteAllBytesAsync(ledger, bytes);
        FakeHidHideControl control = new();
        HidHideOwnership ownership = new(control, new FileHidHideOwnershipStore(ledger));

        var result = await ownership.ShowAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(control.Active);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ledger));
        Assert.Equal(["cloak:False", "read"], control.Calls);
    }

    [Fact]
    public async Task CloakOffPrecedesReadsEvenWhenHidHideCannotBeRead()
    {
        FakeHidHideControl control = new() { ReadError = 5 };
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());

        var result = await ownership.ShowAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(control.Active);
        Assert.Equal(["cloak:False", "read"], control.Calls);
    }

    [Fact]
    public async Task HideAndShowPreserveEveryExternalEntryAndItsOrdering()
    {
        FakeHidHideControl control = new(["HC.exe", "Other.exe"], ["HID\\PRE1", "HID\\PRE2"]);
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());

        Assert.True((await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None))
            .Succeeded);
        Assert.Equal(["HC.exe", "Other.exe", DosPath], control.Applications);
        Assert.Equal(["HID\\PRE1", "HID\\PRE2", "HID\\OWN"], control.Devices);

        Assert.True((await ownership.ShowAsync(CancellationToken.None)).Succeeded);
        Assert.Equal(["HC.exe", "Other.exe"], control.Applications);
        Assert.Equal(["HID\\PRE1", "HID\\PRE2"], control.Devices);
    }

    [Fact]
    public async Task PreexistingEquivalentEntriesAreNeverClaimedOrRemoved()
    {
        FakeHidHideControl control = new([DevicePath], ["HID\\OWN"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnership ownership = new(control, store);

        Assert.True((await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None))
            .Succeeded);
        Assert.Equal(0, control.ListWrites);
        Assert.Null(store.Ledger);

        await ownership.ShowAsync(CancellationToken.None);
        Assert.Equal([DevicePath], control.Applications);
        Assert.Equal(["HID\\OWN"], control.Devices);
    }

    [Fact]
    public async Task HidingTwiceWritesNothingTheSecondTime()
    {
        // Sleep, wake and fault recovery keep the pad hidden, as HC does. Unhiding for those seconds
        // let Steam open the physical pad, which stays visible after it is hidden again (Xbox Ally X,
        // 2026-09-28).
        FakeHidHideControl control = new();
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());
        await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None);
        var writes = control.ListWrites;

        Assert.True((await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None))
            .Succeeded);
        Assert.Equal(writes, control.ListWrites);
        Assert.Equal(["HID\\OWN"], control.Devices);
    }

    [Fact]
    public async Task AnInactiveCloakIsTurnedOnWhenHidingAndOffWhenShowing()
    {
        // Handheld Companion's uninstaller runs HidHideCLI --cloak-off. A WSGM that only checked the
        // switch then ran without a virtual pad while Steam read the physical one (Xbox Ally X,
        // 2026-09-28). WSGM owns the cloak: on while it hides, off on every exit.
        FakeHidHideControl control = new(active: false);
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());

        await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None);
        Assert.True(control.Active);

        await ownership.ShowAsync(CancellationToken.None);
        Assert.False(control.Active);
    }

    [Fact]
    public async Task ShowingTurnsTheCloakOffEvenWithoutALedger()
    {
        // WSGM closes, the original controller comes back: the cloak goes off on every exit,
        // whether or not this run was the one that turned it on.
        FakeHidHideControl control = new(["HC.exe"], ["HID\\PRE"]);
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());

        Assert.True((await ownership.ShowAsync(CancellationToken.None)).Succeeded);

        Assert.False(control.Active);
        Assert.Equal(["HC.exe"], control.Applications);
        Assert.Equal(["HID\\PRE"], control.Devices);
    }

    [Fact]
    public async Task OwnedEntriesAreRemovedWhenSomethingElseTurnedTheCloakOff()
    {
        FakeHidHideControl control = new();
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());
        await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None);
        control.Active = false;

        Assert.True((await ownership.ShowAsync(CancellationToken.None)).Succeeded);

        Assert.Empty(control.Applications);
        Assert.Empty(control.Devices);
    }

    [Fact]
    public async Task ALedgerLeftByACrashIsShownOnTheNextExit()
    {
        // The ledger exists precisely for "WSGM died holding HidHide entries".
        FakeHidHideControl control = new(devices: ["HID\\PRE"]);
        InMemoryHidHideOwnershipStore store = new();
        await new HidHideOwnership(control, store)
            .HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None);

        HidHideOwnership next = new(control, store);
        Assert.True((await next.ShowAsync(CancellationToken.None)).Succeeded);

        Assert.Empty(control.Applications);
        Assert.Equal(["HID\\PRE"], control.Devices);
        Assert.Null(store.Ledger);
    }

    [Fact]
    public async Task AnEntryIsRecordedBeforeHidHideIsWritten()
    {
        FakeHidHideControl control = new() { WriteError = 5 };
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnership ownership = new(control, store);

        var result = await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(store.Ledger!.Deltas, delta => delta.Value == DosPath);
    }

    [Fact]
    public async Task ARefusedShowKeepsTheLedgerForTheNextExitAndDoesNotRetry()
    {
        FakeHidHideControl control = new();
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnership ownership = new(control, store);
        await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None);
        control.WriteError = 5;
        var writes = control.ListWrites;

        Assert.False((await ownership.ShowAsync(CancellationToken.None)).Succeeded);
        Assert.NotNull(store.Ledger);
        // One attempt each for the application list, the device list and the cloak.
        Assert.Equal(writes + 3, control.ListWrites);

        control.WriteError = 0;
        Assert.True((await ownership.ShowAsync(CancellationToken.None)).Succeeded);
        Assert.Empty(control.Devices);
        Assert.Null(store.Ledger);
    }

    [Fact]
    public async Task InverseModeIsRefused()
    {
        FakeHidHideControl control = new() { Inverse = true };
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());

        var result = await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, control.ListWrites);
    }

    [Fact]
    public async Task WithoutHidHideNothingIsHiddenAndShowingSucceeds()
    {
        FakeHidHideControl control = new() { ReadError = 2 };
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnership ownership = new(control, store);

        Assert.False((await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None))
            .Succeeded);
        Assert.True((await ownership.ShowAsync(CancellationToken.None)).Succeeded);
        Assert.Equal(0, control.ListWrites);
    }

    [Fact]
    public async Task WsgmAllowsItselfBeforeItNeedsToReadDevicesSomethingElseHid()
    {
        // The ordering that mattered on real hardware: another tool had already hidden the pad, so
        // the plugin could not see the device it was being asked to discover.
        FakeHidHideControl control = new(["HC.exe"], ["HID\\HC"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnership ownership = new(control, store);

        await ownership.EnsureReadableAsync(true, DosPath, CancellationToken.None);

        // It grants WSGM sight; it must never hide anything or disturb another owner's entries.
        Assert.Equal(["HC.exe", DosPath], control.Applications);
        Assert.Equal(["HID\\HC"], control.Devices);
        Assert.Contains(store.Ledger!.Deltas, delta => delta.Value == DosPath);
    }

    [Fact]
    public async Task NothingHiddenMeansNothingToAllow()
    {
        // The normal machine. WSGM must not add itself to an allowlist that is guarding nothing.
        FakeHidHideControl control = new();
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());

        await ownership.EnsureReadableAsync(true, DosPath, CancellationToken.None);

        Assert.Equal(0, control.ListWrites);
    }

    [Fact]
    public async Task ManagementOffNeverConsultsHidHideForReadability()
    {
        FakeHidHideControl control = new(["HC.exe"], ["HID\\HC"]);
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());

        await ownership.EnsureReadableAsync(false, DosPath, CancellationToken.None);

        Assert.Equal(0, control.Reads);
    }

    [Fact]
    public void AnEntryStoredAsADevicePathIsRecognisedFromItsDriveLetterForm()
    {
        // The exact case that produced the duplicate.
        Assert.True(HidHideOwnership.Contains([DevicePath], DosPath));
    }

    [Fact]
    public void AndTheOtherWayRound()
    {
        Assert.True(HidHideOwnership.Contains([DosPath], DevicePath));
    }

    [Fact]
    public void AnExactMatchStillMatches()
    {
        Assert.True(HidHideOwnership.Contains([DosPath], DosPath));
        Assert.True(HidHideOwnership.Contains([DevicePath], DevicePath));
    }

    [Fact]
    public void TheVolumeNumberIsNotWhatIdentifiesTheFile()
    {
        // Volume numbering is assigned by Windows and is not stable across machines or boots, so it
        // must not be part of the comparison.
        Assert.True(HidHideOwnership.Contains(
            [@"\Device\HarddiskVolume7\Program Files\WSGM\WSGM.exe"],
            DosPath));
    }

    [Fact]
    public void ADifferentProgramIsNotMatched()
    {
        Assert.False(HidHideOwnership.Contains(
            [@"\Device\HarddiskVolume3\Program Files\Handheld Companion\HandheldCompanion.exe"],
            DosPath));
    }

    [Fact]
    public void ADifferentPathToASameNamedProgramIsNotMatched()
    {
        // Only the volume prefix is ignored. Everything that identifies the file still has to agree.
        Assert.False(HidHideOwnership.Contains([@"C:\Other\WSGM\WSGM.exe"], DosPath));
    }

    [Fact]
    public void DeviceInstancePathsAreLeftAlone()
    {
        // The device list never had this problem: instance paths carry no volume prefix, so they
        // must pass through untouched and keep comparing exactly.
        const string instance = @"HID\VID_0DB0&PID_1902&MI_00&COL01\7&3222ED46&0&0000";

        Assert.Equal(instance, HidHideOwnership.NormalizePath(instance));
        Assert.True(HidHideOwnership.Contains([instance], instance));
        Assert.False(HidHideOwnership.Contains(
            [@"HID\VID_0DB0&PID_1901&IG_00\8&1717EFAA&0&0000"],
            instance));
    }

    [Fact]
    public void AUncPathKeepsItsServerAndShare()
    {
        // There is no volume to strip, and the server and share are part of what identifies it.
        const string unc = @"\\build\tools\WSGM.exe";

        Assert.Equal(unc, HidHideOwnership.NormalizePath(unc));
        Assert.False(HidHideOwnership.Contains([unc], DosPath));
    }

    [Fact]
    public void EmptyEntriesMatchNothing()
    {
        Assert.False(HidHideOwnership.Contains([], DosPath));
        Assert.Equal(string.Empty, HidHideOwnership.NormalizePath("   "));
    }

    [Fact]
    public async Task UninstallShowsTheControllerAgainAndLeavesOtherToolsAlone()
    {
        FakeHidHideControl control = new(["HC.exe"], ["HID\\PRE"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnership ownership = new(control, store);
        Assert.True((await ownership.HideAsync(DosPath, [Physical("HID\\OWN")], CancellationToken.None))
            .Succeeded);

        var result = await ownership.ShowForUninstallAsync([DosPath], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["HC.exe"], control.Applications);
        Assert.Equal(["HID\\PRE"], control.Devices);
        Assert.False(control.Active);
        Assert.Null(store.Ledger);
    }

    [Fact]
    public async Task UninstallRemovesAnAllowanceInEitherNotationWithoutALedgerEntry()
    {
        FakeHidHideControl control = new([DevicePath, "HC.exe"], ["HID\\HC"]);
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());

        var result = await ownership.ShowForUninstallAsync([DosPath], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["HC.exe"], control.Applications);
    }

    [Fact]
    public async Task UninstallWithoutHidHideHasNothingToShowAgain()
    {
        FakeHidHideControl control = new() { ReadError = 2 };
        HidHideOwnership ownership = new(control, new InMemoryHidHideOwnershipStore());

        var result = await ownership.ShowForUninstallAsync([DosPath], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, control.ListWrites);
    }

    private static PhysicalDeviceIdentity Physical(string path)
    {
        return new PhysicalDeviceIdentity
        {
            InstancePath = path,
            RequiresHiding = true
        };
    }
}

/// <summary>HidHide's control device in memory: lists, cloak and inverse mode, with injectable errors.</summary>
internal sealed class FakeHidHideControl(
    IEnumerable<string>? applications = null,
    IEnumerable<string>? devices = null,
    bool active = true) : IHidHideControl
{
    internal List<string> Applications { get; } = [.. applications ?? []];

    internal List<string> Devices { get; } = [.. devices ?? []];

    internal bool Active { get; set; } = active;

    internal bool Inverse { get; init; }

    /// <summary>A Win32 error every read returns; zero reads normally.</summary>
    internal int ReadError { get; init; }

    /// <summary>A Win32 error every write returns; zero writes normally.</summary>
    internal int WriteError { get; set; }

    internal int Reads { get; private set; }

    internal List<string> Calls { get; } = [];

    /// <summary>List and cloak writes attempted, including refused ones.</summary>
    internal int ListWrites { get; private set; }

    public HidHideControlState Read()
    {
        Calls.Add("read");
        Reads++;
        return ReadError != 0
            ? new HidHideControlState(false, ReadError, false, false, [], [])
            : new HidHideControlState(true, 0, Active, Inverse, [.. Applications], [.. Devices]);
    }

    public int Write(HidHideEntryKind entryKind, IReadOnlyList<string> entries)
    {
        ListWrites++;
        if (WriteError != 0)
        {
            return WriteError;
        }

        var list = entryKind is HidHideEntryKind.Application ? Applications : Devices;
        list.Clear();
        list.AddRange(entries);
        return 0;
    }

    public int WriteActive(bool active)
    {
        if (HidHideControlState.IsNotInstalled(ReadError))
        {
            return ReadError;
        }

        Calls.Add($"cloak:{active}");
        ListWrites++;
        if (WriteError != 0)
        {
            return WriteError;
        }

        Active = active;
        return 0;
    }
}

internal sealed class InMemoryHidHideOwnershipStore : IHidHideOwnershipStore
{
    internal HidHideOwnershipLedger? Ledger { get; private set; }

    public Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Ledger);
    }

    public Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = ledger;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = null;
        return Task.CompletedTask;
    }
}
