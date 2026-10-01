using System.Net;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Core.Sounds;

namespace WSGM.Tests.Shell;

public sealed class SoundPackServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "WSGM.Tests.sound-service." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task ActivePackRemovalPersistsAndPublishesDefaultsBeforeDeletingAssets()
    {
        var library = new SoundPackLibrary(Path.Combine(_root, "packs"));
        var steam = Path.Combine(_root, "steam");
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
        var retractedBeforeDelete = false;
        service.Changed += () =>
        {
            if (selected == "" && service.ReadOverrides().Sounds.Count == 0 && Directory.Exists(library.PackPath(id)))
            {
                retractedBeforeDelete = true;
            }
        };
        Assert.True((await service.DeleteAsync(id, CancellationToken.None)).Succeeded);
        Assert.True(retractedBeforeDelete);
        Assert.Equal("", selected);
        Assert.Empty(service.ReadOverrides().Sounds);
        Assert.False(Directory.Exists(library.PackPath(id)));
        Assert.Equal("stock", File.ReadAllText(Path.Combine(steam, "steamui", "sounds", "navigation.wav")));
    }

    [Fact]
    public async Task MissingSelectedPackFallsBackWithoutChangingTheStoredChoice()
    {
        var selected = "missing";
        await using var service = new SoundPackService(new SoundPackLibrary(_root), () => selected,
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
        var store = new ThemeStoreClient(handler, "https://example.invalid");
        await using var service = new SoundPackService(new SoundPackLibrary(_root), () => "", _ => { },
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
