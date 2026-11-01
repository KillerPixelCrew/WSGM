using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

public sealed class GameToolsLibraryProjectionTests
{
    private static readonly GameToolCatalog Catalog = GameToolCatalog.Current;
    private static readonly GameToolDefinition Tool = Catalog.Find("optiscaler")!;

    [Fact]
    public void EachInstalledToolReportsItsStatusWithoutAFolderWalk()
    {
        using var directory = new TemporaryDirectory();
        GameToolStore store = new()
        {
            Games =
            [
                Record(1, "Starfield", Installed(directory.Root, "starfield")),
                Record(2, "Cyberpunk 2077", Installed(directory.Root, "cyberpunk")),
                Record(3, "Hogwarts Legacy", Installed(directory.Root, "hogwarts", delete: "dxgi.dll")),
                Record(4, "Alan Wake 2", Installed(directory.Root, "alan") with
                {
                    State = GameToolManifestState.Pending
                }),
                Record(5, "Elden Ring", Installed(directory.Root, "elden") with
                {
                    InstallRoot = Path.Combine(directory.Root, "unplugged")
                }),
                Record(6, "Death Stranding", Installed(directory.Root, "death", "nightly", "10.0.0-pre1",
                    "nightly-20261030:1:a", "OptiScaler_v10.0.0-pre1_20261030.7z")),
                new GameToolRecord { AppId = 7, Name = "No tools", Runtime = new GameRuntimeChoice() },
                Record(8, "Retired tool", Installed(directory.Root, "retired") with { ToolId = "dxvk" })
            ]
        };

        HostedRelease? Latest(GameToolDefinition _, GameToolChannel channel)
        {
            return channel.Id == "nightly"
                ? new HostedRelease("10.0.0-pre1", "nightly-20261101:2:b", "", "GitHub",
                    new ReleaseAsset("OptiScaler_v10.0.0-pre1_20261101.7z", "https://example.test/n.7z"))
                : new HostedRelease("0.9.5", "v0.9.5:3:c", "", "GitHub",
                    new ReleaseAsset("Optiscaler_0.9.5.7z", "https://example.test/s.7z"));
        }

        // Every healthy game is one release behind; only those can report it.
        var library = GameToolsLibraryProjection.Build(store, Catalog, Latest, _ => null);

        Assert.False(library.Loading);
        Assert.Equal(
            ["Alan Wake 2", "Cyberpunk 2077", "Death Stranding", "Elden Ring", "Hogwarts Legacy", "Starfield"],
            library.Games.Select(game => game.Name));
        Assert.Equal(6, library.WithTools);
        Assert.Equal(3, library.Updates);
        var status = library.Games.ToDictionary(game => game.Name, game => Assert.Single(game.Tools));
        Assert.Equal("interrupted", status["Alan Wake 2"].Status);
        Assert.Equal("updateAvailable", status["Cyberpunk 2077"].Status);
        Assert.Equal("Update to 0.9.5", status["Cyberpunk 2077"].StatusLabel);
        Assert.Equal("OptiScaler 0.9.4 as dxgi.dll", status["Cyberpunk 2077"].Detail);
        Assert.Equal("0.9.5", status["Cyberpunk 2077"].LatestVersion);
        Assert.True(status["Cyberpunk 2077"].DefaultChannel);
        Assert.Equal("updateAvailable", status["Death Stranding"].Status);
        Assert.Equal("New build 1 Nov", status["Death Stranding"].StatusLabel);
        Assert.Equal("Nightly", status["Death Stranding"].ChannelLabel);
        Assert.False(status["Death Stranding"].DefaultChannel);
        Assert.Equal("10.0.0-pre1 · nightly 30 Oct 2026", status["Death Stranding"].VersionLabel);
        Assert.Equal("folderUnavailable", status["Elden Ring"].Status);
        Assert.Equal("modified", status["Hogwarts Legacy"].Status);
        Assert.Equal(1, status["Hogwarts Legacy"].ChangedFiles);
        Assert.Equal("OptiScaler 0.9.4 · 1 file changed outside WSGM", status["Hogwarts Legacy"].Detail);
        Assert.Equal("updateAvailable", status["Starfield"].Status);
    }

    [Fact]
    public void AnUnknownLatestReleaseIsNeverReportedAsAnUpdate()
    {
        using var directory = new TemporaryDirectory();
        GameToolStore store = new() { Games = [Record(1, "Starfield", Installed(directory.Root, "starfield"))] };

        var library = GameToolsLibraryProjection.Build(store, Catalog, (_, _) => null, _ => null);

        var tool = Assert.Single(Assert.Single(library.Games).Tools);
        Assert.Equal("upToDate", tool.Status);
        Assert.Equal("Up to date", tool.StatusLabel);
        Assert.Equal(0, library.Updates);
    }

    [Fact]
    public void TheFolderAScanFoundWinsOverTheRecordedRoot()
    {
        using var directory = new TemporaryDirectory();
        var manifest = Installed(directory.Root, "moved");
        var recorded = manifest with { InstallRoot = Path.Combine(directory.Root, "old-library") };
        GameToolStore store = new() { Games = [Record(1, "Moved", recorded)] };

        Assert.Equal("folderUnavailable", Only(GameToolsLibraryProjection.Build(store, Catalog, (_, _) => null,
            _ => null)).Status);
        Assert.Equal("upToDate", Only(GameToolsLibraryProjection.Build(store, Catalog, (_, _) => null,
            _ => manifest.InstallRoot)).Status);
    }

    private static GameToolsToolSummary Only(GameToolsLibrary library)
    {
        return Assert.Single(Assert.Single(library.Games).Tools);
    }

    private static GameToolRecord Record(uint appId, string name, GameToolManifest manifest)
    {
        return new GameToolRecord { AppId = appId, Name = name, Tools = [manifest] };
    }

    // A game folder holding the owned files exactly as the manifest recorded them.
    private static GameToolManifest Installed(string root, string game, string channel = "stable",
        string version = "0.9.4", string releaseId = "v0.9.4:1:a", string asset = "Optiscaler_0.9.4.7z",
        string? delete = null)
    {
        var folder = Path.Combine(root, game);
        Directory.CreateDirectory(folder);
        List<GameToolOwnedFile> files = [];
        foreach (var name in new[] { "dxgi.dll", "OptiScaler.ini" })
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, name);
            var info = new FileInfo(path);
            files.Add(new GameToolOwnedFile
            {
                RelativePath = name, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc,
                Kind = name.EndsWith(".ini", StringComparison.Ordinal)
                    ? GameToolFileKind.Config
                    : GameToolFileKind.Binary
            });
        }

        if (delete is not null)
        {
            File.Delete(Path.Combine(folder, delete));
        }

        return new GameToolManifest
        {
            ToolId = Tool.Id, Channel = channel, Version = version, ReleaseVersion = version, ReleaseId = releaseId,
            AssetName = asset, ProxyName = "dxgi.dll", InstallRoot = folder, TargetDirectory = "", Files = files,
            State = GameToolManifestState.Installed
        };
    }
}
