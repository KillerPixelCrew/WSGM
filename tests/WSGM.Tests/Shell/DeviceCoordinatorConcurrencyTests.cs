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
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var budget = TimeSpan.FromSeconds(5);
        DateTimeOffset receivedDeadline = default;
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
                },
                () => now));

        Assert.Equal(now.Add(budget), receivedDeadline);
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
                return Task.FromResult(VerifiedHandoff());
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
                        return Task.FromResult(VerifiedHandoff());
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
    public async Task ClientTeardown_UnverifiedResponsesAreRetainedThroughDisposal()
    {
        var disposed = false;
        var handoff = VerifiedHandoff() with
        {
            Step = ControllerHandoffStep.TopologyUnverified,
            Result = ControllerHandoffResult.ReleasedVerified
        };
        var stopped = VerifiedStop() with
        {
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "restore readback failed")
        };

        var teardown = await DeviceCoordinator.RunClientTeardownAsync(
            _ => Task.FromResult(handoff),
            _ => Task.FromResult(stopped),
            static () => ValueTask.CompletedTask,
            () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(teardown.Verified);
        Assert.Equal(2, teardown.Failures.Count);
        Assert.True(disposed);
        Assert.Throws<InvalidOperationException>(() =>
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
                teardown,
                CancellationToken.None));
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
                return Task.FromException<ControllerHandoff>(controllerFailure);
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
        var reported = Assert.Throws<InvalidOperationException>(() =>
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
                teardown,
                CancellationToken.None));
        Assert.IsType<AggregateException>(reported.InnerException);
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
                return Task.FromCanceled<ControllerHandoff>(token);
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
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
                teardown,
                cancellation.Token));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.IsType<AggregateException>(canceled.InnerException);
    }

    [Fact]
    public void ClientTeardown_VerifiedCleanupStillRethrowsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
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
        // Plugin maintenance holds this exact reservation across the whole slot operation
        // (a using scope around the maintenance body in Program), so exclusivity while held
        // and reacquirability after release are the load-bearing marker semantics.
        var name = $@"Local\WSGM.Tests.DeviceOwner.Maintenance.{Guid.NewGuid():N}";
        using (Assert.IsType<Mutex>(
                   DeviceCoordinator.TryCreateOwnerMutex(name)))
        {
            Assert.Null(DeviceCoordinator.TryCreateOwnerMutex(name));
        }

        using var reacquired = Assert.IsType<Mutex>(
            DeviceCoordinator.TryCreateOwnerMutex(name));
    }

    [Fact]
    public async Task DevicePluginMaintenance_HoldsOwnerReservationThroughTheWholeOperation()
    {
        var name = $@"Local\WSGM.Tests.DeviceOwner.Maintenance.{Guid.NewGuid():N}";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = Program.RunDevicePluginMaintenanceWithOwnerReservationAsync(
            name,
            "test maintenance",
            async () =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return 23;
            });
        int outcome;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Null(DeviceCoordinator.TryCreateOwnerMutex(name));
        }
        finally
        {
            release.TrySetResult();
            outcome = await maintenance.WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(23, outcome);
        using var reacquired = Assert.IsType<Mutex>(
            DeviceCoordinator.TryCreateOwnerMutex(name));
    }

    private static ControllerHandoff VerifiedHandoff()
    {
        return new ControllerHandoff
        {
            Step = ControllerHandoffStep.TopologyVerified,
            Result = ControllerHandoffResult.ReleasedVerified
        };
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
