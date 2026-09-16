using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Shell;

/// <summary>Shared overlay projection tests over the hardware-free RTSS simulation.</summary>
public sealed class PerformanceOverlayBridgeTests
{
    [Fact]
    public async Task OverlayProjectionObservesAndMutatesTheSinglePerformanceService()
    {
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            // The values the simulated profile already holds, so the poll's drift check has
            // nothing to repair and this stays a test about the projection.
            new PerformancePolicy(
                new PerformanceValues(60, 2),
                []));
        using PerformanceOverlayBridge bridge = new(service);
        using var observation = bridge.AcquireObservation();
        await service.RefreshAsync();

        var before = bridge.Snapshot();
        var overlay = before.Rows.Single(row => row.Id == "overlay-level");
        await bridge.InvokeAsync(overlay, CancellationToken.None);

        var after = bridge.Snapshot();
        Assert.True(after.Visible);
        Assert.Equal("3", after.Rows.Single(row => row.Id == "overlay-level").TrailingText);
        Assert.Equal(3, service.Current.Desired.OverlayLevel);
        Assert.Equal(1, service.ObserverCount);
    }

    [Fact]
    public async Task TheFrameLimitSliderBookendsWhereTheQuickAccessRowDoes()
    {
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            new PerformancePolicy(new PerformanceValues(60, 2), []));
        using PerformanceOverlayBridge bridge = new(service, static () => (30, 120));
        using var observation = bridge.AcquireObservation();
        await service.RefreshAsync();

        var range = bridge.Snapshot().Rows
            .Single(row => row.Id == "frame-limit").Range!.Value;

        // Zero stays reachable because it is how the overlay switches the cap off, and the panel's
        // ceiling replaces RTSS's own — a cap outside these is one the Quick Access row cannot draw.
        Assert.Equal(0, range.Minimum);
        Assert.Equal(120, range.Maximum);
        Assert.Equal(30, range.OffBelow);
    }

    [Fact]
    public async Task WithoutAPanelRangeTheFrameLimitSliderFallsBackToWhatRtssAccepts()
    {
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            new PerformancePolicy(new PerformanceValues(60, 2), []));
        using PerformanceOverlayBridge bridge = new(service);
        using var observation = bridge.AcquireObservation();
        await service.RefreshAsync();

        var range = bridge.Snapshot().Rows
            .Single(row => row.Id == "frame-limit").Range!.Value;

        Assert.Equal(0, range.Minimum);
        Assert.Equal(240, range.Maximum);
        Assert.Equal(0, range.OffBelow);
    }

    [Fact]
    public async Task DisabledPerformancePolicyHidesTheProjectionWithoutPolling()
    {
        // Device Integration and this switch are unrelated: only this one governs the rows.
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            new PerformancePolicy(
                PerformanceValues.Empty,
                [],
                false));
        using PerformanceOverlayBridge bridge = new(service);

        var snapshot = bridge.Snapshot();

        Assert.False(snapshot.Visible);
        Assert.Empty(snapshot.Rows);
        Assert.Equal(0, service.ObserverCount);
    }

    [Fact]
    public async Task PerApplicationRowsLiveOnPowerAndThermalsExceptTheHeadlineToggle()
    {
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            new PerformancePolicy(new PerformanceValues(60, 1), []));
        using PerformanceOverlayBridge bridge = new(service);
        await service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:42", 42, "game.exe"));
        await service.RefreshAsync();

        var before = bridge.Snapshot();

        Assert.Collection(
            before.ProfileRows,
            row => Assert.Equal("detected-application", row.Id),
            row => Assert.Equal("active-profile", row.Id),
            row => Assert.Equal("application-profile", row.Id),
            row => Assert.Equal("reset-profile", row.Id));
        Assert.Equal("Steam 42", before.ProfileRows[0].TrailingText);
        Assert.Equal("Global", before.ProfileRows[1].TrailingText);
        // The per-application detail rows and the shared frame-limit/overlay rows count into Power
        // and thermals, where they render; the enable toggle is the headline on the Device root, so
        // it is not counted into any section.
        DeviceOverlaySnapshot device = new(true, "Ready", string.Empty, null, []);
        var power = Assert.Single(
            DeviceOverlaySectionPages.Build(device, before));
        Assert.Equal(DeviceOverlaySection.PowerAndThermals, power.Section);
        var toggleRows = before.ProfileRows.Count(row =>
            row.Id == DeviceOverlaySectionPages.ApplicationProfileRowId);
        Assert.Equal(1, toggleRows);
        Assert.Equal(before.ProfileRows.Count - toggleRows + before.Rows.Count + 1, power.Count);

        await bridge.InvokeAsync(before.ProfileRows.Single(row =>
            row.Id == "application-profile"));

        var after = bridge.Snapshot();
        Assert.True(service.Current.ApplicationProfileEnabled);
        Assert.Equal(
            "Application",
            after.ProfileRows.Single(row => row.Id == "active-profile").TrailingText);
    }

    // RTSS is WSGM's, not the device platform's.
    // The frame limit and the performance overlay belong to WSGM and RTSS, so they have to keep working
    // on a machine with no plugin installed, with Device Integration switched off, or with a faulted
    // device cycle. These tests pin that by building the whole performance path — service, projection,
    // observation — with no device coordinator, plugin, or capability anywhere in it. If a device
    // dependency is ever introduced into that path, this file stops compiling.
    [Fact]
    public async Task TheOverlayProjectionRendersItsRowsWithNoDevicePlatformPresent()
    {
        await using var service = Service();
        using PerformanceOverlayBridge bridge = new(service);

        var snapshot = bridge.Snapshot();

        Assert.True(snapshot.Visible);
        Assert.Collection(
            snapshot.Rows,
            row => Assert.Equal("frame-limit", row.Id),
            row => Assert.Equal("overlay-level", row.Id));
    }

    [Fact]
    public async Task ObservationIsLeasedByTheOverlayRatherThanByTheDeviceCycle()
    {
        await using var service = Service();
        using PerformanceOverlayBridge bridge = new(service);

        Assert.Equal(0, service.ObserverCount);
        var lease = bridge.AcquireObservation();
        Assert.Equal(1, service.ObserverCount);

        // Polling exists for a rendered control, so it stops when the last UI client leaves — never
        // because a device cycle started, faulted, or ended.
        lease.Dispose();
        Assert.Equal(0, service.ObserverCount);
    }
}
