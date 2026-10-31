using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class ForegroundWindowWatcherTests
{
    [Fact]
    public async Task ObservationStartsAfterSubscriptionAndPublishesTheInitialGame()
    {
        var reads = 0;
        var hooks = 0;
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new ForegroundWindowWatcher(foregroundWindow: () => 1,
            resolveExecutable: _ =>
            {
                Interlocked.Increment(ref reads);
                return ("game.exe", @"D:\Games\game.exe", 42u);
            }, startHook: _ =>
            {
                hooks++;
                return 0;
            });

        Assert.Equal(0, reads);
        Assert.Equal(0, hooks);
        watcher.ApplicationChanged += (name, _, _) => seen.TrySetResult(name);
        watcher.Start();
        watcher.Start();

        Assert.Equal("game.exe", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, hooks);
    }

    [Fact]
    public async Task AnUnreadableStartupIdentityIsRetriedForTheSameWindow()
    {
        var reads = 0;
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new ForegroundWindowWatcher(foregroundWindow: () => 1,
            resolveExecutable: _ => Interlocked.Increment(ref reads) == 1
                ? (string.Empty, null, 0u)
                : ("game.exe", @"D:\Games\game.exe", 42u), startHook: _ => 0);
        watcher.ApplicationChanged += (name, _, _) => seen.TrySetResult(name);

        watcher.Start();

        Assert.Equal("game.exe", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, reads);
    }
}
