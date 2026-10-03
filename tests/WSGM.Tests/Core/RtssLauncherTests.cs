using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     When WSGM may start RTSS for itself, and when it must not.
/// </summary>
/// <remarks>
///     WSGM needs RTSS for the frame limit, the performance overlay and AutoTDP's frametimes, and on a
///     service boot WSGM runs before RTSS's own tray entry does — so a machine with RTSS installed and
///     working still came up with performance controls unavailable purely because of start order.
///     <para>
///         Every test here injects the start callback, so no test ever launches a process.
///     </para>
/// </remarks>
public sealed class RtssLauncherTests
{
    [Fact]
    public void NotRunningIsTheOneStateWorthStarting()
    {
        // Discovery has already accepted the installation and found no process. That is the only
        // unavailable state a launch actually fixes.
        Assert.True(RtssLauncher.ShouldStart(Probe(RtssAvailability.NotRunning), true));
    }

    [Fact]
    public void NoOtherStateStartsAnything()
    {
        // Starting a program because WSGM could not identify it would be exactly the wrong response
        // to "incompatible", and there is nothing to start when it is already ready.
        // A loop rather than [Theory] because the availability enum is internal.
        RtssAvailability[] others =
        [
            RtssAvailability.NotInstalled,
            RtssAvailability.Incompatible,
            RtssAvailability.Degraded,
            RtssAvailability.Unknown,
            RtssAvailability.AdapterUnavailable,
            RtssAvailability.Ready
        ];
        foreach (var availability in others)
        {
            Assert.False(RtssLauncher.ShouldStart(Probe(availability), true));
        }
    }

    [Fact]
    public void PerformanceControlSwitchedOffStartsNothing()
    {
        // A user who turned the feature off has not asked WSGM to launch a background program.
        Assert.False(RtssLauncher.ShouldStart(Probe(RtssAvailability.NotRunning), false));
    }

    [Fact]
    public void AProbeWithNoVerifiedExecutableStartsNothing()
    {
        // The path comes from discovery, which only accepts a signed RTSS under a protected install
        // root. Without one there is nothing WSGM is willing to launch.
        var probe = Probe(RtssAvailability.NotRunning) with { ExecutablePath = null };

        Assert.False(RtssLauncher.ShouldStart(probe, true));
    }

    [Fact]
    public async Task ItStartsTheExactExecutableDiscoveryVerified()
    {
        List<string> started = [];
        using CancellationTokenSource cancellation = new();
        using RtssLauncher launcher = new(path =>
        {
            started.Add(path);
            cancellation.Cancel();
            return Task.FromResult(true);
        });
        Assert.True(await launcher.TryStartAsync(Probe(RtssAvailability.NotRunning), true, cancellation.Token));
        Assert.Equal([@"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe"], started);
    }

    [Fact]
    public async Task AStartInFlightBlocksAnotherStartAndCancellationReleasesIt()
    {
        var starts = 0;
        TaskCompletionSource<bool> starting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        using RtssLauncher launcher = new(_ =>
        {
            starts++;
            return starting.Task;
        });
        var first = launcher.TryStartAsync(Probe(RtssAvailability.NotRunning), true, cancellation.Token);
        Assert.False(await launcher.TryStartAsync(Probe(RtssAvailability.NotRunning), true));
        cancellation.Cancel();
        starting.SetResult(true);
        Assert.True(await first);
        using CancellationTokenSource secondCancellation = new();
        secondCancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
        Assert.True(await launcher.TryStartAsync(Probe(RtssAvailability.NotRunning), true, secondCancellation.Token));
        Assert.Equal(2, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStartsDoNotBlockTheNextAttempt(bool throws)
    {
        var starts = 0;
        using RtssLauncher launcher = new(_ =>
        {
            starts++;
            return throws ? throw new InvalidOperationException("access denied") : Task.FromResult(false);
        });
        Assert.False(await launcher.TryStartAsync(Probe(RtssAvailability.NotRunning), true));
        Assert.False(await launcher.TryStartAsync(Probe(RtssAvailability.NotRunning), true));
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task CancelledOrDisposedLauncherStartsNothing()
    {
        var starts = 0;
        RtssLauncher launcher = new(_ =>
        {
            starts++;
            return Task.FromResult(false);
        });
        Assert.False(
            await launcher.TryStartAsync(Probe(RtssAvailability.NotRunning), true, new CancellationToken(true)));
        launcher.Dispose();
        Assert.False(await launcher.TryStartAsync(Probe(RtssAvailability.NotRunning), true));
        Assert.Equal(0, starts);
    }

    [Fact]
    public void WatchReplacementIgnoresOldCallbacksAndDisposalClosesAdmission()
    {
        Dictionary<int, Action> callbacks = [];
        Dictionary<int, WatchLease> leases = [];
        var exits = 0;
        var watches = 0;
        using RtssLauncher launcher = new(watchProcess: (pid, callback) =>
        {
            watches++;
            callbacks[pid] = callback;
            return leases[pid] = new WatchLease();
        });
        var ready = Probe(RtssAvailability.Ready) with { ProcessId = 10 };
        launcher.Watch(ready, () => exits++);
        launcher.Watch(ready, () => exits++);
        launcher.Watch(ready with { ProcessId = 11 }, () => exits++);
        Assert.True(leases[10].Disposed);
        callbacks[10]();
        Assert.Equal(0, exits);
        callbacks[11]();
        callbacks[11]();
        Assert.Equal(1, exits);
        Assert.True(leases[11].Disposed);
        launcher.Watch(ready with { ProcessId = 12 }, () => exits++);
        launcher.Dispose();
        callbacks[12]();
        launcher.Watch(ready, () => exits++);
        Assert.Equal(1, exits);
        Assert.True(leases[12].Disposed);
        Assert.Equal(3, watches);
    }

    [Fact]
    public void ExitDuringSubscriptionDisposesTheReturnedWatch()
    {
        WatchLease lease = new();
        var exits = 0;
        using RtssLauncher launcher = new(watchProcess: (_, callback) =>
        {
            callback();
            return lease;
        });
        launcher.Watch(Probe(RtssAvailability.Ready) with { ProcessId = 10 }, () => exits++);
        Assert.Equal(1, exits);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void FailedWatchIsAttemptedOncePerProcessAndSimulationIsIgnored()
    {
        var watches = 0;
        using RtssLauncher launcher = new(watchProcess: (_, _) =>
        {
            watches++;
            throw new InvalidOperationException("process exited");
        });
        var ready = Probe(RtssAvailability.Ready) with { ProcessId = 10 };
        launcher.Watch(ready, () => { });
        launcher.Watch(ready, () => { });
        launcher.Watch(ready with { ProcessId = 11 }, () => { });
        launcher.Watch(ready with { ProcessId = null }, () => { });
        Assert.Equal(2, watches);
    }

    private static RtssProbe Probe(RtssAvailability availability)
    {
        return new RtssProbe(availability, "7.3.7",
            @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe", 0, null, "test");
    }

    private sealed class WatchLease : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
