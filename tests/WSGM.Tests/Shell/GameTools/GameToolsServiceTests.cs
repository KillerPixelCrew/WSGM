using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

public sealed class GameToolsServiceTests
{
    [Fact]
    public async Task AnUnknownChannelIsRefusedBeforeAnyWorkStarts()
    {
        using var directory = new TemporaryDirectory();
        using var service = Service(directory.Root);

        var switched = await service.SwitchChannelAsync(1_091_500, "optiscaler", "beta", false,
            CancellationToken.None);
        var installed = await service.InstallAsync(new GameToolsInstallRequest(1_091_500, "optiscaler", null,
            new Dictionary<string, string>(), false, false, "beta"), CancellationToken.None);

        Assert.False(switched.Succeeded);
        Assert.Equal("Unknown release channel.", switched.Error);
        Assert.False(installed.Succeeded);
        Assert.Equal("Unknown release channel.", installed.Error);
        Assert.Null(service.Peek(1_091_500).Busy);
    }

    [Fact]
    public async Task TheGameListIsReadFromTheStoreAndCached()
    {
        using var directory = new TemporaryDirectory();
        var folder = Path.Combine(directory.Root, "game");
        Directory.CreateDirectory(folder);
        GameToolStorage.Mutate(directory.Root, store => store.With(1_091_500, new GameToolRecord
        {
            AppId = 1_091_500, Name = "Cyberpunk 2077",
            Tools =
            [
                new GameToolManifest
                {
                    ToolId = "optiscaler", Channel = "nightly", Version = "10.0.0-pre1",
                    ReleaseVersion = "10.0.0-pre1", AssetName = "OptiScaler_v10.0.0-pre1_20261031.7z",
                    ProxyName = "winmm.dll", InstallRoot = folder, State = GameToolManifestState.Installed
                }
            ]
        }));
        using var service = Service(directory.Root);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () => changed.TrySetResult();

        var library = await service.ReadLibraryAsync().WaitAsync(AsyncConditions.TimeLimit);

        var tool = Assert.Single(Assert.Single(library.Games).Tools);
        Assert.Equal("Cyberpunk 2077", library.Games[0].Name);
        Assert.Equal("upToDate", tool.Status);
        Assert.Equal("OptiScaler 10.0.0-pre1 · nightly 31 Oct 2026 as winmm.dll", tool.Detail);
        Assert.Equal(1, library.WithTools);
        Assert.Equal(0, library.Updates);
        await changed.Task.WaitAsync(AsyncConditions.TimeLimit);
        Assert.Same(library, service.PeekLibrary());
    }

    [Fact]
    public async Task AnEmptyStoreListsNoGames()
    {
        using var directory = new TemporaryDirectory();
        using var service = Service(directory.Root);

        var library = await service.ReadLibraryAsync().WaitAsync(AsyncConditions.TimeLimit);

        Assert.Empty(library.Games);
        Assert.Null(library.Error);
        Assert.False(library.Loading);
    }

    private static GameToolsService Service(string root)
    {
        return new GameToolsService(new UserDataContext(root, "Local\\WSGM.Tests." + Guid.NewGuid().ToString("N")),
            () => null, _ => false, vendors: () => []);
    }
}
