using System.Diagnostics;
using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;
using WSGM.Shell;
using WSGM.Tests.Input;
using static WSGM.Tests.Builders.ControllerBuilders;

namespace WSGM.Tests.Shell;

public sealed class ControllerManagerTests
{
    private const string HostApplication = @"C:\Program Files\WSGM\WSGM.exe";

    [Fact]
    public async Task AnExpiredDisposalDeadlineStillShowsThePhysicalController()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DisposeAsync(Deadline.Expired).AsTask());

        Assert.False(harness.HidHide.Active);
        Assert.Null(harness.Store.Ledger);
        Assert.Equal(ControllerManagementState.Off, manager.State);
    }

    [Fact]
    public async Task CancelledReleaseStillShowsThePhysicalControllerAndTurnsManagementOff()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await manager.ReleaseAsync(HandoffScope.FullDeactivation, _ => Task.CompletedTask,
            Deadline.Expired, cancelled.Token);

        Assert.False(harness.HidHide.Active);
        Assert.Empty(harness.HidHide.Devices);
        Assert.Null(harness.Store.Ledger);
        Assert.Equal(ControllerManagementState.Off, manager.State);
    }

    [Fact]
    public async Task DisposalStillShowsTheControllerWhenTheBackendThrows()
    {
        Harness harness = new();
        var manager = harness.Manager;
        await StartActiveAsync(manager);
        harness.Backend.DisposeFailure = new InvalidOperationException("backend disposal failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisposeAsync().AsTask());

        Assert.False(harness.HidHide.Active);
        Assert.Null(harness.Store.Ledger);
        Assert.Equal(ControllerManagementState.Off, manager.State);
    }

    [Fact]
    public async Task InterruptedCycleRecoveryShowsOnlyOwnedEntriesOnce()
    {
        Harness harness = new(existingDevices: ["another-owner"]);
        await using var manager = harness.Manager;
        await harness.Store.SaveAsync(new HidHideOwnershipLedger
        {
            Deltas = [new HidHideOwnedDelta { EntryKind = HidHideEntryKind.Device, Value = "crash-pad" }]
        }, CancellationToken.None);
        harness.HidHide.Devices.Add("crash-pad");

        await manager.RecoverPhysicalControllerAsync("integration off", CancellationToken.None);
        var writes = harness.HidHide.ListWrites;
        await manager.RecoverPhysicalControllerAsync("integration off", CancellationToken.None);

        Assert.Equal(writes, harness.HidHide.ListWrites);
        Assert.False(harness.HidHide.Active);
        Assert.Equal(["another-owner"], harness.HidHide.Devices);
        Assert.Null(harness.Store.Ledger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusedSyntheticPulseDoesNotLatchIntoTheNextLiveSample(bool throws)
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);
        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None));
        if (throws)
        {
            harness.Backend.NextPublishFailure = new InvalidOperationException("route refused");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.PulseRearButtonAsync(1, CancellationToken.None));
        }
        else
        {
            harness.Backend.RejectNextPublish = true;
            Assert.False(await manager.PulseRearButtonAsync(1, CancellationToken.None));
        }

        await StartActiveAsync(manager);
        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.B), CancellationToken.None));

        Assert.Equal(CanonicalButtons.B, harness.Backend.LastPublished?.Buttons);
    }

    [Fact]
    public async Task DisabledSelectionStartsOffAndTouchesNoHidHideOrBackendState()
    {
        Harness harness = new();
        await using var manager = harness.Manager;

        var status = await manager.StartAsync(
            Disabled("Controller management is off."),
            [Device()],
            null,
            null,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Off, status.State);
        Assert.Equal("Controller management is off.", status.Detail);
        Assert.False(manager.UiPad.IsActive);
        Assert.Null(status.Target);
        Assert.Empty(harness.Backend.Operations);
        Assert.Equal(0, harness.HidHide.Reads);
        Assert.Null(harness.Store.Ledger);
        Assert.Empty(harness.PriorityWrites);
    }

    [Fact]
    public async Task EnabledSelectionHidesTheDeviceAndCreatesTheSelectedTarget()
    {
        Harness harness = new();
        await using var manager = harness.Manager;

        var status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.Xbox360),
            [Device()],
            null,
            null,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Active, status.State);
        Assert.Equal(ManagedControllerTarget.Xbox360, status.Target);
        Assert.Equal(ProfileSource.Global, status.TargetSource);
        Assert.True(manager.UiPad.IsActive);
        Assert.Contains("create:1:neutral", harness.Backend.Operations);
        Assert.Equal([ProcessPriorityClass.High], harness.PriorityWrites);
        Assert.Contains(HostApplication, harness.HidHide.Applications);
        Assert.Contains(Device().InstancePath, harness.HidHide.Devices);
    }

    [Fact]
    public async Task AnApplicationOverrideChoosesTheTargetAtStart()
    {
        Harness harness = new();
        await using var manager = harness.Manager;

        var status = await manager.StartAsync(
            Enabled(
                ManagedControllerTarget.SteamDeckComposite,
                Override("steam:70", ManagedControllerTarget.DualShock4)),
            [Device()],
            "steam:70",
            null,
            CancellationToken.None);

        Assert.Equal(ManagedControllerTarget.DualShock4, status.Target);
        Assert.Equal(ProfileSource.Game, status.TargetSource);
        Assert.Equal("steam:70", status.ApplicationId);
    }

    [Fact]
    public async Task AnUnavailableBackendCreatesNoTargetAndLeavesThePadVisible()
    {
        const string unavailableDetail = "The controller backend is not usable on this system.";
        Harness harness = new()
        {
            Backend =
            {
                Health = new ControllerBackendHealth(false, unavailableDetail, [])
            }
        };
        await using var manager = harness.Manager;

        var status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.Xbox360),
            [Device()],
            null,
            null,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Unavailable, status.State);
        Assert.Equal(unavailableDetail, status.Detail);
        Assert.False(manager.UiPad.IsActive);
        Assert.Empty(harness.HidHide.Devices);
        Assert.False(harness.HidHide.Active);
        Assert.DoesNotContain(harness.Backend.Operations, operation => operation.StartsWith("create"));
        Assert.Empty(harness.PriorityWrites);
    }

    [Fact]
    public async Task ARefusedHideCreatesNoTargetAndTakesBackWhatWentThrough()
    {
        Harness harness = new() { HidHide = { WriteError = 5 } };
        await using var manager = harness.Manager;

        var status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.Xbox360),
            [Device()],
            null,
            null,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Unavailable, status.State);
        Assert.DoesNotContain(harness.Backend.Operations, operation => operation.StartsWith("create"));
    }

    [Fact]
    public async Task ABackendWithoutTheSelectedTargetReportsThatExactReason()
    {
        Harness harness = new(ManagedControllerTarget.Xbox360);
        await using var manager = harness.Manager;

        var status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.DualShock4),
            [Device()],
            null,
            null,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Unavailable, status.State);
        Assert.Contains("DualShock4", status.Detail);
        Assert.Empty(harness.HidHide.Devices);
    }

    [Fact]
    public async Task ARunningApplicationOverrideReplacesTheTargetExactlyOnce()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await manager.StartAsync(
            Enabled(
                ManagedControllerTarget.SteamDeckComposite,
                Override("steam:70", ManagedControllerTarget.DualShock4)),
            [Device()],
            null,
            null,
            CancellationToken.None);

        var status = await manager.ApplyRunningApplicationAsync(
            Running(applicationId: "steam:70"),
            CancellationToken.None);

        Assert.Equal(ManagedControllerTarget.DualShock4, status.Target);
        Assert.Equal(ProfileSource.Game, status.TargetSource);
        // The old target is removed before the replacement is created, so the two are never
        // enumerated at the same time.
        List<string> operations = [.. harness.Backend.Operations];
        Assert.InRange(
            operations.IndexOf("remove:1"),
            0,
            operations.IndexOf("create:2:neutral") - 1);
        Assert.Equal([ProcessPriorityClass.High], harness.PriorityWrites);
    }

    [Fact]
    public async Task AnApplicationWithNoOverrideKeepsTheGlobalTargetWithoutReplacement()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await manager.StartAsync(
            Enabled(
                ManagedControllerTarget.SteamDeckComposite,
                Override("steam:70", ManagedControllerTarget.DualShock4)),
            [Device()],
            null,
            null,
            CancellationToken.None);

        var status = await manager.ApplyRunningApplicationAsync(
            Running(applicationId: "steam:220"),
            CancellationToken.None);

        Assert.Equal(ManagedControllerTarget.SteamDeckComposite, status.Target);
        Assert.DoesNotContain("remove:1", harness.Backend.Operations);
    }

    [Fact]
    public async Task ADisabledSelectionIsNotReconciledIntoAnUnorderedTargetRemoval()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        var status = await manager.ApplySelectionAsync(
            Disabled("Controller management is off."),
            null,
            null,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Active, status.State);
        Assert.DoesNotContain("remove:1", harness.Backend.Operations);
    }

    [Fact]
    public async Task StartingAgainKeepsATargetOfTheSameKind()
    {
        // As HC keeps its virtual controller across sleep and reconnects: recreating it made Steam see
        // the pad unplug and re-attach on every wake.
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);
        var hideWrites = harness.HidHide.ListWrites;

        await StartActiveAsync(manager);

        Assert.Single(harness.Backend.Operations, operation => operation.StartsWith("create:"));
        Assert.DoesNotContain(harness.Backend.Operations, operation => operation.StartsWith("remove:"));
        Assert.Equal(hideWrites, harness.HidHide.ListWrites);
        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None));
    }

    [Fact]
    public async Task SamplesReachTheVirtualTargetWhileNoSurfaceHoldsCapture()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None));
        Assert.Contains("publish:1:live", harness.Backend.Operations);
    }

    [Fact]
    public async Task EverySampleReachesTheUiPadWhateverTheCapture()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None);
        Assert.Equal(GamepadButtons.A, manager.UiPad.Buttons);

        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        await manager.RouteAsync(Sample(CanonicalButtons.QuickAccess), CancellationToken.None);
        Assert.Equal(GamepadButtons.QuickAccess, manager.UiPad.Buttons);
    }

    [Fact]
    public async Task CapturedSamplesLeaveTheTargetNeutral()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None);
        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        var routed = await manager.RouteAsync(Sample(CanonicalButtons.Y), CancellationToken.None);

        Assert.False(routed);
        List<string> operations = [.. harness.Backend.Operations];
        Assert.Equal("publish:1:neutral", operations[^1]);
    }

    [Fact]
    public async Task ForwardingResumesOnlyAfterEveryHeldControlIsReleased()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.RouteAsync(Sample(CanonicalButtons.Guide), CancellationToken.None);
        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        manager.ReleaseUi("overlay");

        Assert.False(await manager.RouteAsync(Sample(CanonicalButtons.Guide), CancellationToken.None));
        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.None), CancellationToken.None));
    }

    [Fact]
    public async Task AControlPressedInsideTheSurfaceCannotLeakIntoTheGameWhenItCloses()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        Assert.False(await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None));
        manager.ReleaseUi("overlay");

        Assert.False(await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None));
        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.None), CancellationToken.None));
    }

    [Fact]
    public async Task NestedSurfacesKeepCaptureUntilTheLastOneCloses()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        await manager.ClaimUiAsync("taskbar", CancellationToken.None);
        manager.ReleaseUi("taskbar");

        Assert.False(await manager.RouteAsync(Sample(CanonicalButtons.None), CancellationToken.None));
        manager.ReleaseUi("overlay");
        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.None), CancellationToken.None));
    }

    [Fact]
    public async Task ASleepKeepsTheTargetAndTheHiddenPadAndOnlyStopsForwarding()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);
        await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None);

        await manager.BlockForwardingAsync("system sleep", CancellationToken.None);

        Assert.False(await manager.RouteAsync(Sample(CanonicalButtons.None), CancellationToken.None));
        Assert.Single(harness.Backend.Operations, operation => operation == "publish:1:neutral");
        Assert.DoesNotContain(harness.Backend.Operations, operation => operation.StartsWith("remove:"));
        Assert.Contains(Device().InstancePath, harness.HidHide.Devices);
        Assert.Equal(ControllerManagementState.Active, manager.State);
    }

    [Fact]
    public async Task TheWakeReopensForwardingWithoutThePluginRepublishing()
    {
        // The wake failure this replaced: the Deck target stayed in Steam but received nothing,
        // because only a republished pad reopened forwarding.
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);
        await manager.BlockForwardingAsync("system sleep", CancellationToken.None);

        await manager.ResumeForwardingAsync("system wake", CancellationToken.None);

        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None));
        Assert.Single(harness.Backend.Operations, operation => operation.StartsWith("create:"));
    }

    [Fact]
    public async Task ARepublishedPadAfterTheWakeAlsoReopensForwarding()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);
        await manager.BlockForwardingAsync("system sleep", CancellationToken.None);

        await StartActiveAsync(manager);

        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseRemovesTheTargetAfterThePluginAndThenShowsThePad()
    {
        Harness harness = new(
            existingApplications: [@"C:\External\Manager.exe"],
            existingDevices: ["HID\\EXTERNAL"]);
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);
        var targetRemovedBeforePlugin = true;
        IReadOnlyList<string> hiddenAtPluginRelease = [];

        await manager.ReleaseAsync(
            HandoffScope.ControllerOnly,
            _ =>
            {
                targetRemovedBeforePlugin = harness.Backend.Operations.Contains("remove:1");
                hiddenAtPluginRelease = [.. harness.HidHide.Devices];
                return Task.CompletedTask;
            },
            Deadline.Never,
            CancellationToken.None);

        // While the plugin is still letting go, the physical device stays hidden and WSGM's target
        // still exists. Showing it earlier would expose a device the plugin is still holding.
        Assert.False(targetRemovedBeforePlugin);
        Assert.Contains(Device().InstancePath, hiddenAtPluginRelease);
        Assert.Contains("remove:1", harness.Backend.Operations);
        Assert.Equal([@"C:\External\Manager.exe"], harness.HidHide.Applications);
        Assert.Equal(["HID\\EXTERNAL"], harness.HidHide.Devices);
        Assert.False(harness.HidHide.Active);
        Assert.Null(harness.Store.Ledger);
        Assert.Equal(ControllerManagementState.Idle, manager.State);
        Assert.False(manager.UiPad.IsActive);
    }

    [Fact]
    public async Task AFailedPluginReleaseStillRemovesWsgmState()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.ReleaseAsync(
            HandoffScope.FullDeactivation,
            _ => Task.FromException(new TimeoutException("The plugin never answered.")),
            Deadline.Never,
            CancellationToken.None);

        Assert.Contains("remove:1", harness.Backend.Operations);
        Assert.Null(harness.Store.Ledger);
        Assert.Equal(ControllerManagementState.Off, manager.State);
    }

    [Fact]
    public async Task AReleaseThatKeepsThePadHiddenLeavesHidHideAlone()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.ReleaseAsync(HandoffScope.ControllerOnly, _ => Task.CompletedTask, Deadline.Never,
            CancellationToken.None,
            true);

        Assert.Contains(Device().InstancePath, harness.HidHide.Devices);
        Assert.NotNull(harness.Store.Ledger);

        await manager.ShowPhysicalControllerAsync("restart gave up", CancellationToken.None);
        Assert.Empty(harness.HidHide.Devices);
    }

    [Fact]
    public async Task SamplesAreRefusedAfterReleaseUntilManagementStartsAgain()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);
        await manager.ReleaseAsync(HandoffScope.ControllerOnly, _ => Task.CompletedTask, Deadline.Never,
            CancellationToken.None);

        Assert.False(await manager.RouteAsync(Sample(CanonicalButtons.A), CancellationToken.None));
        Assert.Equal([ProcessPriorityClass.High, ProcessPriorityClass.Normal], harness.PriorityWrites);

        await StartActiveAsync(manager);
        Assert.True(await manager.RouteAsync(Sample(CanonicalButtons.None), CancellationToken.None));
        Assert.Equal(
            [ProcessPriorityClass.High, ProcessPriorityClass.Normal, ProcessPriorityClass.High],
            harness.PriorityWrites);
    }

    [Fact]
    public async Task DisposeRemovesTheTargetAndThenWsgmOwnedHidHideEntries()
    {
        Harness harness = new();
        var manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.DisposeAsync();

        Assert.Contains("remove:1", harness.Backend.Operations);
        Assert.Empty(harness.HidHide.Applications);
        Assert.Empty(harness.HidHide.Devices);
        Assert.False(harness.HidHide.Active);
        Assert.Null(harness.Store.Ledger);
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        Harness harness = new();
        var manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.Single(harness.Backend.Operations, operation => operation == "remove:1");
        Assert.Equal([ProcessPriorityClass.High, ProcessPriorityClass.Normal], harness.PriorityWrites);
    }

    [Fact]
    public async Task TargetLossRestoresPriorityAndHandsTheUiBackToSdl()
    {
        Harness harness = new();
        await using var manager = harness.Manager;
        await StartActiveAsync(manager);

        harness.Backend.LoseTarget();

        Assert.Equal(ControllerManagementState.Faulted, manager.State);
        Assert.False(manager.UiPad.IsActive);
        Assert.Equal([ProcessPriorityClass.High, ProcessPriorityClass.Normal], harness.PriorityWrites);
    }

    private static async Task StartActiveAsync(ControllerManager manager)
    {
        var status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.Xbox360),
            [Device()],
            null,
            null,
            CancellationToken.None);
        Assert.Equal(ControllerManagementState.Active, status.State);
    }

    private static ControllerSelection Enabled(
        ManagedControllerTarget target,
        params GameProfile[] overrides)
    {
        return new ControllerSelection(true,
            new ProfileConfig { Global = new ProfileValues { ControllerTarget = target }, Games = [.. overrides] },
            "Controller management is off.");
    }

    private static ControllerSelection Disabled(string detail)
    {
        return new ControllerSelection(false, new ProfileConfig(), detail);
    }

    private static PhysicalDeviceIdentity Device()
    {
        return new PhysicalDeviceIdentity
        {
            InstancePath = @"HID\VID_0DB0&PID_1901\7&CLAW",
            RequiresHiding = true
        };
    }

    private static CanonicalControllerSample Sample(CanonicalButtons buttons)
    {
        return new CanonicalControllerSample
        {
            Timestamp = DateTimeOffset.UtcNow,
            Buttons = buttons
        };
    }

    private sealed class Harness
    {
        internal Harness(
            ManagedControllerTarget? onlyTarget = null,
            IEnumerable<string>? existingApplications = null,
            IEnumerable<string>? existingDevices = null)
        {
            Backend = onlyTarget is { } kind
                ? new DeterministicFakeControllerBackend(kind)
                : new DeterministicFakeControllerBackend();
            HidHide = new FakeHidHideControl(existingApplications, existingDevices);
            Store = new InMemoryHidHideOwnershipStore();
            Manager = new ControllerManager(
                Backend,
                new DeterministicFakeHapticSink(),
                new HidHideOwnership(HidHide, Store),
                HostApplication,
                new ControllerProcessPriority(
                    () => Priority,
                    priority =>
                    {
                        Priority = priority;
                        PriorityWrites.Add(priority);
                    },
                    _ => { },
                    _ => { }));
        }

        private ProcessPriorityClass Priority { get; set; } = ProcessPriorityClass.Normal;

        internal List<ProcessPriorityClass> PriorityWrites { get; } = [];

        internal DeterministicFakeControllerBackend Backend { get; }

        internal FakeHidHideControl HidHide { get; }

        internal InMemoryHidHideOwnershipStore Store { get; }

        internal ControllerManager Manager { get; }
    }
}
