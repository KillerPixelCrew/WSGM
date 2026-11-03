using WSGM.Shell;

namespace WSGM.Tests.Shell.Graphics;

public sealed class CoalescedRefreshTests
{
    [Fact]
    public async Task ABurstDuringARunBecomesOneMoreRun()
    {
        var runs = 0;
        var first = new TaskCompletionSource();
        CoalescedRefresh refresh = new(() => ++runs == 1 ? first.Task : Task.CompletedTask);

        var running = refresh.RequestAsync();
        Assert.True(refresh.RequestAsync().IsCompleted);
        Assert.True(refresh.RequestAsync().IsCompleted);
        Assert.True(refresh.RequestAsync().IsCompleted);
        Assert.Equal(1, runs);

        first.SetResult();
        await running;

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task ALoneRequestRunsOnce()
    {
        var runs = 0;
        CoalescedRefresh refresh = new(() =>
        {
            runs++;
            return Task.CompletedTask;
        });

        await refresh.RequestAsync();
        await refresh.RequestAsync();

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task AFailedRunReportsAndTheNextRequestStartsAgain()
    {
        var runs = 0;
        CoalescedRefresh refresh = new(() =>
        {
            runs++;
            return runs == 1 ? Task.FromException(new InvalidOperationException("driver")) : Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(refresh.RequestAsync);
        await refresh.RequestAsync();

        Assert.Equal(2, runs);
    }
}
