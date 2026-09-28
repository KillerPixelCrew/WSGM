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
    public async Task AnOpenedSectionIsPublishedUnderItsTitleAndKeptApartFromTheExtensionsTab()
    {
        var folds = new QuickAccessFolds(Path.Combine(_directory, "quick-access-folds.json"));
        folds.SetOpen("wsgm.themes", true);
        SteamPanelFoldsBackend backend = new(folds);
        var changes = 0;
        backend.Changed += () => changes++;

        Assert.Empty(backend.ReadState().Open);
        var opened = await backend.SetFoldedAsync("Power profiles", false, CancellationToken.None);
        var again = await backend.SetFoldedAsync("Power profiles", false, CancellationToken.None);
        var charging = await backend.SetFoldedAsync("Charging", false, CancellationToken.None);

        Assert.True(opened.Succeeded);
        Assert.True(again.Succeeded);
        Assert.True(charging.Succeeded);
        Assert.Equal(3, changes);
        Assert.Equal(["Charging", "Power profiles"], backend.ReadState().Open);
        Assert.True(folds.IsOpen("wsgm.themes"), "the Extensions tab's fold is untouched");
        Assert.False(folds.IsOpen("Power profiles"), "a panel fold never names an extension");

        await backend.SetFoldedAsync("Power profiles", true, CancellationToken.None);
        Assert.Equal(["Charging"], new SteamPanelFoldsBackend(
            new QuickAccessFolds(Path.Combine(_directory, "quick-access-folds.json"))).ReadState().Open);
    }
}
