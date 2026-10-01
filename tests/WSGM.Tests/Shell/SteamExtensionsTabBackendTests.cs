using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

/// <summary>The Quick Access plugin tab: WSGM's own sections, and the folds it keeps for every section.</summary>
public sealed class SteamExtensionsTabBackendTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    private string Folder => _temporary.GetPath("tab");

    public void Dispose()
    {
        _temporary.Dispose();
    }

    [Fact]
    public void TheLibrarySectionIsOfferedWithNoPluginInstalled()
    {
        var backend = new SteamExtensionsTabBackend(null, () => "/wsgm/library-import", () => "Xbox");

        var item = Assert.Single(backend.ReadState().Items);

        Assert.Equal(SteamExtensionsTabBackend.LibraryId, item.Id);
        Assert.Equal("Bring your Xbox games into Steam.", item.Detail);
    }

    [Fact]
    public async Task ActivatingTheImportEntryAnswersWithItsRoute()
    {
        var backend = new SteamExtensionsTabBackend(null, () => "/wsgm/library-import", null);

        var result = await backend.ActivateAsync(SteamExtensionsTabBackend.ImportId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("/wsgm/library-import", result.Payload?.GetProperty("route").GetString());
        Assert.False((await backend.ActivateAsync("wsgm.gone", CancellationToken.None)).Succeeded);
        Assert.False((await backend.ConfigureAsync(SteamExtensionsTabBackend.LibraryId, "x", default, 0,
                CancellationToken.None))
            .Succeeded);
    }

    [Fact]
    public void ASessionWithoutTheImporterOrThemesOffersNothing()
    {
        var backend = new SteamExtensionsTabBackend(null, null, null);

        Assert.Empty(backend.ReadState().Items);
    }
}
