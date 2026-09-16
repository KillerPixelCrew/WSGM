using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class RunningApplicationCoordinatorTests
{
    [Fact]
    public async Task AQueuedSnapshotCancelsTheOldApplyAndOnlyDispatchesTheNewestControllerTarget()
    {
        var first = Snapshot(
            RunningApplicationTargetState.Active,
            "steam:41",
            "old.exe");
        FakeSource source = new(first);
        var rtssEntered = NewSignal();
        var newestControllerApplied = NewSignal();
        List<long> controllerGenerations = [];

        await using RunningApplicationCoordinator coordinator = new(
            source,
            async (target, cancellationToken) =>
            {
                if (target?.ApplicationId is "steam:41")
                {
                    rtssEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            },
            (snapshot, _) =>
            {
                lock (controllerGenerations)
                {
                    controllerGenerations.Add(snapshot.Generation);
                }

                if (snapshot.Generation is 2)
                {
                    newestControllerApplied.TrySetResult();
                }

                return Task.CompletedTask;
            });

        await rtssEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Publish(Snapshot(
                RunningApplicationTargetState.Active,
                "steam:42",
                "new.exe") with
            {
                Generation = 2
            });
        await newestControllerApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (controllerGenerations)
        {
            Assert.Equal([2], controllerGenerations);
        }
    }

    [Fact]
    public async Task SupersessionCancelsControllerWorkBeforeApplyingTheNextSnapshot()
    {
        var first = Snapshot(
            RunningApplicationTargetState.Active,
            "steam:41",
            "old.exe");
        FakeSource source = new(first);
        var firstControllerEntered = NewSignal();
        var firstControllerCancelled = NewSignal();
        var newestControllerApplied = NewSignal();

        await using RunningApplicationCoordinator coordinator = new(
            source,
            (_, _) => Task.CompletedTask,
            async (snapshot, cancellationToken) =>
            {
                switch (snapshot.Generation)
                {
                    case 1:
                        firstControllerEntered.TrySetResult();
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            firstControllerCancelled.TrySetResult();
                            throw;
                        }

                        break;
                    case 2:
                        newestControllerApplied.TrySetResult();
                        break;
                }
            });

        await firstControllerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Publish(Snapshot(
                RunningApplicationTargetState.Active,
                "steam:42",
                "new.exe") with
            {
                Generation = 2
            });

        await firstControllerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await newestControllerApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ProjectMapsOnlyActiveTruthfulExecutableTarget()
    {
        var snapshot = Snapshot(
            RunningApplicationTargetState.Active,
            "steam:42",
            "game.exe");

        var target = RunningApplicationCoordinator.Project(snapshot);

        Assert.Equal("steam:42", target?.ApplicationId);
        Assert.Equal((uint)42, target?.SteamAppId);
        Assert.Equal("game.exe", target?.RtssProfileName);
    }

    [Fact]
    public void ProjectRetainsIdentityOnlySteamTargetWithoutAnExecutable()
    {
        var snapshot = Snapshot(
            RunningApplicationTargetState.IdentityOnly,
            "steam:42",
            null);

        var target = RunningApplicationCoordinator.Project(snapshot);

        Assert.Equal("steam:42", target?.ApplicationId);
        Assert.Equal((uint)42, target?.SteamAppId);
        Assert.Null(target?.RtssProfileName);
    }

    [Fact]
    public void ProjectClearsTargetWhenApplicationIdentityIsNotAuthoritative()
    {
        RunningApplicationTargetState[] states =
        [
            RunningApplicationTargetState.Global,
            RunningApplicationTargetState.Ambiguous,
            RunningApplicationTargetState.Unavailable
        ];
        foreach (var state in states)
        {
            var snapshot = Snapshot(state, "steam:42", "stale.exe");
            Assert.Null(RunningApplicationCoordinator.Project(snapshot));
        }
    }

    [Theory]
    [InlineData(null, "game.exe")]
    [InlineData("", "game.exe")]
    public void ProjectRejectsIncompleteActiveTarget(string? applicationId, string? profileName)
    {
        var snapshot = Snapshot(
            RunningApplicationTargetState.Active,
            applicationId,
            profileName);

        Assert.Null(RunningApplicationCoordinator.Project(snapshot));
    }

    [Theory]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    [InlineData(3, 4, false)]
    public void SnapshotOrderingRejectsOnlyRegressingGenerations(
        long latestGeneration,
        long candidateGeneration,
        bool expected)
    {
        var snapshot = Snapshot(
                RunningApplicationTargetState.Active,
                "steam:42",
                "game.exe") with
            {
                Generation = candidateGeneration
            };

        Assert.Equal(
            expected,
            RunningApplicationCoordinator.IsOlder(latestGeneration, snapshot));
    }

    private static RunningApplicationTargetSnapshot Snapshot(
        RunningApplicationTargetState state,
        string? applicationId,
        string? profileName)
    {
        return new RunningApplicationTargetSnapshot(
            1,
            1,
            state,
            applicationId,
            42,
            profileName is null ? null : $@"C:\Games\{profileName}",
            profileName,
            null);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeSource(RunningApplicationTargetSnapshot current)
        : IRunningApplicationTargetSource
    {
        public event Action<RunningApplicationTargetSnapshot>? Changed;

        public RunningApplicationTargetSnapshot Current { get; private set; } = current;

        public IDisposable AcquireObservation()
        {
            return new Observation();
        }

        public void Publish(RunningApplicationTargetSnapshot snapshot)
        {
            Current = snapshot;
            Changed?.Invoke(snapshot);
        }

        private sealed class Observation : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
