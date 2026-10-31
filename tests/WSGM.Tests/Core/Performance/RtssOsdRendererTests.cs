using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class RtssOsdRendererTests
{
    [Fact]
    public async Task RichOverlayPublishesBeforeItsSensorCollectorCanBlock()
    {
        using var release = new ManualResetEventSlim();
        var sampling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var renderer = new RtssOsdRenderer(write: text =>
        {
            published.TrySetResult(text);
            return true;
        }, sample: () =>
        {
            sampling.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return RtssOsdMetrics.Empty;
        });
        renderer.SetLevel(2);
        renderer.Start();
        try
        {
            await sampling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(published.Task.IsCompleted);
            Assert.Contains("<FR>", await published.Task);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task MinimalOverlayPublishesWithoutCollectingSensors()
    {
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = 0;
        await using var renderer = new RtssOsdRenderer(write: text =>
        {
            if (text.Length > 0)
            {
                published.TrySetResult(text);
            }

            return true;
        }, sample: () =>
        {
            Interlocked.Increment(ref samples);
            throw new IOException("Sensors unavailable.");
        });

        renderer.SetLevel(1);
        renderer.Start();
        var text = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await renderer.DisposeAsync();

        Assert.Contains("<FR>", text);
        Assert.Equal(0, samples);
    }

    [Fact]
    public async Task ARefusedClearIsRetriedInsteadOfParkingTheRenderer()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clears = 0;
        await using var renderer = new RtssOsdRenderer(write: text =>
        {
            if (text.Length > 0)
            {
                shown.TrySetResult();
                return true;
            }

            if (Interlocked.Increment(ref clears) == 1)
            {
                return false;
            }

            cleared.TrySetResult();
            return true;
        });

        renderer.SetLevel(1);
        renderer.Start();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        renderer.SetLevel(0);
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, clears);
    }
}
