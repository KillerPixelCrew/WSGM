using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Core.Sounds;

namespace WSGM.Tests.Shell;

public sealed class SoundPackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WSGM.Tests.sound-service." + Guid.NewGuid().ToString("N"));

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
        using var archive = SoundPackLibraryTests.Archive(("pack.json", """{"name":"Test"}"""), ("navigation.wav", "custom"));
        var id = library.Install(archive);
        var selected = id;
        await using var service = new SoundPackService(library, () => selected, value => selected = value, () => steam, _ => { });
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
}
