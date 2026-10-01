using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Input;

namespace WSGM.Tests.Input;

public sealed class ManagedControllerRouterTests
{
    [Fact]
    public async Task FakeBackendRequiresNeutralFirstStateAndOwnsOnlyOneTarget()
    {
        DeterministicFakeControllerBackend backend = new(ManagedControllerTarget.Xbox360);
        var neutral = CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow);

        var target = await backend.CreateTargetAsync(
            ManagedControllerTarget.Xbox360,
            neutral,
            CancellationToken.None);

        Assert.Equal(1, target.Generation);
        Assert.Contains("create:1:neutral", backend.Operations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.CreateTargetAsync(
            ManagedControllerTarget.Xbox360,
            neutral,
            CancellationToken.None));
        await backend.DisposeAsync();
    }

    [Fact]
    public async Task RouterReplacesByStoppingOutputNeutralizingAndRemovingBeforeCreate()
    {
        DeterministicFakeControllerBackend backend = new();
        DeterministicFakeHapticSink sink = new();
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(ManagedControllerTarget.Xbox360, CancellationToken.None);
        router.ActivateSource();
        Assert.True(await router.RouteAsync(LiveSample(), CancellationToken.None));

        var replacement = await router.ReplaceAsync(ManagedControllerTarget.DualShock4, CancellationToken.None);

        Assert.Equal(2, replacement.Generation);
        var operations = backend.Operations.ToArray();
        Assert.True(Array.IndexOf(operations, "neutralize:1")
                    < Array.IndexOf(operations, "remove:1"));
        Assert.True(Array.IndexOf(operations, "remove:1")
                    < Array.IndexOf(operations, "create:2:neutral"));
        Assert.True(sink.Frames[^1].IsSilent);
        Assert.Equal(ManagedTargetState.Neutral, router.State);
    }

    [Fact]
    public async Task ActivatingAnActiveSourceAgainChangesNothing()
    {
        DeterministicFakeControllerBackend backend = new();
        await using ManagedControllerRouter router = new(backend, new DeterministicFakeHapticSink());
        await router.CreateAsync(ManagedControllerTarget.Xbox360, CancellationToken.None);

        router.ActivateSource();
        router.ActivateSource();

        Assert.Equal(ManagedTargetState.Active, router.State);
    }

    [Fact]
    public async Task RouterDetachesFeedbackRouteBeforeBackendRemovalStarts()
    {
        DeterministicFakeControllerBackend backend = new();
        DeterministicFakeHapticSink sink = new();
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(ManagedControllerTarget.SteamDeckComposite, CancellationToken.None);
        var droppedBeforeRemoval = router.Output.DroppedFrames;
        backend.Removing = _ =>
        {
            backend.EmitOutput(new HapticOutputFrame
            {
                Timestamp = DateTimeOffset.UtcNow,
                LowFrequency = 1,
                HighFrequency = 1
            });
            Assert.Equal(droppedBeforeRemoval + 1, router.Output.DroppedFrames);
        };

        await router.ReplaceAsync(ManagedControllerTarget.Xbox360, CancellationToken.None);

        Assert.DoesNotContain(sink.Frames, frame => frame.LowFrequency > 0);
    }

    [Fact]
    public async Task InvalidSourceSamplePublishesNeutralAndStopsForwarding()
    {
        DeterministicFakeControllerBackend backend = new();
        DeterministicFakeHapticSink sink = new();
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(ManagedControllerTarget.SteamDeckComposite, CancellationToken.None);
        router.ActivateSource();

        var invalid = LiveSample() with { LeftStickX = float.NaN };
        var delivered = await router.RouteAsync(invalid, CancellationToken.None);

        Assert.False(delivered);
        Assert.Equal(ManagedTargetState.Neutral, router.State);
        Assert.Contains("neutralize:1", backend.Operations);
        Assert.True(sink.Frames[^1].IsSilent);
    }

    [Fact]
    public async Task ALateSampleIsRoutedBecauseEverySampleIsTheWholeState()
    {
        // A sample stamped long ago, as one published across a sleep is, is still just the pad's
        // state. Refusing it on age neutralized the pad after every wake.
        DeterministicFakeControllerBackend backend = new();
        await using ManagedControllerRouter router = new(backend, new DeterministicFakeHapticSink());
        await router.CreateAsync(ManagedControllerTarget.SteamDeckComposite, CancellationToken.None);
        router.ActivateSource();

        var late = LiveSample() with { Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromHours(1) };

        Assert.True(await router.RouteAsync(late, CancellationToken.None));
        Assert.Equal(ManagedTargetState.Active, router.State);
    }

    [Fact]
    public async Task OutputRouterDropsOtherTargetKindsAndClampsUnsupportedChannels()
    {
        DeterministicFakeControllerBackend backend = new();
        DeterministicFakeHapticSink sink = new(new HapticCapabilities
        {
            LowFrequency = OutputChannelSupport.Native,
            HighFrequency = OutputChannelSupport.Unsupported,
            MaxFramesPerSecond = 1000
        });
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(ManagedControllerTarget.Xbox360, CancellationToken.None);

        backend.EmitOutput(new HapticOutputFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            LowFrequency = 1,
            HighFrequency = 1
        }, ManagedControllerTarget.DualShock4);
        // Waited for, so the one-frame queue cannot simply replace it with the next frame.
        Assert.True(SpinWait.SpinUntil(() => router.Output.DroppedFrames >= 1, TimeSpan.FromSeconds(1)));
        backend.EmitOutput(new HapticOutputFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            LowFrequency = 0.75f,
            HighFrequency = 1
        });

        Assert.True(SpinWait.SpinUntil(() => sink.Frames.Count == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(0.75f, sink.Frames[0].LowFrequency);
        Assert.Equal(0, sink.Frames[0].HighFrequency);
    }

    [Fact]
    public async Task ASinkFailureDoesNotSilenceTheNextFrame()
    {
        DeterministicFakeControllerBackend backend = new();
        DeterministicFakeHapticSink sink = new(new HapticCapabilities
        {
            LowFrequency = OutputChannelSupport.Native,
            HighFrequency = OutputChannelSupport.Native,
            MaxFramesPerSecond = 1000
        }) { NextFailure = new IOException("simulated rumble failure") };
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(ManagedControllerTarget.Xbox360, CancellationToken.None);

        backend.EmitOutput(new HapticOutputFrame { Timestamp = DateTimeOffset.UtcNow, LowFrequency = 0.5f });
        Assert.True(SpinWait.SpinUntil(() => sink.NextFailure is null, TimeSpan.FromSeconds(1)));
        backend.EmitOutput(new HapticOutputFrame { Timestamp = DateTimeOffset.UtcNow, LowFrequency = 0.25f });

        Assert.True(SpinWait.SpinUntil(() => sink.Frames.Count == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(0.25f, sink.Frames[0].LowFrequency);
    }

    [Fact]
    public async Task FakeBackendReleasesDelayedOutputDeterministically()
    {
        DeterministicFakeControllerBackend backend = new()
        {
            DelayOutput = true
        };
        DeterministicFakeHapticSink sink = new();
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(ManagedControllerTarget.Xbox360, CancellationToken.None);
        backend.EmitOutput(new HapticOutputFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            LowFrequency = 1
        });

        Assert.Empty(sink.Frames);
        backend.ReleaseDelayedOutput();
        Assert.True(SpinWait.SpinUntil(() => sink.Frames.Count == 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task OutputRouterStopsTimedHapticPulseWithoutLatchingThePhysicalMotors()
    {
        DeterministicFakeControllerBackend backend = new();
        DeterministicFakeHapticSink sink = new(new HapticCapabilities
        {
            LowFrequency = OutputChannelSupport.Native,
            HighFrequency = OutputChannelSupport.Native,
            MaxFramesPerSecond = 1000
        });
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(ManagedControllerTarget.SteamDeckComposite, CancellationToken.None);

        backend.EmitOutput(
            new HapticOutputFrame
            {
                Timestamp = DateTimeOffset.UtcNow,
                LowFrequency = 0.5f,
                HighFrequency = 0.5f
            },
            stopAfter: TimeSpan.FromMilliseconds(5));

        Assert.True(SpinWait.SpinUntil(() => sink.Frames.Count == 2, TimeSpan.FromSeconds(1)));
        Assert.Equal(0.5f, sink.Frames[0].LowFrequency);
        Assert.True(sink.Frames[^1].IsSilent);
    }

    [Fact]
    public async Task BackendTargetLossFaultsInput()
    {
        DeterministicFakeControllerBackend backend = new();
        await using ManagedControllerRouter router = new(backend, new DeterministicFakeHapticSink());
        await router.CreateAsync(ManagedControllerTarget.DualShock4, CancellationToken.None);

        backend.LoseTarget();

        Assert.Equal(ManagedTargetState.Faulted, router.State);
        Assert.Null(router.Target);
    }

    private static CanonicalControllerSample LiveSample()
    {
        return new CanonicalControllerSample
        {
            Timestamp = DateTimeOffset.UtcNow,
            Buttons = CanonicalButtons.A,
            LeftStickX = 0.25f,
            LeftStickY = -0.25f,
            LeftTrigger = 0.5f
        };
    }
}

internal sealed class DeterministicFakeHapticSink(HapticCapabilities? capabilities = null) : IPhysicalHapticSink
{
    private readonly List<HapticOutputFrame> _frames = [];
    private readonly Lock _gate = new();

    /// <summary>Thrown by the next apply, then cleared.</summary>
    internal Exception? NextFailure { get; set; }

    internal IReadOnlyList<HapticOutputFrame> Frames
    {
        get
        {
            lock (_gate)
            {
                return [.. _frames];
            }
        }
    }

    public bool IsOwned => true;

    public HapticCapabilities Capabilities { get; } = capabilities ?? new HapticCapabilities
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        MaxFramesPerSecond = 60
    };

    public Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (NextFailure is { } failure)
            {
                NextFailure = null;
                throw failure;
            }

            _frames.Add(frame);
            return Task.CompletedTask;
        }
    }
}

internal sealed class DeterministicFakeControllerBackend : IControllerTargetBackend
{
    private readonly Queue<ControllerTargetOutput> _delayedOutput = [];
    private readonly Lock _gate = new();
    private readonly List<string> _operations = [];
    private bool _disposed;
    private long _nextGeneration;
    private ControllerTargetHandle? _target;

    internal DeterministicFakeControllerBackend(params ManagedControllerTarget[] supportedTargets)
    {
        IReadOnlyList<ManagedControllerTarget> targets = supportedTargets.Length == 0
            ? Enum.GetValues<ManagedControllerTarget>()
            : [.. supportedTargets];
        Health = new ControllerBackendHealth(
            ControllerBackendHealthState.Ready,
            "Deterministic fake backend is ready.",
            new ControllerBackendCapabilities(targets));
    }

    internal ControllerBackendHealth Health { get; set; }

    internal bool DelayOutput { get; init; }

    private Exception? NextCreateFailure { get; set; }

    private Exception? NextPublishFailure { get; set; }

    private Exception? NextRemoveFailure { get; set; }

    internal Action<ControllerTargetHandle>? Removing { get; set; }

    internal IReadOnlyList<string> Operations
    {
        get
        {
            lock (_gate)
            {
                return [.. _operations];
            }
        }
    }

    public event EventHandler<ControllerTargetOutput>? OutputReceived;

    public event EventHandler<long>? TargetLost;

    public Task<ControllerBackendHealth> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            _operations.Add("discover");
            return Task.FromResult(Health);
        }
    }

    public Task<ControllerTargetHandle> CreateTargetAsync(
        ManagedControllerTarget kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_target is not null)
            {
                throw new InvalidOperationException("The fake backend already owns a target.");
            }

            if (NextCreateFailure is { } failure)
            {
                NextCreateFailure = null;
                throw failure;
            }

            if (Health.State is not ControllerBackendHealthState.Ready
                || Health.Capabilities is null
                || !Health.Capabilities.SupportedTargets.Contains(kind))
            {
                throw new InvalidOperationException($"Target {kind} is unavailable.");
            }

            if (!ManagedControllerSampleValidator.IsNeutral(initialNeutralState))
            {
                throw new ArgumentException(
                    "The first target state must be neutral.",
                    nameof(initialNeutralState));
            }

            var generation = ++_nextGeneration;
            _target = new ControllerTargetHandle(kind, generation);
            _operations.Add($"create:{generation}:neutral");
            return Task.FromResult(_target);
        }
    }

    public ValueTask<bool> PublishAsync(
        ControllerTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            if (NextPublishFailure is { } failure)
            {
                NextPublishFailure = null;
                throw failure;
            }

            _operations.Add(ManagedControllerSampleValidator.IsNeutral(sample)
                ? $"publish:{target.Generation}:neutral"
                : $"publish:{target.Generation}:live");
            return ValueTask.FromResult(true);
        }
    }

    public Task NeutralizeAsync(
        ControllerTargetHandle target,
        CanonicalControllerSample neutralState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            if (!ManagedControllerSampleValidator.IsNeutral(neutralState))
            {
                throw new ArgumentException(
                    "Neutralization requires a neutral sample.",
                    nameof(neutralState));
            }

            _operations.Add($"neutralize:{target.Generation}");
            return Task.CompletedTask;
        }
    }

    public Task<bool> RemoveTargetAsync(ControllerTargetHandle target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Action<ControllerTargetHandle>? removing;
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            if (NextRemoveFailure is { } failure)
            {
                NextRemoveFailure = null;
                throw failure;
            }

            _operations.Add($"remove:{target.Generation}");
            removing = Removing;
        }

        removing?.Invoke(target);

        lock (_gate)
        {
            if (_target?.Generation == target.Generation)
            {
                _target = null;
            }

            return Task.FromResult(true);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _target = null;
            _delayedOutput.Clear();
            _operations.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    internal void EmitOutput(
        HapticOutputFrame frame,
        ManagedControllerTarget? sourceKind = null,
        TimeSpan? stopAfter = null)
    {
        ControllerTargetOutput output;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_target is null)
            {
                throw new InvalidOperationException("No target exists.");
            }

            output = new ControllerTargetOutput(frame, sourceKind ?? _target.Kind, stopAfter);
            _operations.Add($"output:{_target.Generation}");
            if (DelayOutput)
            {
                _delayedOutput.Enqueue(output);
                return;
            }
        }

        OutputReceived?.Invoke(this, output);
    }

    internal void ReleaseDelayedOutput()
    {
        while (true)
        {
            ControllerTargetOutput output;
            lock (_gate)
            {
                if (!_delayedOutput.TryDequeue(out output))
                {
                    return;
                }
            }

            OutputReceived?.Invoke(this, output);
        }
    }

    internal void LoseTarget()
    {
        long generation;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_target is null)
            {
                return;
            }

            generation = _target.Generation;
            _target = null;
            _operations.Add($"lost:{generation}");
        }

        TargetLost?.Invoke(this, generation);
    }

    private void ValidateTarget(ControllerTargetHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_target is null || _target != target)
        {
            throw new InvalidOperationException("The target generation is stale.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
