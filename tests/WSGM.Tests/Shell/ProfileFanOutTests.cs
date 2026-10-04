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
        using ProfileFanOut fanOut = new(profiles,
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
        await fanOut.Completion;

        Assert.Equal(["first", "second"], calls);
    }

    [Fact]
    public async Task AFailingConsumerDoesNotStopTheRest()
    {
        var profiles = Profiles();
        var reached = false;
        using ProfileFanOut fanOut = new(profiles,
        [
            new ProfileConsumer("broken", (_, _) => throw new InvalidOperationException("boom")),
            new ProfileConsumer("next", (_, _) =>
            {
                reached = true;
                return Task.CompletedTask;
            })
        ]);

        fanOut.Queue(profiles.Current, ProfileChangeKind.Values);
        await fanOut.Completion;

        Assert.True(reached);
    }

    [Fact]
    public async Task AnApplicationChangeCancelsThePassInProgressButAValueChangeWaits()
    {
        var profiles = Profiles();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource replacementStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<(long Generation, bool Cancelled)> passes = [];
        using ProfileFanOut fanOut = new(profiles,
        [
            new ProfileConsumer("slow", async (snapshot, token) =>
            {
                started.TrySetResult();
                if (snapshot.Generation == 13)
                {
                    replacementStarted.TrySetResult();
                }
                try
                {
                    await release.Task.WaitAsync(token);
                    passes.Add((snapshot.Generation, false));
                }
                catch (OperationCanceledException)
                {
                    passes.Add((snapshot.Generation, true));
                    throw;
                }
            })
        ]);

        fanOut.Queue(new ProfileSnapshot(profiles.Current.Config, profiles.Current.Active, 10), ProfileChangeKind.Values);
        await started.Task;
        fanOut.Queue(new ProfileSnapshot(profiles.Current.Config, profiles.Current.Active, 11), ProfileChangeKind.Values);
        release.SetResult();
        await fanOut.Completion;
        Assert.Equal([(10, false), (11, false)], passes);

        passes.Clear();
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fanOut.Queue(new ProfileSnapshot(profiles.Current.Config, profiles.Current.Active, 12), ProfileChangeKind.Values);
        await started.Task;
        fanOut.Queue(new ProfileSnapshot(profiles.Current.Config, profiles.Current.Active, 13), ProfileChangeKind.Application);
        await replacementStarted.Task;
        release.SetResult();
        await fanOut.Completion;
        Assert.Equal([(12, true), (13, false)], passes);
    }

    [Fact]
    public async Task ServiceChangesReachTheConsumers()
    {
        var profiles = Profiles();
        List<long> generations = [];
        using ProfileFanOut fanOut = new(profiles,
        [
            new ProfileConsumer("record", (snapshot, _) =>
            {
                generations.Add(snapshot.Generation);
                return Task.CompletedTask;
            })
        ]);

        await profiles.SetAsync(ProfileField.FrameLimit, 40);
        await fanOut.Completion;

        Assert.Equal([profiles.Current.Generation], generations);
    }
}
