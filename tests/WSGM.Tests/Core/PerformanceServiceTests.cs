using WSGM.Core;
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
    public async Task VerifiedReadbackCompletesTheSingleSharedCommand()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);

        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            "overlay",
            "command-1");

        Assert.Equal(PerformanceCommandPhase.SucceededVerified, command.Phase);
        Assert.Equal(60, service.Current.Desired.FrameLimit);
        Assert.Equal(60, service.Current.Observed.FrameLimit);
        Assert.Equal(PerformanceReadbackQuality.Verified, service.Current.FrameLimitQuality);
        Assert.Single(adapter.Applies);
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
            static (_, _) => Task.FromException(new IOException("disk unavailable")));

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
    public async Task UnprovenReadbackIsReportedAsAppliedUnverified()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Probe = FakeRtssAdapter.ReadyProbe with
        {
            Capabilities = FakeRtssAdapter.ReadyProbe.Capabilities! with
            {
                OverlayLevelReadback = false
            }
        };
        await using var service = CreateService(adapter);

        var command = await service.SetAsync(
            PerformanceControl.OverlayLevel,
            3,
            "overlay",
            "command-4");

        Assert.Equal(PerformanceCommandPhase.AppliedUnverified, command.Phase);
        Assert.Equal(PerformanceReadbackQuality.AppliedUnverified, service.Current.OverlayLevelQuality);
    }

    [Fact]
    public async Task RestartDuringMutationMakesOutcomeIndeterminate()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.OnApply = (request, _) =>
        {
            adapter.Write(request);
            adapter.Probe = adapter.Probe with { Generation = request.Generation + 1 };
            return Task.FromResult(new RtssApplyResult(true, null));
        };
        await using var service = CreateService(adapter);

        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            50,
            "overlay",
            "command-5");

        Assert.Equal(PerformanceCommandPhase.Indeterminate, command.Phase);
    }

    [Fact]
    public async Task AdapterTimeoutIsReportedWithoutEscaping()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.OnApply = static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new RtssApplyResult(true, null);
        };
        await using var service = new PerformanceService(
            adapter,
            PersistAsync,
            commandTimeout: TimeSpan.FromMilliseconds(100));

        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            45,
            "qam",
            "command-6");

        Assert.Equal(PerformanceCommandPhase.TimedOut, command.Phase);
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
    public async Task DriftFromTheDesiredValuesIsReappliedOnce()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Values[string.Empty] = new PerformanceValues(60, 1);
        await using var service = CreateService(
            adapter,
            new PerformancePolicy(new PerformanceValues(60, 1), []));
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
    public async Task AProfileTakenBackAfterARepairIsReportedRatherThanFought()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Values[string.Empty] = new PerformanceValues(60, 1);
        await using var service = CreateService(
            adapter,
            new PerformancePolicy(new PerformanceValues(60, 1), []));
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

        Assert.Equal(2, afterOneRepair);
        Assert.Equal(afterOneRepair, adapter.Applies.Count);
    }

    [Fact]
    public async Task ReloadedPolicyReconcilesThroughTheSameAdapterPath()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);

        await service.UpdatePolicyAsync(new PerformancePolicy(new PerformanceValues(55, 2), []));

        Assert.Equal(new PerformanceValues(55, 2), service.Current.Desired);
        Assert.Equal(new PerformanceValues(55, 2), service.Current.Observed);
        Assert.Equal(2, adapter.Applies.Count);
    }

    [Fact]
    public async Task PollingStartsOnlyWhileAClientOwnsAnObservationLease()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = new PerformanceService(
            adapter,
            PersistAsync,
            pollInterval: TimeSpan.FromMilliseconds(250));
        await Task.Delay(50);
        Assert.Equal(0, adapter.ProbeCount);

        using (service.AcquireObservation())
        {
            await adapter.FirstProbe.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, service.ObserverCount);
        }

        Assert.Equal(0, service.ObserverCount);
        Assert.InRange(service.PollInterval, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ApplicationTransitionUsesPerPropertyProfilePrecedence()
    {
        var policy = new PerformancePolicy(
            new PerformanceValues(60, 1),
            [new PerformanceApplicationPolicy("steam:7", "game.exe", new PerformanceValues(null, 3))]);
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter, policy);

        await service.SetTargetAsync(new PerformanceApplicationTarget("steam:7", 7, "game.exe", 123));

        Assert.Equal(new PerformanceValues(60, 3), service.Current.Desired);
        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.FrameLimit, Value: 60 });
        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.OverlayLevel, Value: 3 });
    }

    [Fact]
    public async Task GlobalAndApplicationWritesHaveOnePersistenceSignalEach()
    {
        await using var adapter = new FakeRtssAdapter();
        var policy = new PerformancePolicy(
            new PerformanceValues(60, 1),
            [new PerformanceApplicationPolicy("steam:7", "game.exe", new PerformanceValues(40, 3))]);
        var policies = new List<PerformancePolicy>();
        await using var service = new PerformanceService(
            adapter,
            (persisted, _) =>
            {
                policies.Add(persisted);
                return Task.CompletedTask;
            },
            policy);

        await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            "overlay",
            "persistent");
        await service.SetTargetAsync(new PerformanceApplicationTarget("steam:7", 7, "game.exe", 123));
        await service.SetAsync(
            PerformanceControl.FrameLimit,
            45,
            "overlay",
            "application");

        Assert.Equal(2, policies.Count);
        Assert.Equal(60, policies[0].Global.FrameLimit);
        Assert.Equal(45, policies[1].Applications[0].Values.FrameLimit);
        Assert.Equal(45, service.Current.Desired.FrameLimit);
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
        await using var service = CreateService(
            adapter,
            new PerformancePolicy(new PerformanceValues(null, null), []));

        await service.SetTargetAsync(
            new PerformanceApplicationTarget("process:hitman3.exe", null, "HITMAN3.exe"));
        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            "qam",
            "no-optin");

        Assert.Equal(PerformanceCommandPhase.SucceededVerified, command.Phase);
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
        await using var service = CreateService(
            adapter,
            new PerformancePolicy(new PerformanceValues(null, null), []));

        await service.SetTargetAsync(
            new PerformanceApplicationTarget("process:game.exe", null, "game.exe"));
        var command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            "qam",
            "existing-profile");

        Assert.Equal(PerformanceCommandPhase.SucceededVerified, command.Phase);
        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.FrameLimit, Value: 60 });
    }

    [Fact]
    public async Task InvalidRtssProfileNameNeverReachesAdapter()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:7", 7, @"..\Global")));

        Assert.Empty(adapter.Applies);
    }

    [Fact]
    public async Task IdentityOnlyApplicationDefersRtssWritesUntilForegroundEnrichment()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(
            adapter,
            new PerformancePolicy(new PerformanceValues(60, 1), []));

        await service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:42", 42, null));
        Assert.Empty(adapter.Applies);
        Assert.Equal(PerformanceCommandPhase.Deferred, service.Current.Command.Phase);

        Assert.True(await service.SetApplicationProfileEnabledAsync(true));
        var deferred = await service.SetAsync(
            PerformanceControl.FrameLimit,
            45,
            "test",
            "identity-only");
        Assert.Equal(PerformanceCommandPhase.Deferred, deferred.Phase);
        Assert.True(service.Current.ApplicationProfileEnabled);
        Assert.Empty(adapter.Applies);

        await service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:42", 42, "game.exe"));

        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.FrameLimit, Value: 45 });
        Assert.Contains(adapter.Applies, request =>
            request is { RtssProfileName: "game.exe", Control: PerformanceControl.OverlayLevel, Value: 1 });
    }

    private static PerformanceService CreateService(
        IRtssAdapter adapter,
        PerformancePolicy? policy = null)
    {
        return new PerformanceService(adapter, PersistAsync, policy);
    }

    private static Task PersistAsync(
        PerformancePolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheServiceRunsWithNoDevicePlatformPresent()
    {
        await using var service = Service();

        Assert.True(service.Enabled);
        Assert.NotNull(service.Current);
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
                new HashSet<int> { 0, 1, 2, 3, 4 },
                true,
                true),
            null);

        public int ActiveApplies;

        public int MaximumActiveApplies;

        public int ProbeCount;

        public List<RtssOsdPowerStatus> PowerStatuses { get; } = [];

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
            Values.TryGetValue(rtssProfileName, out var values);
            return Task.FromResult(new RtssReadback(
                values ?? PerformanceValues.Empty,
                PerformanceReadbackQuality.Verified,
                PerformanceReadbackQuality.Verified,
                DateTimeOffset.UtcNow));
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
