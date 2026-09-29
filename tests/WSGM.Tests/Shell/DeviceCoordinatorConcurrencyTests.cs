using System.Reflection;
using WSGM.Device.Sdk.Capabilities;
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
    public async Task CanceledStart_LifetimeCancellationPreservesClientForShutdown()
    {
        var callerCleanupRan = false;

        await DeviceCoordinator.RunCanceledStartCleanupPolicyAsync(
            true,
            () =>
            {
                callerCleanupRan = true;
                return Task.CompletedTask;
            });

        Assert.False(callerCleanupRan);
    }

    [Fact]
    public async Task CanceledStart_CallerCancellationUsesAFreshBoundedCleanupContext()
    {
        using var canceledCaller = new CancellationTokenSource();
        await canceledCaller.CancelAsync();
        var budget = TimeSpan.FromSeconds(5);
        var receivedDeadline = Deadline.Expired;
        var receivedToken = canceledCaller.Token;

        await DeviceCoordinator.RunCanceledStartCleanupPolicyAsync(
            false,
            () => DeviceCoordinator.RunFreshBoundedCleanupAsync(
                budget,
                (deadline, token) =>
                {
                    receivedDeadline = deadline;
                    receivedToken = token;
                    return Task.CompletedTask;
                }));

        Assert.InRange(receivedDeadline.Remaining, budget - TimeSpan.FromSeconds(1), budget);
        Assert.True(receivedToken.CanBeCanceled);
        Assert.False(receivedToken.IsCancellationRequested);
        Assert.NotEqual(canceledCaller.Token, receivedToken);
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
    public void PendingTeardownFailure_IsRetainedForShutdownWhenNoClientRemains()
    {
        var tracker = new DeviceTeardownFailureTracker();
        var hostExitFailure = new InvalidOperationException(
            "fault while shutdown waited for the transition");

        tracker.Retain(hostExitFailure);
        var drained = tracker.Drain();

        Assert.Single(drained);
        Assert.Same(hostExitFailure, drained[0]);
        Assert.Empty(tracker.Drain());
        Assert.False(tracker.HasFailures);
    }

    [Fact]
    public void PendingTeardownFailure_IsClearedOnlyByALaterVerifiedOwnerTeardown()
    {
        var tracker = new DeviceTeardownFailureTracker();
        tracker.Retain(new InvalidOperationException("earlier cleanup unverified"));

        tracker.ResolveAfterVerifiedOwnerTeardown();

        Assert.Empty(tracker.Drain());
    }

    [Fact]
    public async Task Shutdown_CancelsLifetimeBeforeWaitingForAnInFlightTransition()
    {
        using var lifetime = new CancellationTokenSource();
        using var transitionGate = new SemaphoreSlim(0, 1);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = lifetime.Token.Register(() => canceled.TrySetResult());

        var waiting = DeviceCoordinator.CancelLifetimeAndWaitForTransitionAsync(
            lifetime,
            transitionGate);
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(waiting.IsCompleted);

            transitionGate.Release();
            await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (!waiting.IsCompleted)
            {
                transitionGate.Release();
            }

            await waiting.WaitAsync(TimeSpan.FromSeconds(1));
            transitionGate.Release();
        }
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
    [InlineData(DeviceCycleState.Suspended, true, "Resume")]
    [InlineData(DeviceCycleState.Suspended, false, "Restart")]
    [InlineData(DeviceCycleState.Active, true, "Restart")]
    [InlineData(DeviceCycleState.Degraded, true, "Restart")]
    [InlineData(DeviceCycleState.Faulted, true, "Restart")]
    [InlineData(DeviceCycleState.Activating, true, "Restart")]
    [InlineData(null, true, "Restart")]
    public void Resume_OnlyACleanlySuspendedPluginIsResumedInPlace(
        DeviceCycleState? lifecycleState,
        bool registrationUsable,
        string expected)
    {
        Assert.Equal(expected, DeviceCoordinator.DecideResume(
            true, DeviceCycleState.Suspended, lifecycleState, registrationUsable, true, true).ToString());
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
            false, state, null, false, afterSystemSleep, integrationWanted).ToString());
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
