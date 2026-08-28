using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using Xunit;

namespace WSGM.Tests;

/// <summary>Shared overlay projection tests over the hardware-free RTSS simulation.</summary>
public sealed class PerformanceOverlayBridgeTests
{
    [Fact]
    public async Task OverlayProjectionObservesAndMutatesTheSinglePerformanceService()
    {
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            new PerformancePolicy(
                new PerformanceValues(60, 0),
                Array.Empty<PerformanceApplicationPolicy>()));
        using PerformanceOverlayBridge bridge = new(service);
        using IDisposable observation = bridge.AcquireObservation();
        await service.RefreshAsync();

        PerformanceOverlaySnapshot before = bridge.Snapshot();
        DescriptorRow overlay = before.Rows.Single(row => row.Id == "overlay-level");
        await bridge.InvokeAsync(overlay, CancellationToken.None);

        PerformanceOverlaySnapshot after = bridge.Snapshot();
        Assert.True(after.Visible);
        Assert.Equal("3", after.Rows.Single(row => row.Id == "overlay-level").TrailingText);
        Assert.Equal(3, service.Current.Desired.OverlayLevel);
        Assert.Equal(1, service.ObserverCount);
    }

    [Fact]
    public async Task DisabledPerformancePolicyHidesTheProjectionWithoutPolling()
    {
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            new PerformancePolicy(
                PerformanceValues.Empty,
                Array.Empty<PerformanceApplicationPolicy>(),
                Enabled: false));
        using PerformanceOverlayBridge bridge = new(service);

        PerformanceOverlaySnapshot snapshot = bridge.Snapshot();

        Assert.False(snapshot.Visible);
        Assert.Equal(0, service.ObserverCount);
    }
}
