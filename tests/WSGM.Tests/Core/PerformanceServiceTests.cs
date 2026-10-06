using WSGM.Core;
using WSGM.Shell;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Core;

public sealed class PerformanceServiceTests
{
    [Fact]
    public async Task PowerStatusIsForwardedUntilTheServiceIsDisposed()
    {
        await using var adapter = new FakeRtssAdapter();
        var service = CreateService(adapter);
        var first = new RtssOsdPowerStatus(18, false, false, null, string.Empty);
        var second = new RtssOsdPowerStatus(17, true, true, 17, "Holding");

        service.ApplyOsdPowerStatus(first);
        service.ApplyOsdPowerStatus(second);

        Assert.Equal([first, second], adapter.PowerStatuses);

        await service.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => service.ApplyOsdPowerStatus(first));
    }

    [Fact]
    public void NonzeroOverlayOpensBothCurrentAndGlobalRtssPresentationGates()
    {
        Assert.Equal(
            ["game.exe", string.Empty],
            RtssNativeAdapter.OverlayActivationProfiles(3, "game.exe"));
    }

    [Fact]
    public void GlobalOverlayDoesNotWriteTheSameRtssProfileTwice()
    {
        Assert.Equal(
            [string.Empty],
            RtssNativeAdapter.OverlayActivationProfiles(1, string.Empty));
    }

    [Fact]
    public void OverlayOffDoesNotCloseAnyRtssPresentationGate()
    {
        Assert.Empty(RtssNativeAdapter.OverlayActivationProfiles(0, "game.exe"));
    }

    [Fact]
    public async Task AWriteIsPublishedAsObservedWithoutAReadback()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);

        await service.RefreshAsync();
        var readsBefore = adapter.ReadCount;
        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            "overlay",
            "command-1");

        Assert.Equal(PerformanceCommandPhase.Applied, command.Phase);
        Assert.Equal(60, service.Current.Desired.FrameLimit);
        Assert.Equal(60, service.Current.Observed.FrameLimit);
        Assert.Single(adapter.Applies);
        Assert.Equal(readsBefore, adapter.ReadCount);
    }

    [Fact]
    public async Task AdapterBoundsRejectBeforeMutation()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);

        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            999,
            "qam",
            "command-2");

        Assert.Equal(PerformanceCommandPhase.Rejected, command.Phase);
        Assert.Empty(adapter.Applies);
    }

    [Fact]
    public async Task PersistenceFailureRollsBackDesiredStateBeforeRtssMutation()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = new PerformanceService(
            adapter,
            InertLauncher(),
            static (_, _, _) => Task.FromException<ProfileSnapshot>(new IOException("disk unavailable")));

        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            "overlay",
            "persistence-failure");

        Assert.Equal(PerformanceCommandPhase.Failed, command.Phase);
        Assert.Null(service.Current.Desired.FrameLimit);
        Assert.Empty(adapter.Applies);
        Assert.Equal(RtssAvailability.Ready, service.Current.Probe.Availability);
    }

    [Fact]
    public async Task MissingRtssIsAnIsolatedRejectedFeature()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Probe = FakeRtssAdapter.ReadyProbe with
        {
            Availability = RtssAvailability.NotInstalled,
            Capabilities = null,
            Diagnostic = "RTSS is absent."
        };
        await using var service = CreateService(adapter);

        var command = await service.SetAsync(
            PerformanceControl.OverlayLevel,
            2,
            "qam",
            "command-3");

        Assert.Equal(PerformanceCommandPhase.Rejected, command.Phase);
        Assert.Equal(RtssAvailability.NotInstalled, service.Current.Probe.Availability);
    }

    [Fact]
    public async Task AdapterTimeoutIsReportedAsAFailureWithoutEscaping()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.OnApply = static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new RtssApplyResult(true, null);
        };
        await using var service = Service(adapter, Profiles(), commandTimeout: TimeSpan.FromMilliseconds(100));

        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            45,
            "qam",
            "command-6");

        Assert.Equal(PerformanceCommandPhase.Failed, command.Phase);
    }

    [Fact]
    public async Task ExternalEditIsPublishedAsExternalChange()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Values[string.Empty] = new PerformanceValues(60, 1);
        await using var service = CreateService(adapter);
        await service.RefreshAsync();

        adapter.Values[string.Empty] = new PerformanceValues(45, 1);
        await service.RefreshAsync();

        Assert.Equal(45, service.Current.Observed.FrameLimit);
        Assert.Equal(PerformanceCommandPhase.ExternalChange, service.Current.Command.Phase);
    }

    [Fact]
    public async Task DriftFromTheDesiredValuesIsWrittenAgain()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Values[string.Empty] = new PerformanceValues(60, 1);
        await using var service = CreateService(adapter, Config(60, 1));
        await service.RefreshAsync();
        Assert.Empty(adapter.Applies);

        adapter.Values[string.Empty] = new PerformanceValues(12, 1);
        await service.RefreshAsync();

        // Both controls are rewritten, because the repair goes through the same effective-desired
        // path every application transition uses rather than a second partial one.
        Assert.Equal(2, adapter.Applies.Count);
        Assert.Equal(60, adapter.Values[string.Empty].FrameLimit);

        await service.RefreshAsync();

        Assert.Equal(2, adapter.Applies.Count);
    }

    [Fact]
    public async Task AProfileTakenBackIsWrittenAgainOnEveryPoll()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Values[string.Empty] = new PerformanceValues(60, 1);
        await using var service = CreateService(adapter, Config(60, 1));
        await service.RefreshAsync();

        // A writer that takes the profile back the moment WSGM lets go of it.
        adapter.OnApply = (request, _) =>
        {
            adapter.Write(request);
            adapter.Values[request.RtssProfileName] = new PerformanceValues(12, 1);
            return Task.FromResult(new RtssApplyResult(true, null));
        };
        adapter.Values[string.Empty] = new PerformanceValues(12, 1);
        await service.RefreshAsync();
        var afterOneRepair = adapter.Applies.Count;

        await service.RefreshAsync();
        await service.RefreshAsync();

        // HandheldCompanion's watchdog keeps writing the value back while RTSS disagrees.
        Assert.Equal(2, afterOneRepair);
        Assert.Equal(3 * afterOneRepair, adapter.Applies.Count);
    }

    [Fact]
    public async Task ReloadedPolicyReconcilesThroughTheSameAdapterPath()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);
        var profiles = Profiles(Config(55, 2));

        await service.ApplyProfilesAsync(new ProfileSnapshot(profiles.Current.Config, profiles.Current.Active, 2),
            true);

        Assert.Equal(new PerformanceValues(55, 2), service.Current.Desired);
        Assert.Equal(new PerformanceValues(55, 2), service.Current.Observed);
        Assert.Equal(2, adapter.Applies.Count);
    }

    [Fact]
    public async Task PollingStartsImmediatelyWithoutAnObserver()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = Service(adapter, Profiles(), TimeSpan.FromMilliseconds(250));
        await adapter.FirstProbe.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.InRange(service.PollInterval, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupLaunchAndReadyDriftRepairNeedNoPerformanceUi(bool unknownExecutable)
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Probe = FakeRtssAdapter.ReadyProbe with { Availability = RtssAvailability.NotRunning };
        adapter.Values[string.Empty] = new PerformanceValues(0, 0);
        var profiles = Profiles(Config(60, 2));
        if (unknownExecutable)
        {
            profiles.SetRunningApplication(new PerformanceApplicationTarget("steam:42", 42, null));
        }

        var starts = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RtssLauncher launcher = new(_ =>
        {
            Interlocked.Increment(ref starts);
            adapter.Probe = FakeRtssAdapter.ReadyProbe;
            started.TrySetResult();
            return Task.FromResult(false);
        });
        await using var service = LaunchService(adapter, profiles, launcher);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.RefreshAsync();
        // The first refresh owns the launch; a fresh probe confirms the new process state.
        await service.RefreshAsync();
        Assert.Equal(1, starts);
        Assert.Contains(adapter.Applies, request => request is
            { Control: PerformanceControl.OverlayLevel, Value: 2, RtssProfileName: "" });
        if (unknownExecutable)
        {
            Assert.DoesNotContain(adapter.Applies, request => request.Control == PerformanceControl.FrameLimit);
            Assert.Null(service.Current.Observed.FrameLimit);
        }
        else
        {
            Assert.Contains(adapter.Applies,
                request => request is { Control: PerformanceControl.FrameLimit, Value: 60 });
        }

        var count = adapter.Applies.Count;
        await service.RefreshAsync();
        await service.RefreshAsync();
        Assert.Equal(count, adapter.Applies.Count);
    }

    [Fact]
    public async Task DisabledServiceProbesAndEnableStartsImmediatelyWithoutWaitingForThePoll()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Probe = FakeRtssAdapter.ReadyProbe with { Availability = RtssAvailability.NotRunning };
        var profiles = Profiles(Config(60, 2));
        var starts = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RtssLauncher launcher = new(_ =>
        {
            Interlocked.Increment(ref starts);
            started.TrySetResult();
            return Task.FromResult(false);
        });
        await using var service = LaunchService(adapter, profiles, launcher, false);
        await service.RefreshAsync();
        Assert.Equal(0, starts);
        Assert.Empty(adapter.Applies);
        Assert.True(adapter.ProbeCount > 0);
        await service.ApplyProfilesAsync(profiles.Current, true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task ExitTriggersOneOwnedRefreshAndDisposeDetachesTheWatch()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Probe = FakeRtssAdapter.ReadyProbe with { ProcessId = 10 };
        var profiles = Profiles();
        Action? exit = null;
        WatchLease lease = new();
        TaskCompletionSource starting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        RtssLauncher launcher = new(_ =>
        {
            Interlocked.Increment(ref starts);
            starting.TrySetResult();
            return release.Task;
        }, (_, callback) =>
        {
            exit = callback;
            return lease;
        });
        var service = LaunchService(adapter, profiles, launcher);
        await service.RefreshAsync();
        Assert.NotNull(exit);
        adapter.Probe = adapter.Probe with { Availability = RtssAvailability.NotRunning };
        exit();
        await starting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var concurrentRefresh = service.RefreshAsync();
        exit();
        Assert.Equal(1, starts);
        adapter.Probe = FakeRtssAdapter.ReadyProbe;
        release.SetResult(false);
        await concurrentRefresh;
        await service.DisposeAsync();
        exit();
        Assert.True(lease.Disposed);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task EnableDuringDisabledInFlightRefreshSchedulesAnotherProbe()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Probe = FakeRtssAdapter.ReadyProbe with { ProcessId = 10 };
        var profiles = Profiles();
        TaskCompletionSource watching = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        RtssLauncher launcher = new(_ =>
        {
            Interlocked.Increment(ref starts);
            adapter.Probe = FakeRtssAdapter.ReadyProbe;
            started.TrySetResult();
            return Task.FromResult(false);
        }, (_, _) =>
        {
            watching.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return new WatchLease();
        });
        await using var service = LaunchService(adapter, profiles, launcher, false);
        try
        {
            await watching.Task.WaitAsync(TimeSpan.FromSeconds(2));
            adapter.Probe = adapter.Probe with { Availability = RtssAvailability.NotRunning };
            await service.ApplyProfilesAsync(profiles.Current, true);
            Assert.Equal(0, starts);
        }
        finally
        {
            release.TrySetResult();
        }

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.RefreshAsync();
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task ExitDuringWatchInstallationSchedulesAnotherOwnedRefresh()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Probe = FakeRtssAdapter.ReadyProbe with { ProcessId = 10 };
        var profiles = Profiles();
        var starts = 0;
        WatchLease lease = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RtssLauncher launcher = new(_ =>
        {
            Interlocked.Increment(ref starts);
            adapter.Probe = FakeRtssAdapter.ReadyProbe;
            started.TrySetResult();
            return Task.FromResult(false);
        }, (_, callback) =>
        {
            adapter.Probe = adapter.Probe with { Availability = RtssAvailability.NotRunning };
            callback();
            return lease;
        });
        await using var service = LaunchService(adapter, profiles, launcher);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.RefreshAsync();
        Assert.True(lease.Disposed);
        Assert.Equal(1, starts);
    }

    private static PerformanceService LaunchService(FakeRtssAdapter adapter, ProfileService profiles,
        RtssLauncher launcher, bool enabled = true)
    {
        return new PerformanceService(adapter, launcher,
            (field, value, token) => profiles.SetAsync(field, value, token), profiles.Current, enabled,
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ApplicationTransitionUsesPerPropertyProfilePrecedence()
    {
        var profiles = Profiles(Config(60, 1, Game("steam:7", "game.exe", overlayLevel: 3)));
        await using var adapter = new FakeRtssAdapter();
        await using var service = Service(adapter, profiles);

        await service.RunAsync(profiles, new PerformanceApplicationTarget("steam:7", 7, "game.exe", 123));

        Assert.Equal(new PerformanceValues(60, 3), service.Current.Desired);
        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.FrameLimit, Value: 60 });
        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.OverlayLevel, Value: 3 });
    }

    [Fact]
    public async Task GlobalAndApplicationWritesLandInTheLayerInForce()
    {
        await using var adapter = new FakeRtssAdapter();
        var profiles = Profiles(Config(60, 1, Game("steam:7", "game.exe", 40, 3)));
        await using var service = Service(adapter, profiles);

        await service.SetAsync(
            PerformanceControl.FrameLimit,
            50,
            "overlay",
            "persistent");
        await service.RunAsync(profiles, new PerformanceApplicationTarget("steam:7", 7, "game.exe", 123));
        await service.SetAsync(
            PerformanceControl.FrameLimit,
            45,
            "overlay",
            "application");

        Assert.Equal(50, profiles.Current.Config.Global.FrameLimit);
        Assert.Equal(45, profiles.Current.Config.Games[0].Values.FrameLimit);
        Assert.Equal(45, service.Current.Desired.FrameLimit);
        Assert.Equal(ProfileSource.Game, service.Current.FrameLimitLayer);
    }

    [Fact]
    public async Task SimultaneousClientsAreSerializedThroughOneAdapterPath()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.OnApply = async (request, cancellationToken) =>
        {
            var active = Interlocked.Increment(ref adapter.ActiveApplies);
            adapter.MaximumActiveApplies = Math.Max(adapter.MaximumActiveApplies, active);
            try
            {
                await Task.Delay(20, cancellationToken);
                adapter.Write(request);
                return new RtssApplyResult(true, null);
            }
            finally
            {
                Interlocked.Decrement(ref adapter.ActiveApplies);
            }
        };
        await using var service = CreateService(adapter);

        var overlay = service.SetAsync(
            PerformanceControl.FrameLimit,
            50,
            "overlay",
            "overlay-command");
        var qam = service.SetAsync(
            PerformanceControl.FrameLimit,
            55,
            "qam",
            "qam-command");
        await Task.WhenAll(overlay, qam);

        Assert.Equal(1, adapter.MaximumActiveApplies);
        Assert.Equal(2, adapter.Applies.Count);
        Assert.Contains(service.Current.Observed.FrameLimit, new int?[] { 50, 55 });
        Assert.Equal("qam-command", service.Current.Command.CorrelationId);
    }

    [Fact]
    public async Task AnApplicationWithoutItsOwnProfileIsWrittenThroughTheGlobalProfile()
    {
        // Saving an RTSS profile that does not exist creates it, which sprayed a profile onto
        // every executable that ever took focus (device-observed 2026-09-02). Without a per-game
        // opt-in and without an existing RTSS profile, the global profile carries the value.
        await using var adapter = new FakeRtssAdapter();
        var profiles = Profiles();
        await using var service = Service(adapter, profiles);

        await service.RunAsync(profiles,
            new PerformanceApplicationTarget("process:hitman3.exe", null, "HITMAN3.exe"));
        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            "qam",
            "no-optin");

        Assert.Equal(PerformanceCommandPhase.Applied, command.Phase);
        Assert.All(adapter.Applies, request => Assert.Equal(string.Empty, request.RtssProfileName));
        Assert.DoesNotContain("HITMAN3.exe", adapter.Values.Keys);
    }

    [Fact]
    public async Task AnExistingRtssProfileStillReceivesTheEffectiveValues()
    {
        // An RTSS profile that already exists is the stronger RTSS layer: its explicit values
        // would silently override a global write, so the effective values go into it even without
        // a WSGM per-game entry.
        await using var adapter = new FakeRtssAdapter();
        adapter.ExistingProfiles.Add("game.exe");
        var profiles = Profiles();
        await using var service = Service(adapter, profiles);

        await service.RunAsync(profiles,
            new PerformanceApplicationTarget("process:game.exe", null, "game.exe"));
        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            "qam",
            "existing-profile");

        Assert.Equal(PerformanceCommandPhase.Applied, command.Phase);
        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.FrameLimit, Value: 60 });
    }

    [Fact]
    public async Task InvalidRtssProfileNameNeverReachesAdapter()
    {
        await using var adapter = new FakeRtssAdapter();
        var profiles = Profiles(Config(60));
        await using var service = Service(adapter, profiles);

        await service.RunAsync(profiles, new PerformanceApplicationTarget("steam:7", 7, @"..\Global"));

        // The executable is dropped, so the write waits for a valid one instead of naming a path.
        Assert.Null(service.Current.Target?.RtssProfileName);
        Assert.DoesNotContain(adapter.Applies, request => request.RtssProfileName.Contains('\\'));
    }

    [Fact]
    public async Task IdentityOnlyApplicationAppliesOverlayWhileFrameLimitWaitsForEnrichment()
    {
        await using var adapter = new FakeRtssAdapter();
        var profiles = Profiles(Config(60, 1));
        await using var service = Service(adapter, profiles);

        await service.RunAsync(profiles, new PerformanceApplicationTarget("steam:42", 42, null));
        Assert.Contains(adapter.Applies,
            request => request is { Control: PerformanceControl.OverlayLevel, RtssProfileName: "", Value: 1 });
        Assert.DoesNotContain(adapter.Applies, request => request.Control == PerformanceControl.FrameLimit);

        Assert.True(await profiles.SetGameEnabledAsync(true));
        await service.ApplyProfilesAsync(profiles.Current, true);
        var deferred = await service.SetAsync(
            PerformanceControl.FrameLimit,
            45,
            "test",
            "identity-only");
        Assert.Equal(PerformanceCommandPhase.Deferred, deferred.Phase);
        Assert.True(service.Current.ApplicationProfileEnabled);
        Assert.DoesNotContain(adapter.Applies, request => request.Control == PerformanceControl.FrameLimit);

        await service.RunAsync(profiles, new PerformanceApplicationTarget("steam:42", 42, "game.exe"));

        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.FrameLimit, Value: 45 });
        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.OverlayLevel, Value: 1 });
    }

    private static PerformanceService CreateService(
        IRtssAdapter adapter,
        ProfileConfig? config = null)
    {
        return Service(adapter, Profiles(config));
    }

    [Fact]
    public async Task TheServiceRunsWithNoDevicePlatformPresent()
    {
        await using var service = Service();

        Assert.True(service.Enabled);
        Assert.NotNull(service.Current);
    }

    private sealed class WatchLease : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class FakeRtssAdapter : IRtssAdapter
    {
        public static readonly RtssProbe ReadyProbe = new(
            RtssAvailability.Ready,
            "7.3.7",
            @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe",
            1,
            new RtssCapabilities(
                0,
                240,
                new HashSet<int> { 0, 1, 2, 3, 4 }),
            null);

        public int ActiveApplies;

        public int MaximumActiveApplies;
        public int ProbeCount;

        public int ReadCount;

        public List<RtssOsdPowerStatus> PowerStatuses { get; } = [];

        public RtssOsdMetrics Sensors { get; } = RtssOsdMetrics.Empty;

        public RtssProbe Probe { get; set; } = ReadyProbe;

        public Dictionary<string, PerformanceValues> Values { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = PerformanceValues.Empty
        };

        /// <summary>Profiles RTSS already holds on disk, as the service would find them.</summary>
        public HashSet<string> ExistingProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<RtssApplyRequest> Applies { get; } = [];

        public Func<RtssApplyRequest, CancellationToken, Task<RtssApplyResult>>? OnApply { get; set; }

        public TaskCompletionSource<bool> FirstProbe { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ApplyOsdCustomization(RtssOsdCustomSettings settings)
        {
            // The fake has no renderer; the service only forwards.
        }

        public void ApplyOsdPowerStatus(RtssOsdPowerStatus status)
        {
            PowerStatuses.Add(status);
        }

        public RtssOsdMetrics SampleSensors()
        {
            return Sensors;
        }

        public bool ProfileExists(string rtssProfileName)
        {
            return rtssProfileName.Length == 0
                   || ExistingProfiles.Contains(rtssProfileName)
                   || Values.ContainsKey(rtssProfileName);
        }

        public Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref ProbeCount);
            FirstProbe.TrySetResult(true);
            return Task.FromResult(Probe);
        }

        public Task<RtssReadback> ReadAsync(
            string rtssProfileName,
            long generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref ReadCount);
            Values.TryGetValue(rtssProfileName, out var values);
            return Task.FromResult(new RtssReadback(values ?? PerformanceValues.Empty, DateTimeOffset.UtcNow));
        }

        public Task<RtssApplyResult> ApplyAsync(
            RtssApplyRequest request,
            CancellationToken cancellationToken)
        {
            if (OnApply is not null)
            {
                return OnApply(request, cancellationToken);
            }

            Write(request);
            return Task.FromResult(new RtssApplyResult(true, null));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void Write(RtssApplyRequest request)
        {
            Applies.Add(request);
            Values.TryGetValue(request.RtssProfileName, out var current);
            Values[request.RtssProfileName] = (current ?? PerformanceValues.Empty).With(
                request.Control,
                request.Value);
        }
    }
}
