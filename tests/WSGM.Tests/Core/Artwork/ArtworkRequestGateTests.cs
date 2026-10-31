using System.Text.Json;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

/// <summary>
///     One provider's pacing and memory: a bound on requests in flight, answers remembered so a title's
///     five lookups ask once, failures remembered briefly, and the person waiting served before the
///     background.
/// </summary>
public sealed class ArtworkRequestGateTests
{
    private static JsonElement Answer(string text)
    {
        using var document = JsonDocument.Parse($"{{\"value\":\"{text}\"}}");
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task ARememberedAnswerIsNotFetchedAgain()
    {
        ArtworkRequestGate gate = new(1, 8);
        var fetches = 0;

        for (var round = 0; round < 3; round++)
        {
            await gate.CachedAsync("page", _ =>
            {
                fetches++;
                return Task.FromResult<JsonElement?>(Answer("a"));
            }, CancellationToken.None);
        }

        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task DuplicatesQueuedBehindTheFirstFindItsAnswer()
    {
        // Five lookups of one page go out together; only the first may reach the provider.
        ArtworkRequestGate gate = new(1, 8);
        TaskCompletionSource<JsonElement?> held = new();
        var fetches = 0;
        Func<CancellationToken, Task<JsonElement?>> fetch = _ =>
        {
            fetches++;
            return held.Task;
        };

        var requests = Enumerable.Range(0, 5).Select(_ => gate.CachedAsync("page", fetch, CancellationToken.None))
            .ToList();
        held.SetResult(Answer("a"));
        await Task.WhenAll(requests);

        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task AFailureIsRememberedSoTheQueueBehindItDoesNotRepeatIt()
    {
        ArtworkRequestGate gate = new(1, 8);
        var fetches = 0;
        Func<CancellationToken, Task<JsonElement?>> failing = _ =>
        {
            fetches++;
            throw new ArtworkProviderException("Screenscraper quota reached.");
        };

        var first = await Assert.ThrowsAsync<ArtworkProviderException>(() =>
            gate.CachedAsync("page", failing, CancellationToken.None));
        var second = await Assert.ThrowsAsync<ArtworkProviderException>(() =>
            gate.CachedAsync("page", failing, CancellationToken.None));

        Assert.Equal(1, fetches);
        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public async Task ClearingForgetsAnswersAndFailures()
    {
        // A key that changed must not be answered with what the old one earned.
        ArtworkRequestGate gate = new(1, 8);
        await Assert.ThrowsAsync<ArtworkProviderException>(() => gate.CachedAsync("page",
            _ => throw new ArtworkProviderException("rejected"), CancellationToken.None));

        gate.Clear();
        var answer = await gate.CachedAsync("page", _ => Task.FromResult<JsonElement?>(Answer("b")),
            CancellationToken.None);

        Assert.Equal("b", answer?.GetProperty("value").GetString());
    }

    [Fact]
    public async Task OnlyTheBoundOfAnswersIsKeptTheOldestGoingFirst()
    {
        ArtworkRequestGate gate = new(1, 2);
        var fetches = 0;
        Func<CancellationToken, Task<JsonElement?>> fetch = _ =>
        {
            fetches++;
            return Task.FromResult<JsonElement?>(Answer("x"));
        };

        await gate.CachedAsync("a", fetch, CancellationToken.None);
        await gate.CachedAsync("b", fetch, CancellationToken.None);
        await gate.CachedAsync("c", fetch, CancellationToken.None);
        await gate.CachedAsync("a", fetch, CancellationToken.None);

        Assert.Equal(4, fetches);
    }

    [Fact]
    public async Task NoMoreThanTheCapacityIsInFlight()
    {
        ArtworkRequestGate gate = new(2, 0);
        TaskCompletionSource release = new();
        var running = 0;
        var peak = 0;

        var requests = Enumerable.Range(0, 6).Select(_ => gate.RunAsync(async _ =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref running));
            await release.Task;
            Interlocked.Decrement(ref running);
            return 0;
        }, CancellationToken.None)).ToList();

        // Two are in and hold their slots; each of the other four is admitted only as one leaves.
        await AsyncConditions.WaitForAsync(() => Volatile.Read(ref running) == 2);
        release.SetResult();
        await Task.WhenAll(requests);

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task ARequestSomeoneIsWaitingOnGoesAheadOfBackgroundWork()
    {
        // The Game Library gathers a whole scan in the background; the artwork page the user just
        // opened must not queue behind it.
        ArtworkRequestGate gate = new(1, 0);
        TaskCompletionSource first = new();
        List<string> order = [];
        var busy = gate.RunAsync(async _ =>
        {
            await first.Task;
            return 0;
        }, CancellationToken.None);

        Task background;
        using (ArtworkRequestGate.Background())
        {
            background = gate.RunAsync(_ =>
            {
                order.Add("background");
                return Task.FromResult(0);
            }, CancellationToken.None);
        }

        var interactive = gate.RunAsync(_ =>
        {
            order.Add("interactive");
            return Task.FromResult(0);
        }, CancellationToken.None);
        first.SetResult();
        await Task.WhenAll(busy, background, interactive);

        Assert.Equal(["interactive", "background"], order);
    }

    [Fact]
    public async Task AQueuedRequestThatIsCancelledGivesUpItsPlace()
    {
        ArtworkRequestGate gate = new(1, 0);
        TaskCompletionSource first = new();
        var busy = gate.RunAsync(async _ =>
        {
            await first.Task;
            return 0;
        }, CancellationToken.None);
        using CancellationTokenSource cancel = new();
        var waiting = gate.RunAsync(_ => Task.FromResult(1), cancel.Token);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.SetResult();
        await busy;

        Assert.Equal(2, await gate.RunAsync(_ => Task.FromResult(2), CancellationToken.None));
    }
}
