using System.Diagnostics;
using System.Reflection;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;
using WSGM.Tests.Input;

namespace WSGM.Tests.Shell;

public sealed class DeviceCoordinatorConcurrencyTests
{
    [Fact]
    public async Task FailedUnlockResumeRestartsOnceAndTheFreshControllerCycleForwardsInput()
    {
        DeterministicFakeControllerBackend backend = new();
        await using ControllerManager controllers = new(backend, new DeterministicFakeHapticSink(),
            new HidHideOwnership(new FakeHidHideControl(), new InMemoryHidHideOwnershipStore()),
            @"C:\WSGM.Tests\WSGM.exe", new ControllerProcessPriority(
                () => ProcessPriorityClass.Normal, _ => { }, _ => { }, _ => { }));
        ControllerSelection selection = new(true,
            new ProfileConfig { Global = new ProfileValues { ControllerTarget = ManagedControllerTarget.Xbox360 } },
            "off");
        await controllers.StartAsync(selection, [], null, null, CancellationToken.None);
        await controllers.BlockForwardingAsync("session lock", CancellationToken.None);
        var sample = CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow) with { Buttons = CanonicalButtons.A };
        Assert.False(await controllers.RouteAsync(sample, CancellationToken.None));
        List<string> calls = [];
        var failure = new IOException("unlock resume failed");

        var resumed = await DeviceCoordinator.RunResumeOrRestartAsync(
            () => Task.FromException(failure),
            () => calls.Add("synchronize"),
            async error =>
            {
                Assert.Same(failure, error);
                calls.Add("restart");
                await controllers.ReleaseAsync(HandoffScope.ControllerOnly, _ => Task.CompletedTask,
                    Deadline.After(TimeSpan.FromSeconds(1)), CancellationToken.None, true);
                await controllers.StartAsync(selection, [], null, null, CancellationToken.None);
            },
            CancellationToken.None);

        Assert.False(resumed);
        Assert.Equal(["synchronize", "restart"], calls);
        Assert.Equal(ControllerManagementState.Active, controllers.State);
        Assert.True(await controllers.RouteAsync(sample, CancellationToken.None));
        Assert.Contains("create:2:neutral", backend.Operations);
    }

    [Fact]
    public async Task SuccessfulResumeSynchronizesWithoutRestartingTheCycle()
    {
        List<string> calls = [];
        var resumed = await DeviceCoordinator.RunResumeOrRestartAsync(
            () =>
            {
                calls.Add("resume");
                return Task.CompletedTask;
            },
            () => calls.Add("synchronize"),
            _ =>
            {
                calls.Add("restart");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(resumed);
        Assert.Equal(["resume", "synchronize"], calls);
    }

    [Fact]
    public async Task CancelledResumeSynchronizesAndPropagatesWithoutStartingAnotherCycle()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        var synchronized = false;
        var restarted = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeviceCoordinator.RunResumeOrRestartAsync(
            () => Task.FromException(new OperationCanceledException(cancellation.Token)),
            () => synchronized = true,
            _ =>
            {
                restarted = true;
                return Task.CompletedTask;
            },
            cancellation.Token));

        Assert.True(synchronized);
        Assert.False(restarted);
    }

    [Fact]
    public async Task ResumeThatRanOutOfItsDeadlineRestartsTheCycle()
    {
        var restarted = false;
        var resumed = await DeviceCoordinator.RunResumeOrRestartAsync(
            () => Task.FromException(new OperationCanceledException()),
            () => { },
            _ =>
            {
                restarted = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(resumed);
        Assert.True(restarted);
    }

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
    public async Task CanceledStart_CleansPartialOwnershipRestoresRetryStateAndRethrows()
    {
        using var cancellation = new CancellationTokenSource();
        var state = DeviceCycleState.Faulted;
        var cleaned = false;
        var restartPending = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeviceCoordinator.RunCancellationSafeStartAsync(
                token =>
                {
                    _ = token;
                    state = DeviceCycleState.Activating;
                    cancellation.Cancel();
                    return Task.CompletedTask;
                },
                () =>
                {
                    cleaned = true;
                    restartPending = false;
                    return ValueTask.CompletedTask;
                },
                () => state = DeviceCycleState.Faulted,
                cancellation.Token));

        Assert.True(cleaned);
        Assert.False(restartPending);
        Assert.Equal(DeviceCycleState.Faulted, state);
    }

    [Fact]
    public async Task ClientTeardown_StopsBeforeDetachAndDispose()
    {
        List<string> order = [];

        var teardown = await DeviceCoordinator.RunClientTeardownAsync(
            _ =>
            {
                order.Add("controller");
                return Task.CompletedTask;
            },
            _ =>
            {
                order.Add("stop");
                return Task.FromResult(VerifiedStop());
            },
            () =>
            {
                order.Add("detach");
                return ValueTask.CompletedTask;
            },
            () =>
            {
                order.Add("dispose");
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(teardown.Verified);
        Assert.Equal(["controller", "stop", "detach", "dispose"], order);
    }

    [Fact]
    public async Task ClientTeardown_ThrowingAdmissionAndStateSubscribersCannotSkipProtocolCleanupOrDisposal()
    {
        var admissionFailure = new InvalidOperationException("capability subscriber failed");
        var transitionFailure = new InvalidOperationException("state subscriber failed");
        List<string> order = [];

        var teardown =
            await DeviceCoordinator.RunClientTeardownWithStateNotificationsAsync(
                () =>
                {
                    order.Add("close-admission");
                    throw admissionFailure;
                },
                () =>
                {
                    order.Add("deactivating");
                    throw transitionFailure;
                },
                () => DeviceCoordinator.RunClientTeardownAsync(
                    _ =>
                    {
                        order.Add("controller");
                        return Task.CompletedTask;
                    },
                    _ =>
                    {
                        order.Add("stop");
                        return Task.FromResult(VerifiedStop());
                    },
                    () =>
                    {
                        order.Add("detach");
                        return ValueTask.CompletedTask;
                    },
                    () =>
                    {
                        order.Add("dispose");
                        return ValueTask.CompletedTask;
                    },
                    CancellationToken.None),
                () => order.Add("disabled"));

        Assert.False(teardown.Verified);
        Assert.Contains(admissionFailure, teardown.Failures);
        Assert.Contains(transitionFailure, teardown.Failures);
        Assert.Equal(
            [
                "close-admission",
                "deactivating",
                "controller",
                "stop",
                "detach",
                "dispose",
                "disabled"
            ],
            order);
    }

    [Fact]
    public async Task ClientTeardown_AnUnverifiedStopIsRetainedButDoesNotBlockWhatFollows()
    {
        // A device that cannot read its state back reports every restore as unverified. That is a
        // log line, not a reason to refuse the next start.
        var disposed = false;
        var stopped = VerifiedStop() with
        {
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "restore readback failed")
        };

        var teardown = await DeviceCoordinator.RunClientTeardownAsync(
            _ => Task.CompletedTask,
            _ => Task.FromResult(stopped),
            static () => ValueTask.CompletedTask,
            () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(teardown.Verified);
        Assert.Single(teardown.Failures);
        Assert.True(disposed);
        DeviceCoordinator.ReportDeviceTeardown(teardown, CancellationToken.None);
    }

    [Fact]
    public async Task ClientTeardown_ProtocolExceptionsDoNotSkipStopOrDispose()
    {
        var controllerFailure = new IOException("controller pipe failed");
        var stopFailure = new TimeoutException("plugin stop timed out");
        List<string> order = [];

        var teardown = await DeviceCoordinator.RunClientTeardownAsync(
            _ =>
            {
                order.Add("controller");
                return Task.FromException(controllerFailure);
            },
            _ =>
            {
                order.Add("stop");
                return Task.FromException<DevicePluginState>(stopFailure);
            },
            () =>
            {
                order.Add("detach");
                return ValueTask.CompletedTask;
            },
            () =>
            {
                order.Add("dispose");
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Contains(controllerFailure, teardown.Failures);
        Assert.Contains(stopFailure, teardown.Failures);
        Assert.Equal(["controller", "stop", "detach", "dispose"], order);
        DeviceCoordinator.ReportDeviceTeardown(teardown, CancellationToken.None);
    }

    [Fact]
    public async Task ClientTeardown_CanceledHandoffStillAttemptsStopBeforeDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        List<string> order = [];

        var teardown = await DeviceCoordinator.RunClientTeardownAsync(
            token =>
            {
                order.Add("controller");
                return Task.FromCanceled(token);
            },
            token =>
            {
                order.Add("stop");
                return Task.FromCanceled<DevicePluginState>(token);
            },
            () =>
            {
                order.Add("detach");
                return ValueTask.CompletedTask;
            },
            () =>
            {
                order.Add("dispose");
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(["controller", "stop", "detach", "dispose"], order);
        Assert.Equal(2, teardown.Failures.Count);
        var canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            DeviceCoordinator.ReportDeviceTeardown(
                teardown,
                cancellation.Token));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
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

    private static DevicePluginState VerifiedStop()
    {
        return new DevicePluginState
        {
            State = DeviceCycleState.Disabled,
            CycleGeneration = 1
        };
    }
}
