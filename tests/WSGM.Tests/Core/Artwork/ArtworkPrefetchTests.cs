using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     A save's images, started ahead of the titles that use them: each downloaded once, handed over once,
///     a bounded number held, and a download a stopped save cancelled started again.
/// </summary>
public sealed class ArtworkPrefetchTests
{
    [Fact]
    public async Task AnImageStartedAheadIsHandedOverWithoutASecondDownload()
    {
        List<string> downloads = [];
        ArtworkPrefetch prefetch = new((url, _) =>
        {
            downloads.Add(url);
            return Task.FromResult(new byte[] { 1 });
        });

        prefetch.Start(["https://s/a.png", "", "https://s/b.png"], CancellationToken.None);
        prefetch.Start(["https://s/b.png", "https://s/c.png"], CancellationToken.None);

        Assert.Equal([1], await prefetch.TakeAsync("https://s/a.png", CancellationToken.None));
        Assert.Equal(["https://s/a.png", "https://s/b.png", "https://s/c.png"], downloads);
    }

    [Fact]
    public async Task AnImageIsHandedOverOnceAndOneNobodyStartedDownloadsWhenTaken()
    {
        var downloads = 0;
        ArtworkPrefetch prefetch = new((_, _) =>
        {
            downloads++;
            return Task.FromResult(new byte[] { 2 });
        });

        prefetch.Start(["https://s/a.png"], CancellationToken.None);
        await prefetch.TakeAsync("https://s/a.png", CancellationToken.None);
        await prefetch.TakeAsync("https://s/a.png", CancellationToken.None);
        await prefetch.TakeAsync("https://s/new.png", CancellationToken.None);

        Assert.Equal(3, downloads);
    }

    [Fact]
    public async Task OnlyTheCapacityIsHeldTheOldestDroppedFirst()
    {
        List<string> downloads = [];
        ArtworkPrefetch prefetch = new((url, _) =>
        {
            downloads.Add(url);
            return Task.FromResult(Array.Empty<byte>());
        });
        var urls = Enumerable.Range(0, ArtworkPrefetch.Capacity + 1).Select(index => $"https://s/{index}.png")
            .ToList();

        prefetch.Start(urls, CancellationToken.None);
        await prefetch.TakeAsync(urls[0], CancellationToken.None);
        await prefetch.TakeAsync(urls[1], CancellationToken.None);

        Assert.Equal([.. urls, urls[0]], downloads);
    }

    [Fact]
    public async Task ADownloadAStoppedSaveCancelledStartsAgainWhenAsked()
    {
        var downloads = 0;
        ArtworkPrefetch prefetch = new(async (_, token) =>
        {
            downloads++;
            if (token.CanBeCanceled)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return [3];
        });
        using CancellationTokenSource save = new();
        prefetch.Start(["https://s/a.png"], save.Token);
        save.Cancel();

        // The next save asks for the same image and gets it, not the stopped save's cancellation.
        Assert.Equal([3], await prefetch.TakeAsync("https://s/a.png", CancellationToken.None));
        Assert.Equal(2, downloads);
    }

    [Fact]
    public async Task ADownloadsFailureReachesTheTitleThatTakesIt()
    {
        ArtworkPrefetch prefetch = new((_, _) =>
            Task.FromException<byte[]>(new ArtworkProviderException("Could not download the artwork image.")));

        prefetch.Start(["https://s/a.png"], CancellationToken.None);

        var failure = await Assert.ThrowsAsync<ArtworkProviderException>(() =>
            prefetch.TakeAsync("https://s/a.png", CancellationToken.None));
        Assert.Equal("Could not download the artwork image.", failure.Message);
    }
}
