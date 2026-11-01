using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class DesktopAppLifecycleTests
{
    private static DateTimeOffset Deadline => DateTimeOffset.UtcNow.AddMinutes(1);

    [Theory]
    [InlineData(@"C:\Apps\DISPLAYFUSION.exe", true)]
    [InlineData(@"C:\Apps\wallpaper64.exe", true)]
    [InlineData(@"C:\Apps\LittleBigMouse.Hook.exe", true)]
    [InlineData(@"C:\Apps\DisplayFusionService.exe", false)]
    [InlineData(@"C:\Apps\MyDisplayFusion.exe", false)]
    [InlineData("steam://rungameid/431960", false)]
    public void LaunchSuppressionMatchesOnlyExplicitProcessNames(string path, bool expected)
    {
        Assert.Equal(expected, DesktopAppLifecycle.MatchesPath(path));
    }

    [Fact]
    public async Task AbsentApplicationsAreNeverStarted()
    {
        var backend = new Backend();
        var lifecycle = new DesktopAppLifecycle(backend, _ => { });
        Assert.True(await lifecycle.StopAsync(CancellationToken.None));
        await lifecycle.RestoreAsync(Deadline);
        Assert.Empty(backend.Restarts);
    }

    [Fact]
    public async Task CapturesBeforeStoppingAndRestoresOnceInReverseOrder()
    {
        var backend = new Backend(0, 1, 2, 3);
        var lifecycle = new DesktopAppLifecycle(backend, _ => { });
        Assert.True(await lifecycle.StopAsync(CancellationToken.None));
        Assert.Equal(4, backend.Stops.Count);
        Assert.Equal(4, backend.CapturesAtFirstStop);
        await lifecycle.RestoreAsync(Deadline);
        await lifecycle.RestoreAsync(Deadline);
        Assert.Equal(backend.Stops.AsEnumerable().Reverse(), backend.Restarts);
    }

    [Fact]
    public async Task PartialFailureRestoresOnlyDispatchedApplications()
    {
        var backend = new Backend(0, 1, 2) { FailStop = 1 };
        var lifecycle = new DesktopAppLifecycle(backend, _ => { });
        Assert.False(await lifecycle.StopAsync(CancellationToken.None));
        await lifecycle.RestoreAsync(Deadline);
        Assert.Equal(2, backend.Restarts.Count);
        Assert.DoesNotContain(backend.Initial[2], backend.Restarts);
    }

    [Fact]
    public async Task CancellationBeforeDispatchDoesNotCreateRestorationOwnership()
    {
        var backend = new Backend(0);
        var lifecycle = new DesktopAppLifecycle(backend, _ => { });
        Assert.False(await lifecycle.StopAsync(new CancellationToken(true)));
        await lifecycle.RestoreAsync(Deadline);
        Assert.Empty(backend.Stops);
        Assert.Empty(backend.Restarts);
    }

    [Fact]
    public async Task RepeatedEntryDoesNotOverwritePendingRestoration()
    {
        var backend = new Backend(0);
        var lifecycle = new DesktopAppLifecycle(backend, _ => { });
        Assert.True(await lifecycle.StopAsync(CancellationToken.None));
        Assert.False(await lifecycle.StopAsync(CancellationToken.None));
        Assert.Single(backend.Stops);
        await lifecycle.RestoreAsync(Deadline);
        Assert.Single(backend.Restarts);
        Assert.True(await lifecycle.StopAsync(CancellationToken.None));
        Assert.Equal(2, backend.Stops.Count);
    }

    [Fact]
    public async Task IndependentlyRestartedAppIsNotDuplicated()
    {
        var backend = new Backend(0);
        var lifecycle = new DesktopAppLifecycle(backend, _ => { });
        await lifecycle.StopAsync(CancellationToken.None);
        backend.Running.Add(backend.Initial[0]);
        await lifecycle.RestoreAsync(Deadline);
        Assert.Empty(backend.Restarts);
    }

    [Fact]
    public async Task UnknownRestartIsNeverDispatchedAgain()
    {
        var backend = new Backend(0) { RestartResult = ScheduledTaskLaunchDisposition.Unknown };
        var lifecycle = new DesktopAppLifecycle(backend, _ => { });
        await lifecycle.StopAsync(CancellationToken.None);
        await lifecycle.RestoreAsync(Deadline);
        await lifecycle.RestoreAsync(Deadline);
        Assert.Single(backend.Restarts);
    }

    [Fact]
    public async Task FailedRestartDoesNotSkipOtherAppsAndRemainsRecoverable()
    {
        var backend = new Backend(0, 1) { FailRestart = 1 };
        var lifecycle = new DesktopAppLifecycle(backend, _ => { });
        await lifecycle.StopAsync(CancellationToken.None);
        await lifecycle.RestoreAsync(Deadline);
        Assert.Equal(2, backend.Restarts.Count);
        backend.FailRestart = -1;
        await lifecycle.RestoreAsync(Deadline);
        Assert.Equal(3, backend.Restarts.Count);
    }

    [Fact]
    public async Task RespawningIntegrationRefusesTakeover()
    {
        var backend = new Backend(0) { Respawn = true };
        var lifecycle = new DesktopAppLifecycle(backend, _ => { });
        Assert.False(await lifecycle.StopAsync(CancellationToken.None));
    }

    private sealed class Backend(params int[] rules) : IDesktopAppBackend
    {
        private int _captures;

        public List<DesktopAppInstance> Initial { get; } =
        [
            .. rules.Select(index => new DesktopAppInstance(
                DesktopAppLifecycle.Rules[index], index, DateTime.UnixEpoch, $@"C:\Apps\{index}.exe"))
        ];

        public List<DesktopAppInstance> Running { get; } =
        [
            .. rules.Select(index => new DesktopAppInstance(
                DesktopAppLifecycle.Rules[index], index, DateTime.UnixEpoch, $@"C:\Apps\{index}.exe"))
        ];

        public List<DesktopAppInstance> Stops { get; } = [];
        public List<DesktopAppInstance> Restarts { get; } = [];
        public int FailStop { get; init; } = -1;
        public int FailRestart { get; set; } = -1;
        public bool Respawn { get; init; }
        public ScheduledTaskLaunchDisposition RestartResult { get; init; } = ScheduledTaskLaunchDisposition.Dispatched;
        public int CapturesAtFirstStop { get; private set; }

        public IReadOnlyList<DesktopAppInstance> Capture(DesktopAppRule rule)
        {
            _captures++;
            return [.. Running.Where(instance => instance.Rule == rule)];
        }

        public Task StopAsync(DesktopAppInstance instance, CancellationToken cancellationToken)
        {
            if (Stops.Count == 0)
            {
                CapturesAtFirstStop = _captures;
            }

            Stops.Add(instance);
            if (!Respawn)
            {
                Running.Remove(instance);
            }

            return instance.ProcessId == FailStop
                ? throw new InvalidOperationException("Exit uncertain")
                : Task.CompletedTask;
        }

        public bool IsRunning(DesktopAppInstance instance)
        {
            return Running.Contains(instance);
        }

        public Task<ScheduledTaskLaunchDisposition> RestartAsync(DesktopAppInstance instance, DateTimeOffset deadline)
        {
            Restarts.Add(instance);
            if (instance.ProcessId == FailRestart)
            {
                throw new InvalidOperationException("Before dispatch");
            }

            if (RestartResult == ScheduledTaskLaunchDisposition.Dispatched)
            {
                Running.Add(instance);
            }

            return Task.FromResult(RestartResult);
        }
    }
}
