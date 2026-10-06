using System.Net;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.Tests.Core.Sounds;

namespace WSGM.Tests.Shell;

public sealed class SoundPackServiceTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    private string Root => _temporary.GetPath("sounds");

    public void Dispose()
    {
        _temporary.Dispose();
    }

    [Fact]
    public async Task ActivePackRemovalDeletesTheAssetsThenClearsTheChoiceAndPublishesDefaults()
    {
        var library = new SoundPackLibrary(Path.Combine(Root, "packs"));
        var steam = Path.Combine(Root, "steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamui", "sounds"));
        File.WriteAllText(Path.Combine(steam, "steamui", "sounds", "navigation.wav"), "stock");
        using var archive =
            SoundPackLibraryTests.Archive(("pack.json", """{"name":"Test"}"""), ("navigation.wav", "custom"));
        var id = library.Install(archive);
        var selected = id;
        await using var service =
            new SoundPackService(library, () => selected, value => selected = value, () => steam, _ => { });
        Assert.True((await service.RefreshAsync(CancellationToken.None)).Succeeded);
        Assert.Single(service.ReadOverrides().Sounds);
        Assert.True((await service.DeleteAsync(id, CancellationToken.None)).Succeeded);
        Assert.Equal("", selected);
        Assert.Equal("", service.ReadState().Selected);
        Assert.Empty(service.ReadOverrides().Sounds);
        Assert.False(Directory.Exists(library.PackPath(id)));
        Assert.Equal("stock", File.ReadAllText(Path.Combine(steam, "steamui", "sounds", "navigation.wav")));
    }

    [Fact]
    public async Task MissingSelectedPackFallsBackWithoutChangingTheStoredChoice()
    {
        var selected = "missing";
        await using var service = new SoundPackService(new SoundPackLibrary(Root), () => selected,
            value => selected = value, () => null, _ => { });
        Assert.True((await service.RefreshAsync(CancellationToken.None)).Succeeded);
        Assert.Empty(service.ReadOverrides().Sounds);
        Assert.Equal("missing", selected);
        Assert.Contains("unavailable", service.ReadState().Compatibility);
    }

    [Fact]
    public async Task StopPreviewDoesNotWaitForAnInFlightRepositoryRequest()
    {
        using var handler = new HeldRequest();
        // ReSharper disable once ArgumentsStyleStringLiteral
        var store = new ThemeStoreClient(handler, apiUrl: "https://example.invalid");
        await using var service = new SoundPackService(new SoundPackLibrary(Root), () => "", _ => { },
            () => null, _ => { }, store);
        var browse = service.BrowseAsync(1, "", CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var stopped = await service.StopPreviewAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(stopped.Succeeded);
            Assert.False(browse.IsCompleted);
        }
        finally
        {
            handler.Release.TrySetResult();
        }

        Assert.True((await browse).Succeeded);
    }

    [Fact]
    public async Task RemovingActivePackRetractsCachedAudioEvenWhenSavingDefaultsFails()
    {
        var library = new SoundPackLibrary(Path.Combine(Root, "packs"));
        var steam = Path.Combine(Root, "steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamui", "sounds"));
        File.WriteAllText(Path.Combine(steam, "steamui", "sounds", "navigation.wav"), "stock");
        using var archive = SoundPackLibraryTests.Archive(("pack.json", """{"name":"Test"}"""),
            ("navigation.wav", "custom"));
        var id = library.Install(archive);
        await using var service = new SoundPackService(library, () => id,
            _ => throw new IOException("Configuration is unreadable."), () => steam, _ => { });
        Assert.True((await service.RefreshAsync(CancellationToken.None)).Succeeded);
        Assert.Single(service.ReadOverrides().Sounds);
        var removed = await service.DeleteAsync(id, CancellationToken.None);
        Assert.False(removed.Succeeded);
        Assert.Contains("Configuration is unreadable", removed.Error);
        Assert.Empty(service.ReadOverrides().Sounds);
        Assert.Contains("unavailable", service.ReadState().Compatibility);
        Assert.False(Directory.Exists(library.PackPath(id)));
    }

    [Fact]
    public async Task PlaybackReportsReflectDecodeResultsAndRejectAnOlderSelection()
    {
        var library = new SoundPackLibrary(Path.Combine(Root, "packs"));
        var steam = Path.Combine(Root, "steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamui", "sounds"));
        File.WriteAllText(Path.Combine(steam, "steamui", "sounds", "navigation.wav"), "stock");
        using var archive = SoundPackLibraryTests.Archive(("pack.json", """{"name":"Test"}"""),
            ("navigation.wav", "custom"));
        var selected = library.Install(archive);
        await using var service = new SoundPackService(library, () => selected, value => selected = value,
            () => steam, _ => { });
        Assert.True((await service.RefreshAsync(CancellationToken.None)).Succeeded);
        service.SetHostState(true, new SteamUiPatchSnapshot(SteamSoundOverrideSurface.PatchId, true,
            SteamUiPatchState.Verified, null, default!, null, DateTimeOffset.UtcNow));
        var revision = service.Revision;
        service.ReportPlaybackStatus(new SteamSoundOverrideStatus(revision, true, 0, null));
        Assert.Contains("checking", service.ReadState().Integration);
        service.ReportPlaybackStatus(new SteamSoundOverrideStatus(revision, false, 0,
            "Unreadable sound: navigation.wav"));
        Assert.Contains("Unreadable sound: navigation.wav", service.ReadState().Integration);
        Assert.Contains("stock sounds", service.ReadState().Integration);
        Assert.True((await service.SelectAsync("", CancellationToken.None)).Succeeded);
        service.ReportPlaybackStatus(new SteamSoundOverrideStatus(revision, false, 1, null));
        Assert.Contains("Waiting", service.ReadState().Integration);
        service.ReportPlaybackStatus(new SteamSoundOverrideStatus(service.Revision, false, 0, null));
        Assert.Equal("Steam defaults are active.", service.ReadState().Integration);
    }

    private sealed class HeldRequest : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"total\":0,\"items\":[]}") };
        }
    }
}
