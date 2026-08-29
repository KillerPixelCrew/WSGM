using WSGM.Device.Sdk.Input;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class HidHideOwnershipTests
{
    [Fact]
    public async Task ControllerManagementOffNeverReadsOrWritesHidHide()
    {
        DeterministicFakeHidHideAdapter adapter = new();
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);

        HidHideActivationResult result = await manager.StartAsync(
            controllerManagementEnabled: false,
            "DeviceHost.exe",
            [Physical("HID\\OWN")],
            1,
            CancellationToken.None);

        Assert.False(result.Activated);
        Assert.Equal(0, adapter.ReadCount);
        Assert.Equal(0, adapter.MutationCount);
        Assert.Null(store.Ledger);
    }

    [Fact]
    public async Task ApplyAndCleanupPreserveEveryExternalEntryAndItsOrdering()
    {
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["HC.exe", "external.exe"],
            devices: ["HID\\PRE-A", "HID\\PRE-B"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);

        HidHideActivationResult activation = await manager.StartAsync(
            controllerManagementEnabled: true,
            "DeviceHost.exe",
            [Physical("HID\\OWN")],
            9,
            CancellationToken.None);
        Assert.True(activation.Activated);

        adapter.ExternalReplace(
            applications: ["external-new.exe", "HC.exe", "external.exe", "DeviceHost.exe"],
            devices: ["HID\\PRE-B", "HID\\NEW", "HID\\PRE-A", "HID\\OWN"]);

        HidHideCleanupResult cleanup = await manager.CleanupAsync(
            CancellationToken.None);
        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);

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
            applications: ["devicehost.EXE"],
            devices: ["hid\\own"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);

        HidHideActivationResult activation = await manager.StartAsync(
            controllerManagementEnabled: true,
            "DeviceHost.exe",
            [Physical("HID\\OWN")],
            4,
            CancellationToken.None);
        HidHideCleanupResult cleanup = await manager.CleanupAsync(
            CancellationToken.None);
        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);

        Assert.True(activation.Activated);
        Assert.True(cleanup.Verified);
        Assert.Equal(["devicehost.EXE"], final.Applications);
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
            controllerManagementEnabled: true,
            "DeviceHost.exe",
            [Physical("HID\\OWN")],
            7,
            CancellationToken.None);
        adapter.ExternalReplace(
            applications: ["DeviceHost.exe", "DeviceHost.exe"],
            devices: ["HID\\OWN"]);

        HidHideCleanupResult cleanup = await manager.CleanupAsync(
            CancellationToken.None);
        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);

        Assert.False(cleanup.Verified);
        Assert.Equal(["DeviceHost.exe", "DeviceHost.exe"], final.Applications);
        Assert.Empty(final.Devices);
        Assert.NotNull(store.Ledger);
        Assert.Contains("Application:DeviceHost.exe", cleanup.Detail);
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

        HidHideActivationResult activation = await manager.StartAsync(
            controllerManagementEnabled: true,
            "DeviceHost.exe",
            [Physical("HID\\OWN")],
            2,
            CancellationToken.None);

        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);
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

        HidHideActivationResult activation = await manager.StartAsync(
            controllerManagementEnabled: true,
            "DeviceHost.exe",
            [Physical("HID\\OWN")],
            2,
            CancellationToken.None);

        Assert.False(activation.Activated);
        Assert.Equal(0, adapter.MutationCount);
        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);
        Assert.False(final.Active);
    }

    [Fact]
    public async Task CrashRecoveryRefusesDifferentTransactionOrTargetGeneration()
    {
        DeterministicFakeHidHideAdapter adapter = new();
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);
        HidHideActivationResult activation = await manager.StartAsync(
            controllerManagementEnabled: true,
            "DeviceHost.exe",
            [Physical("HID\\OWN")],
            14,
            CancellationToken.None);

        HidHideCleanupResult recovery = await manager.ReconcileAsync(
            activation.Ledger!.TransactionId,
            targetGeneration: 15,
            CancellationToken.None);
        HidHideExactSnapshot current = await adapter.ReadAsync(CancellationToken.None);

        Assert.False(recovery.Verified);
        Assert.Contains("DeviceHost.exe", current.Applications);
        Assert.Contains("HID\\OWN", current.Devices);
        Assert.NotNull(store.Ledger);
    }

    private static PhysicalDeviceIdentity Physical(string path) => new()
    {
        InstancePath = path,
        RequiresHiding = true,
    };
}
