using WSGM.Core;
using WSGM.Shell;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Shell;

public sealed class ProfileFanOutTests
{
    [Fact]
    public async Task ConsumersApplyInTheirRegisteredOrder()
    {
        var profiles = Profiles();
        List<string> calls = [];
        await using ProfileFanOut fanOut = new(profiles,
        [
            new ProfileConsumer("first", (_, _) =>
            {
                calls.Add("first");
                return Task.CompletedTask;
            }),
            new ProfileConsumer("second", (_, _) =>
            {
                calls.Add("second");
                return Task.CompletedTask;
            })
        ]);

        fanOut.Queue(profiles.Current, ProfileChangeKind.Values);
        await fanOut.Idle;

        Assert.Equal(["first", "second"], calls);
    }

    [Fact]
    public async Task AFailingConsumerDoesNotStopTheRest()
    {
        var profiles = Profiles();
        var reached = false;
        await using ProfileFanOut fanOut = new(profiles,
        [
            new ProfileConsumer("broken", (_, _) => throw new InvalidOperationException("boom")),
            new ProfileConsumer("next", (_, _) =>
            {
                reached = true;
                return Task.CompletedTask;
            })
        ]);

        fanOut.Queue(profiles.Current, ProfileChangeKind.Values);
        await fanOut.Idle;

        Assert.True(reached);
    }

    [Fact]
    public async Task AnApplicationChangeCancelsThePassInProgressButAValueChangeWaits()
    {
        var profiles = Profiles();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<(long Generation, bool Cancelled)> passes = [];
        await using ProfileFanOut fanOut = new(profiles,
        [
            new ProfileConsumer("slow", async (snapshot, token) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                    passes.Add((snapshot.Generation, false));
                }
                catch (OperationCanceledException)
                {
                    passes.Add((snapshot.Generation, true));
                    throw;
                }
            })
        ]);

        fanOut.Queue(profiles.Current with { Generation = 10 }, ProfileChangeKind.Values);
        await started.Task;
        fanOut.Queue(profiles.Current with { Generation = 11 }, ProfileChangeKind.Values);
        await fanOut.Idle;
        Assert.Equal([(10, false), (11, false)], passes);

        passes.Clear();
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fanOut.Queue(profiles.Current with { Generation = 12 }, ProfileChangeKind.Values);
        await started.Task;
        fanOut.Queue(profiles.Current with { Generation = 13 }, ProfileChangeKind.Application);
        await fanOut.Idle;
        Assert.Equal([(12, true), (13, false)], passes);
    }

    [Fact]
    public async Task ServiceChangesReachTheConsumers()
    {
        var profiles = Profiles();
        List<long> generations = [];
        await using ProfileFanOut fanOut = new(profiles,
        [
            new ProfileConsumer("record", (snapshot, _) =>
            {
                generations.Add(snapshot.Generation);
                return Task.CompletedTask;
            })
        ]);

        await profiles.SetAsync(ProfileField.FrameLimit, 40);
        await fanOut.Idle;

        Assert.Equal([profiles.Current.Generation], generations);
    }
}
