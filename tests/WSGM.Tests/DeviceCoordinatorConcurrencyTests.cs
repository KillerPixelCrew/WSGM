using System.Reflection;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceCoordinatorConcurrencyTests
{
    [Fact]
    public void ApplicationEntryPoint_IsTheSynchronousStaMainWrapper()
    {
        MethodInfo entryPoint = typeof(Program).Assembly.EntryPoint
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
        DeviceCycleState state = DeviceCycleState.Faulted;
        bool cleaned = false;
        bool restartPending = true;

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
        bool callerCleanupRan = false;

        await DeviceCoordinator.RunCanceledStartCleanupPolicyAsync(
            lifetimeCancellationRequested: true,
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
        canceledCaller.Cancel();
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        TimeSpan budget = TimeSpan.FromSeconds(5);
        DateTimeOffset receivedDeadline = default;
        CancellationToken receivedToken = canceledCaller.Token;

        await DeviceCoordinator.RunCanceledStartCleanupPolicyAsync(
            lifetimeCancellationRequested: false,
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

        DeviceClientTeardownResult teardown = await DeviceCoordinator.RunClientTeardownAsync(
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

        DeviceClientTeardownResult teardown =
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
                "disabled",
            ],
            order);
    }

    [Fact]
    public async Task DeviceHostClientDisposal_RetainsFailuresButAlwaysClosesTheHostJobAndLifetime()
    {
        var cancelFailure = new InvalidOperationException("lifetime callback failed");
        var unregisterFailure = new InvalidOperationException("wait unregistration failed");
        var frameFailure = new IOException("frame disposal failed");
        var hostFailure = new InvalidOperationException("host disposal reported failure");
        List<string> order = [];

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            DeviceHostClient.RunDisposeStepsAsync(
                () =>
                {
                    order.Add("cancel-lifetime");
                    throw cancelFailure;
                },
                () =>
                {
                    order.Add("unregister-wait");
                    return ValueTask.FromException(unregisterFailure);
                },
                () => order.Add("cancel-pending"),
                () =>
                {
                    order.Add("wait-reader");
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    order.Add("dispose-frames");
                    return ValueTask.FromException(frameFailure);
                },
                () => order.Add("dispose-state-event"),
                () => order.Add("dispose-state-ring"),
                () =>
                {
                    order.Add("dispose-host-job");
                    throw hostFailure;
                },
                () => order.Add("dispose-lifetime")).AsTask());

        Assert.Contains(cancelFailure, failure.InnerExceptions);
        Assert.Contains(unregisterFailure, failure.InnerExceptions);
        Assert.Contains(frameFailure, failure.InnerExceptions);
        Assert.Contains(hostFailure, failure.InnerExceptions);
        Assert.Equal(
            [
                "cancel-lifetime",
                "unregister-wait",
                "cancel-pending",
                "wait-reader",
                "dispose-frames",
                "dispose-state-event",
                "dispose-state-ring",
                "dispose-host-job",
                "dispose-lifetime",
            ],
            order);
    }

    [Theory]
    [MemberData(nameof(PartialStartupResourceFailures))]
    public void DeviceHostClientStartup_PartialResourceFailureDisposesEveryEarlierHandle(
        int failingStage,
        string[] expectedDisposals)
    {
        var acquisitionFailure = new InvalidOperationException("resource construction failed");
        List<string> disposals = [];

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            DeviceHostClient.AcquireStartupResources<
                TrackingDisposable,
                TrackingDisposable,
                TrackingDisposable>(
                () => new TrackingDisposable(() => disposals.Add("pipe")),
                () => failingStage == 2
                    ? throw acquisitionFailure
                    : new TrackingDisposable(() => disposals.Add("ring")),
                () => failingStage == 3
                    ? throw acquisitionFailure
                    : new TrackingDisposable(() => disposals.Add("event"))));

        Assert.Same(acquisitionFailure, failure);
        Assert.Equal(expectedDisposals, disposals);
    }

    public static TheoryData<int, string[]> PartialStartupResourceFailures => new()
    {
        { 2, ["pipe"] },
        { 3, ["ring", "pipe"] },
    };

    [Fact]
    public async Task HostCompletionFault_StillClosesItsOwnerAndRemainsEligibleForRestart()
    {
        var monitorFailure = new InvalidOperationException("process wait failed");
        List<string> order = [];

        DeviceHostExit exit = await DeviceCoordinator.NormalizeHostCompletionAsync(
            Task.FromException<DeviceHostExit>(monitorFailure));
        IReadOnlyList<Exception> cleanupFailures =
            await DeviceCoordinator.RunHostExitOwnerCleanupAsync(
                () =>
                {
                    order.Add("detach");
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    order.Add("dispose-host-job");
                    return ValueTask.CompletedTask;
                });

        Assert.Equal(DeviceHostExitReason.ProcessFault, exit.Reason);
        Assert.Contains(nameof(InvalidOperationException), exit.Detail, StringComparison.Ordinal);
        Assert.NotNull(DeviceCoordinator.UnverifiedHostExitFailure(
            intentionalStop: false,
            exit));
        Assert.Empty(cleanupFailures);
        Assert.Equal(["detach", "dispose-host-job"], order);
        Assert.True(DeviceCoordinator.ShouldRestartAfterHostExit(
            intentionalStop: false,
            coordinatorDisposed: false,
            integrationEnabled: true,
            exit,
            cleanupVerified: true));
    }

    [Fact]
    public async Task HostExit_DetachFailureCannotSkipJobDisposalOrPermitRestart()
    {
        var detachFailure = new InvalidOperationException("detach failed");
        bool disposed = false;

        IReadOnlyList<Exception> cleanupFailures =
            await DeviceCoordinator.RunHostExitOwnerCleanupAsync(
                () => throw detachFailure,
                () =>
                {
                    disposed = true;
                    return ValueTask.CompletedTask;
                });

        Assert.True(disposed);
        Assert.Contains(detachFailure, cleanupFailures);
        Assert.False(DeviceCoordinator.ShouldRestartAfterHostExit(
            intentionalStop: false,
            coordinatorDisposed: false,
            integrationEnabled: true,
            new DeviceHostExit(
                71,
                DeviceHostExitReason.ProcessFault,
                "faulted",
                TimeSpan.Zero),
            cleanupVerified: false));
    }

    [Theory]
    [MemberData(nameof(ExpectedTerminalReaderClosures))]
    public async Task DeviceHostClientDisposal_TerminalPipeClosureIsNotASecondTeardownFailure(
        Exception terminalClosure)
    {
        await DeviceHostClient.WaitForReaderDuringDisposeAsync(Task.FromException(terminalClosure));
    }

    public static TheoryData<Exception> ExpectedTerminalReaderClosures => new()
    {
        new EndOfStreamException("host completed stop"),
        new IOException("pipe closed during stop"),
        new ObjectDisposedException("pipe"),
    };

    [Fact]
    public async Task DeviceHostClientDisposal_ProtocolFaultRemainsATeardownFailure()
    {
        var protocolFailure = new InvalidDataException("unexpected terminal frame");

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DeviceHostClient.WaitForReaderDuringDisposeAsync(
                Task.FromException(protocolFailure)).AsTask());

        Assert.Same(protocolFailure, failure);
    }

    [Fact]
    public async Task ClientTeardown_UnverifiedResponsesAreRetainedThroughDisposal()
    {
        bool disposed = false;
        DeviceControllerHandoffResponse handoff = VerifiedHandoff() with
        {
            Step = ControllerHandoffStep.TopologyUnverified,
            Result = ControllerHandoffResult.ReleasedVerified,
        };
        DeviceLifecycleNotification stopped = VerifiedStop() with
        {
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "restore readback failed"),
        };

        DeviceClientTeardownResult teardown = await DeviceCoordinator.RunClientTeardownAsync(
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

        DeviceClientTeardownResult teardown = await DeviceCoordinator.RunClientTeardownAsync(
            _ =>
            {
                order.Add("controller");
                return Task.FromException<DeviceControllerHandoffResponse>(controllerFailure);
            },
            _ =>
            {
                order.Add("stop");
                return Task.FromException<DeviceLifecycleNotification>(stopFailure);
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
        InvalidOperationException reported = Assert.Throws<InvalidOperationException>(() =>
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
                teardown,
                CancellationToken.None));
        Assert.IsType<AggregateException>(reported.InnerException);
    }

    [Fact]
    public async Task ClientTeardown_CanceledHandoffStillAttemptsStopBeforeDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> order = [];

        DeviceClientTeardownResult teardown = await DeviceCoordinator.RunClientTeardownAsync(
            token =>
            {
                order.Add("controller");
                return Task.FromCanceled<DeviceControllerHandoffResponse>(token);
            },
            token =>
            {
                order.Add("stop");
                return Task.FromCanceled<DeviceLifecycleNotification>(token);
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
        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
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

        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
                DeviceClientTeardownResult.Clean,
                cancellation.Token));

        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    [Fact]
    public void PendingTeardownFailure_IsRetainedForShutdownWhenNoClientRemains()
    {
        var tracker = new DeviceTeardownFailureTracker();
        Exception hostExitFailure = Assert.IsType<InvalidOperationException>(
            DeviceCoordinator.UnverifiedHostExitFailure(
                intentionalStop: false,
                new DeviceHostExit(
                    71,
                    DeviceHostExitReason.ProcessFault,
                    "fault while shutdown waited for the transition",
                    TimeSpan.FromSeconds(2))));

        tracker.Retain(hostExitFailure);
        IReadOnlyList<Exception> drained = tracker.Drain();

        Assert.Single(drained);
        Assert.Same(hostExitFailure, drained[0]);
        Assert.Empty(tracker.Drain());
        Assert.Null(DeviceCoordinator.UnverifiedHostExitFailure(
            intentionalStop: true,
            new DeviceHostExit(
                0,
                DeviceHostExitReason.Clean,
                "verified stop completed",
                TimeSpan.FromSeconds(1))));
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
        using CancellationTokenRegistration registration = lifetime.Token.Register(
            () => canceled.TrySetResult());

        Task waiting = DeviceCoordinator.CancelLifetimeAndWaitForTransitionAsync(
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
        string name = $@"Local\WSGM.Tests.DeviceOwner.Failure.{Guid.NewGuid():N}";

        Mutex? owner = DeviceCoordinator.TryCreateOwnerMutex(
            name,
            static _ => throw new IOException("simulated named-object failure"));
        Mutex? denied = DeviceCoordinator.TryCreateOwnerMutex(
            name,
            static _ => throw new UnauthorizedAccessException("simulated access denial"));
        Mutex? unavailable = DeviceCoordinator.TryCreateOwnerMutex(
            name,
            static _ => throw new WaitHandleCannotBeOpenedException("simulated object failure"));

        Assert.Null(owner);
        Assert.Null(denied);
        Assert.Null(unavailable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task NormalAdmission_RunningOrUnverifiedDeviceHostDisposesOwnerAndRefuses(
        bool? deviceHostRunning)
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.AdmissionRefusal.{Guid.NewGuid():N}";
        var owner = new Mutex(initiallyOwned: false);
        try
        {
            Mutex? admitted = await DeviceCoordinator.TryReserveOwnerForStartAsync(
                name,
                _ => Task.FromResult(deviceHostRunning),
                create: _ => (owner, true));

            Assert.Null(admitted);
            Assert.Throws<ObjectDisposedException>(() => owner.WaitOne(TimeSpan.Zero));
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task NormalAdmission_VerifiedAbsentDeviceHostRetainsOwner()
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.AdmissionSuccess.{Guid.NewGuid():N}";
        using var owner = new Mutex(initiallyOwned: false);

        Mutex? admitted = await DeviceCoordinator.TryReserveOwnerForStartAsync(
            name,
            static _ => Task.FromResult<bool?>(false),
            create: _ => (owner, true));

        Assert.Same(owner, admitted);
        Assert.False(owner.SafeWaitHandle.IsClosed);
    }

    [Fact]
    public async Task NormalAdmission_UnexpectedSnapshotFailureDisposesOwnerBeforeRethrowing()
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.AdmissionFailure.{Guid.NewGuid():N}";
        var owner = new Mutex(initiallyOwned: false);
        try
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DeviceCoordinator.TryReserveOwnerForStartAsync(
                    name,
                    static _ => Task.FromException<bool?>(
                        new InvalidOperationException("simulated process snapshot failure")),
                    create: _ => (owner, true)));

            Assert.Equal("simulated process snapshot failure", failure.Message);
            Assert.Throws<ObjectDisposedException>(() => owner.WaitOne(TimeSpan.Zero));
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task NormalAdmission_CancellationAfterReservationDisposesTheOwner()
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.AdmissionCancellation.{Guid.NewGuid():N}";
        var owner = new Mutex(initiallyOwned: false);
        using var cancellation = new CancellationTokenSource();
        var snapshot = new TaskCompletionSource<bool?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task<Mutex?> admission = DeviceCoordinator.TryReserveOwnerForStartAsync(
                name,
                _ => snapshot.Task,
                cancellation.Token,
                create: _ => (owner, true));
            cancellation.Cancel();
            snapshot.SetResult(false);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission);
            Assert.Throws<ObjectDisposedException>(() => owner.WaitOne(TimeSpan.Zero));
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public async Task DeviceHostSnapshot_ExecutesOnTheAsynchronousWorkerBoundary()
    {
        int callerThread = 0;
        int snapshotThread = 0;
        var published = new TaskCompletionSource<Task<bool?>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            published.TrySetResult(DeviceHostProcess.IsAnyRunningAsync(
                inspect: () =>
                {
                    snapshotThread = Environment.CurrentManagedThreadId;
                    return false;
                }));
        });

        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(10)));
        Task<bool?> snapshot = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await snapshot.WaitAsync(TimeSpan.FromSeconds(10))) ?? true);
        Assert.NotEqual(callerThread, snapshotThread);
    }

    [Fact]
    public void OwnerMarker_IsUnownedAndItsHandleMayBeDisposedOnAnotherThread()
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.Lifetime.{Guid.NewGuid():N}";
        Mutex marker = Assert.IsType<Mutex>(DeviceCoordinator.TryCreateOwnerMutex(name));
        Mutex? ownerForCleanup = marker;
        try
        {
            Mutex? duplicate = DeviceCoordinator.TryCreateOwnerMutex(name);
            try
            {
                Assert.Null(duplicate);
            }
            finally
            {
                duplicate?.Dispose();
            }

            bool acquiredOnWorker = false;
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

            using Mutex reacquired = Assert.IsType<Mutex>(
                DeviceCoordinator.TryCreateOwnerMutex(name));
        }
        finally
        {
            ownerForCleanup?.Dispose();
        }
    }

    [Fact]
    public void InstallerRollbackRetentionKeepsAnExistingUnownedMarkerAlive()
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.Rollback.{Guid.NewGuid():N}";
        Mutex? installer = Assert.IsType<Mutex>(DeviceCoordinator.TryCreateOwnerMutex(name));
        Mutex? retained = null;
        try
        {
            retained = Assert.IsType<Mutex>(DeviceCoordinator.TryRetainOwnerMutex(name));
            installer.Dispose();
            installer = null;

            Assert.Null(DeviceCoordinator.TryCreateOwnerMutex(name));
        }
        finally
        {
            retained?.Dispose();
            installer?.Dispose();
        }

        using Mutex reacquired = Assert.IsType<Mutex>(
            DeviceCoordinator.TryCreateOwnerMutex(name));
    }

    [Fact]
    public void ProductionOwnerMarker_IsTheExactMachineWideHardwareReservation()
    {
        Assert.Equal(@"Global\WSGM.DeviceOwner", DeviceCoordinator.ProductionOwnerName);
    }

    [Fact]
    public async Task DevicePluginMaintenance_HoldsOwnerReservationThroughTheWholeOperation()
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.Maintenance.{Guid.NewGuid():N}";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> maintenance = Program.RunDevicePluginMaintenanceWithOwnerReservationAsync(
            name,
            "test maintenance",
            async () =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return 23;
            });
        int outcome = 0;
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
        using Mutex reacquired = Assert.IsType<Mutex>(
            DeviceCoordinator.TryCreateOwnerMutex(name));
    }

    private static DeviceControllerHandoffResponse VerifiedHandoff() => new()
    {
        Step = ControllerHandoffStep.TopologyVerified,
        Result = ControllerHandoffResult.ReleasedVerified,
    };

    private static DeviceLifecycleNotification VerifiedStop() => new()
    {
        State = DeviceCycleState.Disabled,
        CycleGeneration = 1,
    };

    private sealed class TrackingDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Action? current = Interlocked.Exchange(ref _dispose, null);
            current?.Invoke();
        }
    }
}
