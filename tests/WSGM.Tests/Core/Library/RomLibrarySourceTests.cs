using System.Buffers.Binary;
using System.Text;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core.Library;

public sealed class RomLibrarySourceTests
{
    [Theory]
    [InlineData("psp", "Game.chd")]
    [InlineData("n64", "Game.7z")]
    [InlineData("sega32x", "Game.zip")]
    [InlineData("pico", "Game.zip")]
    public async Task SupportedCollectionFormatsAreDiscovered(string system, string file)
    {
        using TemporaryDirectory temporary = new();
        Write(temporary.Root, file);
        var game = Assert.Single(await Source(temporary.Root, system).DiscoverAsync([], CancellationToken.None));
        Assert.Equal(system, game.Content!.SystemId);
        Assert.EndsWith(file, game.InstallPath);
    }

    [Theory]
    [InlineData("_UPDATES_DLC\\*")]
    [InlineData("_UPDATES_DLC/*")]
    [InlineData("*\\_UPDATES_DLC\\*")]
    [InlineData("_UPDATES_DLC*")]
    public async Task ExclusionsMatchFoldersWithEitherPathSeparator(string pattern)
    {
        using TemporaryDirectory temporary = new();
        Write(temporary.Root, "_UPDATES_DLC/Hidden.gb");
        Write(temporary.Root, "Kept.gb");
        var game = Assert.Single(await Source(temporary.Root, "gb", [pattern]).DiscoverAsync([], CancellationToken.None));
        Assert.Equal("Kept", game.Name);
    }

    [Fact]
    public async Task SwitchUpdatesAndDlcAreExcludedWhileUnknownAndBaseGamesRemain()
    {
        using TemporaryDirectory temporary = new();
        foreach (var file in new[]
                 {
                     "Game [01006F8002326000][v0].nsp", "Game [01006F8002326800][v65536].nsp",
                     "Game [01006F8002327001][v0].nsp", "_UPDATES_DLC/Untagged.nsp", "Unknown.xci",
                     "Update Quest.nsp"
                 })
        {
            Write(temporary.Root, file);
        }

        var games = await Source(temporary.Root, "switch").DiscoverAsync([], CancellationToken.None);
        Assert.Equal(3, games.Count);
        Assert.Contains(games, game => game.Name == "Game");
        Assert.Contains(games, game => game.Name == "Unknown");
        Assert.Contains(games, game => game.Name == "Update Quest");
    }

    [Fact]
    public async Task CollectionDetectionUsesCanonicalSystemAliasesAndIgnoresUnrecognizedFolders()
    {
        using TemporaryDirectory temporary = new();
        Write(temporary.Root, "gc/Game.rvz");
        Write(temporary.Root, "n3ds/Game.3ds");
        Write(temporary.Root, "genesis/Game.md");
        Write(temporary.Root, "pico/Game.zip");
        Write(temporary.Root, "unknown/Game.zip");
        var games = await Source(temporary.Root, "auto").DiscoverAsync([], CancellationToken.None);
        Assert.Equal(["3ds", "gamecube", "megadrive", "pico"], games.Select(game => game.Content!.SystemId).Order());
        Assert.All(games, game => Assert.Contains(Path.DirectorySeparatorChar, game.Key));
        Assert.Equal(4, games.Select(game => game.Content!.Id).Distinct().Count());
    }

    [Fact]
    public async Task CleanupKeepsDiscNumbersAndDuplicateVersionsNeedExplicitSelection()
    {
        using TemporaryDirectory temporary = new();
        foreach (var file in new[] { "Game (USA).zip", "Game (USA).7z", "Other (USA) (Disc 1).zip", "Other (USA) (Disc 2).zip" })
        {
            Write(temporary.Root, file);
        }

        var games = await Source(temporary.Root, "gb").DiscoverAsync([], CancellationToken.None);
        Assert.Contains(games, game => game.Name == "Other (Disc 1)");
        Assert.Contains(games, game => game.Name == "Other (Disc 2)");
        Assert.Contains(games, game => game.Name == "Game (USA) [ZIP]");
        Assert.Contains(games, game => game.Name == "Game (USA) [7Z]");
        var plan = ImportPlan.Build(games, [], [], "launcher.exe", ImportMode.SteamIntegration, includeUnroutable: false);
        Assert.All(plan.Where(entry => entry.Name.StartsWith("Game", StringComparison.Ordinal)), entry =>
        {
            Assert.False(entry.Preselect);
            Assert.True(entry.Selectable);
            Assert.Contains("Multiple copies", entry.Reason);
        });
        Assert.All(plan.Where(entry => entry.Name.StartsWith("Other", StringComparison.Ordinal)), entry => Assert.True(entry.Preselect));
    }

    [Fact]
    public async Task Ps2PlaylistGroupsDiscsButLaunchesTheFirstImageWithOneArgument()
    {
        using TemporaryDirectory temporary = new();
        var first = Write(temporary.Root, "Discs/John's Game (Disc 1).chd");
        Write(temporary.Root, "Discs/John's Game (Disc 2).chd");
        Write(temporary.Root, "Game.m3u", "#EXTM3U\nDiscs/John's Game (Disc 1).chd\nDiscs/John's Game (Disc 2).chd\n");
        var game = Assert.Single(await Source(temporary.Root, "ps2").DiscoverAsync([], CancellationToken.None));
        Assert.Equal(2, game.Content!.RequiredPaths.Count);
        Assert.Contains(game.Notes, note => note.Contains("Change Disc", StringComparison.Ordinal));
        Write(temporary.Root, "portable.ini");
        var launch = EmulatorStorage.Resolve(new EmulatorStore
        {
            Installations = [new EmulatorInstallation
            {
                Id = "pcsx2", DefinitionId = "pcsx2", DataPath = temporary.Root,
                ExecutablePath = Write(temporary.Root, "pcsx2.exe"), Systems = ["ps2"],
                LaunchArguments = ["--", "{rom}"]
            }]
        }, "pcsx2", "ps2", "", game.InstallPath);
        Assert.Equal(["--", first], launch.Arguments);
    }

    [Fact]
    public async Task Ps3UsesSfoTitleAndArtworkIdentityAndLeavesItsContentTreeAlone()
    {
        using TemporaryDirectory temporary = new();
        Write(temporary.Root, "dev_hdd0/game/NPEA00255/USRDIR/EBOOT.BIN");
        Write(temporary.Root, "dev_hdd0/game/NPEA00255/USRDIR/data/EBOOT.BIN");
        Sfo(temporary.GetPath("dev_hdd0/game/NPEA00255/PARAM.SFO"), "God of War™ HD", "NPEA00255");
        var game = Assert.Single(await Source(temporary.Root, "ps3").DiscoverAsync([], CancellationToken.None));
        Assert.Equal("God of War™ HD", game.Name);
        Assert.Equal(game.Name, game.ArtworkQuery!.ContentName);
        Assert.Equal(0, game.ArtworkQuery.ContentSize);
        Assert.Contains("Title ID: NPEA00255", game.Notes);
    }

    [Fact]
    public async Task InvalidOptionalSfoKeepsThePathTitleAndCompressedSwitchContentExplainsItsRefusal()
    {
        using TemporaryDirectory temporary = new();
        Write(temporary.Root, "NPEA00001/USRDIR/EBOOT.BIN");
        Write(temporary.Root, "NPEA00001/PARAM.SFO", "not an SFO");
        Assert.Equal("NPEA00001", Assert.Single(await Source(temporary.Root, "ps3").DiscoverAsync([], CancellationToken.None)).Name);
        Write(temporary.Root, "Compressed.nsz");
        var games = await Source(temporary.Root, "switch").DiscoverAsync([], CancellationToken.None);
        var entry = Assert.Single(ImportPlan.Build(games, [], [], "launcher.exe", ImportMode.SteamIntegration, includeUnroutable: false));
        Assert.False(entry.Selectable);
        Assert.False(entry.Preselect);
        Assert.Contains("Decompress", entry.Reason);
    }

    private static RomLibrarySource Source(string root, string system, string[]? exclusions = null)
    {
        return new RomLibrarySource(new RomSourceConfig
        {
            Id = "roms", Name = "Test ROMs", SystemId = system,
            Root = new ManagedContentPath { AbsolutePath = root, Directory = true }, Exclusions = [.. exclusions ?? []]
        });
    }

    private static string Write(string root, string relative, string text = "fixture")
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static void Sfo(string path, string title, string titleId)
    {
        var keys = Encoding.UTF8.GetBytes("TITLE\0TITLE_ID\0CATEGORY\0");
        var values = new[] { title, titleId, "HG" }.Select(value => Encoding.UTF8.GetBytes(value + "\0")).ToArray();
        var keyTable = 20 + 3 * 16;
        var dataTable = keyTable + keys.Length;
        var bytes = new byte[dataTable + values.Sum(value => value.Length)];
        "\0PSF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)keyTable);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)dataTable);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 3);
        keys.CopyTo(bytes, keyTable);
        var valueOffset = 0;
        for (var i = 0; i < 3; i++)
        {
            var entry = bytes.AsSpan(20 + i * 16);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)new[] { 0, 6, 15 }[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], 0x0204);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)values[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)valueOffset);
            values[i].CopyTo(bytes, dataTable + valueOffset);
            valueOffset += values[i].Length;
        }

        File.WriteAllBytes(path, bytes);
    }
}
