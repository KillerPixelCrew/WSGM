using System.Reflection;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceCoordinatorConcurrencyTests
{
    [Fact]
    public void ApplicationEntryPoint_IsTheSynchronousStaMainWrapper()
    {
        var entryPoint = typeof(Program).Assembly.EntryPoint
                         ?? throw new InvalidOperationException("WSGM has no assembly entry point.");

        Assert.Equal(typeof(Program), entryPoint.DeclaringType);
        Assert.Equal(nameof(Program.Main), entryPoint.Name);
        Assert.Equal(typeof(int), entryPoint.ReturnType);
        Assert.NotNull(entryPoint.GetCustomAttribute<STAThreadAttribute>());
    }

    [Fact]
    public void ClientTeardown_VerifiedCleanupStillRethrowsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            DeviceCoordinator.ReportDeviceTeardown(
                DeviceClientTeardownResult.Clean,
                cancellation.Token));

        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    [Fact]
    public void OwnerMarkerCreationFailure_FailsClosed()
    {
        var name = $@"Local\WSGM.Tests.DeviceOwner.Failure.{Guid.NewGuid():N}";

        var owner = DeviceCoordinator.TryCreateOwnerMutex(
            name,
            static _ => throw new IOException("simulated named-object failure"));
        var denied = DeviceCoordinator.TryCreateOwnerMutex(
            name,
            static _ => throw new UnauthorizedAccessException("simulated access denial"));
        var unavailable = DeviceCoordinator.TryCreateOwnerMutex(
            name,
            static _ => throw new WaitHandleCannotBeOpenedException("simulated object failure"));

        Assert.Null(owner);
        Assert.Null(denied);
        Assert.Null(unavailable);
    }

    [Fact]
    public void OwnerMarker_IsUnownedAndItsHandleMayBeDisposedOnAnotherThread()
    {
        var name = $@"Local\WSGM.Tests.DeviceOwner.Lifetime.{Guid.NewGuid():N}";
        var marker = Assert.IsType<Mutex>(DeviceCoordinator.TryCreateOwnerMutex(name));
        var ownerForCleanup = marker;
        try
        {
            var duplicate = DeviceCoordinator.TryCreateOwnerMutex(name);
            try
            {
                Assert.Null(duplicate);
            }
            finally
            {
                duplicate?.Dispose();
            }

            var acquiredOnWorker = false;
            Exception? acquireFailure = null;
            var acquireThread = new Thread(() =>
            {
                try
                {
                    acquiredOnWorker = marker.WaitOne(TimeSpan.Zero);
                    if (acquiredOnWorker)
                    {
                        marker.ReleaseMutex();
                    }
                }
                catch (Exception ex)
                {
                    acquireFailure = ex;
                }
            });
            acquireThread.Start();
            Assert.True(acquireThread.Join(TimeSpan.FromSeconds(10)));
            if (!acquiredOnWorker && acquireFailure is null)
            {
                // This is the old initially-owned policy. Release it on the creating thread so a
                // failing regression test cannot strand a thread-owned named mutex in the runner.
                marker.ReleaseMutex();
            }

            Assert.Null(acquireFailure);
            Assert.True(acquiredOnWorker);

            Exception? disposeFailure = null;
            var disposeThread = new Thread(() =>
            {
                try
                {
                    marker.Dispose();
                }
                catch (Exception ex)
                {
                    disposeFailure = ex;
                }
            });
            disposeThread.Start();
            Assert.True(disposeThread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(disposeFailure);
            ownerForCleanup = null;

            using var reacquired = Assert.IsType<Mutex>(
                DeviceCoordinator.TryCreateOwnerMutex(name));
        }
        finally
        {
            ownerForCleanup?.Dispose();
        }
    }

    [Fact]
    public void ProductionOwnerMarker_IsTheExactMachineWideHardwareReservation()
    {
        Assert.Equal(@"Global\WSGM.DeviceOwner", DeviceCoordinator.ProductionOwnerName);
    }

    [Fact]
    public void OwnerReservation_IsExclusiveWhileHeldAndReacquirableAfterRelease()
    {
        // WSGM and Device Lab each hold this exact reservation while plugin code may touch the
        // hardware, so exclusivity while held and reacquirability after release are the
        // load-bearing marker semantics.
        var name = $@"Local\WSGM.Tests.DeviceOwner.Exclusive.{Guid.NewGuid():N}";
        using (Assert.IsType<Mutex>(
                   DeviceCoordinator.TryCreateOwnerMutex(name)))
        {
            Assert.Null(DeviceCoordinator.TryCreateOwnerMutex(name));
        }

        using var reacquired = Assert.IsType<Mutex>(
            DeviceCoordinator.TryCreateOwnerMutex(name));
    }

    // A suspend or resume cut off by the freeze leaves the plugin in a state the runtime refuses to
    // resume (Claw 2026-09-22), or quarantined, or faulted and torn down after the pad re-enumerated
    // on wake (Xbox Ally X 2026-09-28). Each of those gets a fresh cycle; only a clean suspend resumes.
    [Theory]
    [InlineData(DeviceCycleState.Suspended, "Resume")]
    [InlineData(DeviceCycleState.Active, "Restart")]
    [InlineData(DeviceCycleState.Degraded, "Restart")]
    [InlineData(DeviceCycleState.Faulted, "Restart")]
    [InlineData(DeviceCycleState.Activating, "Restart")]
    [InlineData(null, "Restart")]
    public void Resume_OnlyACleanlySuspendedPluginIsResumedInPlace(
        DeviceCycleState? lifecycleState,
        string expected)
    {
        Assert.Equal(expected, DeviceCoordinator.DecideResume(
            true, DeviceCycleState.Suspended, lifecycleState, true, true).ToString());
    }

    [Theory]
    [InlineData(DeviceCycleState.Faulted, true, true, "Restart")]
    [InlineData(DeviceCycleState.Faulted, false, true, "Skip")]
    [InlineData(DeviceCycleState.Faulted, true, false, "Skip")]
    [InlineData(DeviceCycleState.Disabled, true, true, "Skip")]
    public void Resume_AGoneCycleRestartsOnlyAfterASleepThatFaultedIt(
        DeviceCycleState state,
        bool afterSystemSleep,
        bool integrationWanted,
        string expected)
    {
        // A session unlock does not reset hardware, so a teardown that was unverified keeps blocking
        // a restart until a sleep does.
        Assert.Equal(expected, DeviceCoordinator.DecideResume(
            false, state, null, afterSystemSleep, integrationWanted).ToString());
    }
}
