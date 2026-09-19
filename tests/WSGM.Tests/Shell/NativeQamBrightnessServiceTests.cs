using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class NativeQamBrightnessServiceTests
{
    [Fact]
    public async Task SharedStateTracksExternalChangesAndDisplayLossWithoutWriting()
    {
        int? brightness = 42;
        var changes = 0;
        using NativeQamBrightnessService service = new(() => true, () => { },
            () => brightness, _ => throw new InvalidOperationException("Readback must not write."),
            Timeout.InfiniteTimeSpan);
        service.Changed += () => changes++;
        var initial = await service.ReadAsync();
        Assert.Equal(42, service.Current!.Percent);
        var unchanged = await service.ReadAsync();
        Assert.Equal(initial!.Revision, unchanged!.Revision);
        Assert.Equal(1, changes);
        brightness = 73;
        var changed = await service.ReadAsync();
        Assert.Equal(73, service.Current!.Percent);
        Assert.True(changed!.Revision > unchanged.Revision);
        brightness = null;
        await service.ReadAsync();
        Assert.Null(service.Current);
        Assert.Equal(3, changes);
        brightness = 21;
        var restored = await service.ReadAsync();
        Assert.Equal(21, service.Current!.Percent);
        Assert.True(restored!.Revision > changed.Revision);
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task OverlappingSliderRequestsApplyInAdmissionOrder()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> writes = [];
        var brightness = 100;
        using NativeQamBrightnessService service = new(() => true, () => { }, () => brightness,
            value =>
            {
                if (writes.Count == 0)
                {
                    entered.SetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                }

                writes.Add(value);
                brightness = value;
                return true;
            }, Timeout.InfiniteTimeSpan);
        var first = service.SetBrightnessAsync(60, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.SetBrightnessAsync(20, CancellationToken.None);
        release.Set();
        await Task.WhenAll(first, second);
        Assert.Equal([60, 20], writes);
        Assert.Equal(20, (await service.ReadAsync())!.Percent);
    }

    [Fact]
    public async Task WriteReturnsConfirmedReadbackWithANewerRevisionThanAnEarlierPoll()
    {
        var brightness = 100;
        var writes = 0;
        var publications = 0;
        using NativeQamBrightnessService service = new(() => true, () => publications++,
            () => brightness, value =>
            {
                brightness = value;
                writes++;
                return true;
            }, Timeout.InfiniteTimeSpan);
        var before = await service.ReadAsync();
        var result = await service.SetBrightnessAsync(31, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(31, result.Payload!.Value.GetProperty("percent").GetInt32());
        Assert.True(result.Payload.Value.GetProperty("revision").GetInt64() > before!.Revision);
        Assert.Equal(1, writes);
        Assert.Equal(1, publications);
        Assert.Equal(31, (await service.ReadAsync())!.Percent);
        brightness = 47;
        Assert.Equal(47, (await service.ReadAsync())!.Percent);
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(100)]
    public async Task MissingOrMismatchingReadbackReportsFailureWithoutRetry(int? readback)
    {
        var writes = 0;
        using NativeQamBrightnessService service = new(() => true, () => { },
            () => readback, _ =>
            {
                writes++;
                return true;
            }, Timeout.InfiniteTimeSpan);

        var result = await service.SetBrightnessAsync(31, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Null(result.Payload);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task UnavailableReadbackPublishesNothingInsteadOfFullBrightness()
    {
        using NativeQamBrightnessService service = new(() => true, () => { },
            () => null, _ => throw new InvalidOperationException(), Timeout.InfiniteTimeSpan);
        Assert.Null(await service.ReadAsync());
    }

    [Fact]
    public async Task CanceledDisabledDisposedAndInvalidRequestsNeverReachHardware()
    {
        var active = true;
        var writes = 0;
        using NativeQamBrightnessService service = new(() => active, () => { },
            () => 31, _ =>
            {
                writes++;
                return true;
            }, Timeout.InfiniteTimeSpan);
        using CancellationTokenSource canceled = new();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetBrightnessAsync(31, canceled.Token));
        Assert.False((await service.SetBrightnessAsync(101, CancellationToken.None)).Succeeded);
        active = false;
        Assert.False((await service.SetBrightnessAsync(31, CancellationToken.None)).Succeeded);
        active = true;
        // ReSharper disable once DisposeOnUsingVariable
        service.Dispose();
        Assert.False((await service.SetBrightnessAsync(31, CancellationToken.None)).Succeeded);
        Assert.Null(await service.ReadAsync());
        Assert.Equal(0, writes);
    }
}
