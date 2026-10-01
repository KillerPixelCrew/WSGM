using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

public sealed class SteamPanelFoldsBackendTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    private string Folder => _temporary.GetPath("folds");

    public void Dispose()
    {
        _temporary.Dispose();
    }

    [Fact]
    public async Task AnOpenedSectionIsPublishedUnderTheIdItWasNamedByAndKept()
    {
        var path = Path.Combine(Folder, "quick-access-folds.json");
        SteamPanelFoldsBackend backend = new(new QuickAccessFolds(path));
        var changes = 0;
        backend.Changed += () => changes++;

        Assert.Empty(backend.ReadState().Open);
        var opened = await backend.SetFoldedAsync("Power profiles", false, CancellationToken.None);
        var again = await backend.SetFoldedAsync("Power profiles", false, CancellationToken.None);
        var theme = await backend.SetFoldedAsync("extensions:wsgm.themes:theme:Dark", false, CancellationToken.None);

        Assert.True(opened.Succeeded);
        Assert.True(again.Succeeded);
        Assert.True(theme.Succeeded);
        Assert.Equal(3, changes);
        Assert.Equal(["Power profiles", "extensions:wsgm.themes:theme:Dark"], backend.ReadState().Open);

        await backend.SetFoldedAsync("Power profiles", true, CancellationToken.None);
        Assert.Equal(["extensions:wsgm.themes:theme:Dark"],
            new SteamPanelFoldsBackend(new QuickAccessFolds(path)).ReadState().Open);
    }
}
