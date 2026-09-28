using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>The Quick Access plugin tab: WSGM's own sections, and the folds it keeps for every section.</summary>
public sealed class SteamExtensionsTabBackendTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "WSGM.Tests.tab." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private QuickAccessFolds Folds()
    {
        return new QuickAccessFolds(Path.Combine(_directory, "quick-access-folds.json"));
    }

    [Fact]
    public void TheLibrarySectionIsOfferedFoldableAndFoldedWithNoPluginInstalled()
    {
        var backend = new SteamExtensionsTabBackend(null, () => "/wsgm/library-import", () => "Xbox", folds: Folds());

        var item = Assert.Single(backend.ReadState().Items);

        Assert.Equal(SteamExtensionsTabBackend.LibraryId, item.Id);
        Assert.True(item.Collapsible);
        Assert.True(item.Collapsed, "every section starts folded");
        Assert.Equal("Bring your Xbox games into Steam.", item.Detail);
    }

    [Fact]
    public async Task AnOpenedSectionIsKeptAndPublishedAndRaisesChanged()
    {
        var folds = Folds();
        var backend = new SteamExtensionsTabBackend(null, () => "/wsgm/library-import", null, folds: folds);
        var changed = 0;
        backend.Changed += () => changed++;

        var opened = await backend.CollapseAsync(SteamExtensionsTabBackend.LibraryId, false, CancellationToken.None);

        Assert.True(opened.Succeeded);
        Assert.Equal(1, changed);
        Assert.False(backend.ReadState().Items[0].Collapsed);
        Assert.False(
            new SteamExtensionsTabBackend(null, () => "/x", null, folds: Folds()).ReadState().Items[0].Collapsed);
        Assert.True((await backend.CollapseAsync(SteamExtensionsTabBackend.LibraryId, true, CancellationToken.None))
            .Succeeded);
        Assert.True(backend.ReadState().Items[0].Collapsed);
    }

    [Fact]
    public async Task ActivatingTheImportEntryAnswersWithItsRoute()
    {
        var backend = new SteamExtensionsTabBackend(null, () => "/wsgm/library-import", null, folds: Folds());

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
        var backend = new SteamExtensionsTabBackend(null, null, null, folds: Folds());

        Assert.Empty(backend.ReadState().Items);
    }
}
