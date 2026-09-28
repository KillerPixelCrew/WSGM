using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamPanelFoldsBackendTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wsgm-panel-folds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task AFoldIsPublishedUnderItsTitleAndKeptApartFromTheExtensionsTab()
    {
        var folds = new QuickAccessFolds(Path.Combine(_directory, "quick-access-folds.json"));
        folds.SetFolded("wsgm.themes", true);
        SteamPanelFoldsBackend backend = new(folds);
        var changes = 0;
        backend.Changed += () => changes++;

        Assert.Empty(backend.ReadState().Folded);
        var folded = await backend.SetFoldedAsync("Power profiles", true, CancellationToken.None);
        var again = await backend.SetFoldedAsync("Power profiles", true, CancellationToken.None);
        var charging = await backend.SetFoldedAsync("Charging", true, CancellationToken.None);

        Assert.True(folded.Succeeded);
        Assert.True(again.Succeeded);
        Assert.True(charging.Succeeded);
        Assert.Equal(3, changes);
        Assert.Equal(["Charging", "Power profiles"], backend.ReadState().Folded);
        Assert.True(folds.IsFolded("wsgm.themes"), "the Extensions tab's fold is untouched");
        Assert.False(folds.IsFolded("Power profiles"), "a panel fold never names an extension");

        await backend.SetFoldedAsync("Power profiles", false, CancellationToken.None);
        Assert.Equal(["Charging"], new SteamPanelFoldsBackend(
            new QuickAccessFolds(Path.Combine(_directory, "quick-access-folds.json"))).ReadState().Folded);
    }
}
