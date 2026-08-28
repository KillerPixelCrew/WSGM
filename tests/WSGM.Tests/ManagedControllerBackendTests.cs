using WSGM.Device.Contracts.Input;
using WSGM.Input;

namespace WSGM.Tests;

public sealed class ManagedControllerBackendTests
{
    [Fact]
    public async Task FakeBackendRequiresNeutralFirstStateAndOwnsOnlyOneTarget()
    {
        DeterministicFakeHidBackend backend = new(VirtualTargetKind.Xbox360);
        CanonicalControllerSample neutral = CanonicalControllerSample.Neutral(
            0,
            7,
            DateTimeOffset.UtcNow);

        HidTargetHandle target = await backend.CreateTargetAsync(
            VirtualTargetKind.Xbox360,
            neutral,
            CancellationToken.None);

        Assert.Equal(1, target.Generation);
        Assert.Contains("create:1:neutral", backend.Operations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.CreateTargetAsync(
            VirtualTargetKind.Xbox360,
            neutral,
            CancellationToken.None));
        await backend.DisposeAsync();
    }

    [Fact]
    public async Task RouterReplacesByStoppingOutputNeutralizingAndRemovingBeforeCreate()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(42);
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(
            VirtualTargetKind.Xbox360,
            42,
            CancellationToken.None);
        router.ActivateSource(42);
        Assert.True(await router.RouteAsync(LiveSample(1, 42),
            CancellationToken.None));

        HidTargetHandle replacement = await router.ReplaceAsync(
            VirtualTargetKind.DualShock4,
            43,
            CancellationToken.None);

        Assert.Equal(2, replacement.Generation);
        string[] operations = backend.Operations.ToArray();
        Assert.True(Array.IndexOf(operations, "neutralize:1")
            < Array.IndexOf(operations, "remove:1"));
        Assert.True(Array.IndexOf(operations, "remove:1")
            < Array.IndexOf(operations, "create:2:neutral"));
        Assert.Contains("target-replacement", sink.StopReasons);
        Assert.Equal(ManagedTargetState.Neutral, router.State);
    }

    [Fact]
    public async Task InvalidSourceSamplePublishesNeutralAndStopsForwarding()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(11);
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(
            VirtualTargetKind.SteamDeckComposite,
            11,
            CancellationToken.None);
        router.ActivateSource(11);

        CanonicalControllerSample invalid = LiveSample(1, 10) with
        {
            LeftStickX = float.NaN,
        };
        bool delivered = await router.RouteAsync(invalid, CancellationToken.None);

        Assert.False(delivered);
        Assert.Equal(ManagedTargetState.Neutral, router.State);
        Assert.Contains("neutralize:1", backend.Operations);
        Assert.Contains("source-invalid", sink.StopReasons);
    }

    [Fact]
    public async Task OutputRouterDropsStaleGenerationAndClampsUnsupportedChannels()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(5, new()
        {
            LowFrequency = OutputChannelSupport.Native,
            HighFrequency = OutputChannelSupport.Unsupported,
            MaxFramesPerSecond = 1000,
        });
        await using ManagedControllerRouter router = new(backend, sink);
        HidTargetHandle target = await router.CreateAsync(
            VirtualTargetKind.Xbox360,
            5,
            CancellationToken.None);

        backend.EmitOutput(new()
        {
            TargetGeneration = target.Generation - 1,
            Timestamp = DateTimeOffset.UtcNow,
            LowFrequency = 1,
            HighFrequency = 1,
        });
        backend.EmitOutput(new()
        {
            TargetGeneration = target.Generation,
            Timestamp = DateTimeOffset.UtcNow,
            LowFrequency = 0.75f,
            HighFrequency = 1,
        });

        Assert.True(SpinWait.SpinUntil(() => sink.Frames.Count == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(0.75f, sink.Frames[0].LowFrequency);
        Assert.Equal(0, sink.Frames[0].HighFrequency);
        Assert.True(router.Output.DroppedFrames >= 1);
    }

    [Fact]
    public async Task FakeBackendReleasesDelayedOutputDeterministically()
    {
        DeterministicFakeHidBackend backend = new()
        {
            DelayOutput = true,
        };
        DeterministicFakeHapticSink sink = new(6);
        await using ManagedControllerRouter router = new(backend, sink);
        HidTargetHandle target = await router.CreateAsync(
            VirtualTargetKind.Xbox360,
            6,
            CancellationToken.None);
        backend.EmitOutput(new()
        {
            TargetGeneration = target.Generation,
            Timestamp = DateTimeOffset.UtcNow,
            LowFrequency = 1,
        });

        Assert.Empty(sink.Frames);
        backend.ReleaseDelayedOutput();
        Assert.True(SpinWait.SpinUntil(() => sink.Frames.Count == 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task BackendTargetLossFaultsInputWithoutReusingGeneration()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(3);
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(
            VirtualTargetKind.DualShock4,
            3,
            CancellationToken.None);

        backend.LoseTarget();

        Assert.Equal(ManagedTargetState.Faulted, router.State);
        Assert.Null(router.Target);
    }

    private static CanonicalControllerSample LiveSample(long sequence, long generation) => new()
    {
        Sequence = sequence,
        DeviceGeneration = generation,
        Timestamp = DateTimeOffset.UtcNow,
        Buttons = CanonicalButtons.A,
        LeftStickX = 0.25f,
        LeftStickY = -0.25f,
        LeftTrigger = 0.5f,
    };
}
