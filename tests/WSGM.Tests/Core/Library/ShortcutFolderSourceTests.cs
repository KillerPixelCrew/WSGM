using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Tests.Core;

/// <summary>What a shortcuts folder offers, and what it leaves out.</summary>
public sealed class ShortcutFolderSourceTests
{
    private const string Folder = @"C:\Games\Shortcuts";
    private const string Epic = @"C:\Epic\Launcher\EpicGamesLauncher.exe";

    private static readonly Dictionary<string, ShellLinkInfo> Links = new(StringComparer.OrdinalIgnoreCase)
    {
        [$@"{Folder}\Moonlit.lnk"] = new ShellLinkInfo(@"C:\Games\Moonlit\moonlit.exe", " -windowed ", ""),
        [$@"{Folder}\Worked In.lnk"] = new ShellLinkInfo(@"C:\Games\Other\other.exe", "", @"C:\Games\Other\bin"),
        [$@"{Folder}\Broken.lnk"] = new ShellLinkInfo(@"C:\Games\Gone\gone.exe", "", ""),
        [$@"{Folder}\Steam Game.lnk"] =
            new ShellLinkInfo(@"C:\Steam\steam.exe", "steam://rungameid/10", @"C:\Steam"),
        [$@"{Folder}\Manual.lnk"] = new ShellLinkInfo(@"C:\Games\Moonlit\manual.pdf", "", "")
    };

    private static LibraryFakeDisk Disk()
    {
        return new LibraryFakeDisk()
            .With(@"C:\Games\Moonlit\moonlit.exe")
            .With(@"C:\Games\Moonlit\manual.pdf")
            .With(@"C:\Games\Other\other.exe")
            .With(@"C:\Steam\steam.exe")
            .With(Epic)
            .With($@"{Folder}\Moonlit.lnk")
            .With($@"{Folder}\Worked In.lnk")
            .With($@"{Folder}\Broken.lnk")
            .With($@"{Folder}\Steam Game.lnk")
            .With($@"{Folder}\Manual.lnk")
            .With($@"{Folder}\Unreadable.lnk")
            .With($@"{Folder}\Fortnite.url",
                "[InternetShortcut]\r\nURL=com.epicgames.launcher://apps/Fortnite?action=launch&silent=true\r\n")
            .With($@"{Folder}\Portal.url", "[InternetShortcut]\r\nURL=steam://rungameid/400\r\n")
            .With($@"{Folder}\Website.url", "[InternetShortcut]\r\nURL=https://example.com/\r\n")
            .With($@"{Folder}\Unknown.url", "[InternetShortcut]\r\nURL=nobody://game\r\n")
            .With($@"{Folder}\Local.url", "[InternetShortcut]\r\nURL=file:///C:/Games/Other/other.exe\r\n")
            .With($@"{Folder}\notes.txt")
            .With($@"{Folder}\unins000.exe")
            .With($@"{Folder}\Portable\Tool.exe")
            .With($@"{Folder}\Portable\vc_redist.x64.exe")
            .With($@"{Folder}\Hidden\Secret.exe")
            .Mark($@"{Folder}\Hidden", FileAttributes.Directory | FileAttributes.Hidden);
    }

    private static ShortcutFolderSource Source(LibraryFakeDisk disk, ShortcutFolderConfig? folder = null)
    {
        folder ??= new ShortcutFolderConfig { Id = "folder:abc", Path = Folder };
        return new ShortcutFolderSource(
            folder, disk.DirectoryExists, disk.FileExists, disk.List, path => Links.GetValueOrDefault(path),
            disk.ReadText, Resolve);
    }

    private static ProtocolCommand? Resolve(string uri)
    {
        return uri.StartsWith("com.epicgames.launcher:", StringComparison.OrdinalIgnoreCase)
            ? new ProtocolCommand(Epic, $"\"{uri}\"")
            : null;
    }

    [Fact]
    public async Task OnlyEntriesThatCanBecomeAShortcutAreOffered()
    {
        var games = await Source(Disk()).DiscoverAsync(CancellationToken.None);

        Assert.Equal(
            ["Fortnite.url", "Local.url", "Moonlit.lnk", @"Portable\Tool.exe", "Worked In.lnk"],
            games.Select(game => game.Key).Order(StringComparer.Ordinal));
        Assert.All(games, game => Assert.Equal("folder:abc", game.SourceId));
    }

    [Fact]
    public async Task AShortcutRunsItsTargetWithItsArgumentsAndWorkingFolder()
    {
        var games = (await Source(Disk()).DiscoverAsync(CancellationToken.None)).ToDictionary(game => game.Key);

        var moonlit = Assert.Single(games["Moonlit.lnk"].CommandRoutes);
        Assert.Equal("Moonlit", games["Moonlit.lnk"].Name);
        Assert.Equal("direct", moonlit.Id);
        Assert.Equal(@"C:\Games\Moonlit\moonlit.exe", moonlit.Target);
        Assert.Equal(@"C:\Games\Moonlit", moonlit.StartDirectory);
        Assert.Equal("-windowed", moonlit.LaunchOptions);

        Assert.Equal(@"C:\Games\Other\bin", games["Worked In.lnk"].CommandRoutes[0].StartDirectory);

        var tool = Assert.Single(games[@"Portable\Tool.exe"].CommandRoutes);
        Assert.Equal($@"{Folder}\Portable\Tool.exe", tool.Target);
        Assert.Equal($@"{Folder}\Portable", tool.StartDirectory);
        Assert.Empty(tool.LaunchOptions);

        Assert.Equal(@"C:\Games\Other\other.exe", games["Local.url"].CommandRoutes[0].Target);
    }

    [Fact]
    public async Task ALauncherLinkRunsTheProgramItsSchemeIsRegisteredTo()
    {
        var games = await Source(Disk()).DiscoverAsync(CancellationToken.None);

        var fortnite = games.Single(game => game.Key == "Fortnite.url");
        var route = Assert.Single(fortnite.CommandRoutes);
        Assert.Equal("launcher", route.Id);
        Assert.Equal("Through EpicGamesLauncher", route.Label);
        Assert.Equal("Through EpicGamesLauncher", fortnite.Launch.Label);
        Assert.Equal(Epic, route.Target);
        Assert.Equal("\"com.epicgames.launcher://apps/Fortnite?action=launch&silent=true\"", route.LaunchOptions);
    }

    [Fact]
    public async Task SubfoldersAreLeftAloneWhenTheFolderSaysSo()
    {
        var folder = new ShortcutFolderConfig { Id = "folder:abc", Path = Folder, IncludeSubfolders = false };

        var games = await Source(Disk(), folder).DiscoverAsync(CancellationToken.None);

        Assert.DoesNotContain(games, game => game.Key.Contains('\\'));
        Assert.Contains(games, game => game.Key == "Moonlit.lnk");
    }

    [Fact]
    public async Task OnlyTheConfiguredFileTypesAreRead()
    {
        var folder = new ShortcutFolderConfig { Id = "folder:abc", Path = Folder, Extensions = [".exe"] };

        var games = await Source(Disk(), folder).DiscoverAsync(CancellationToken.None);

        Assert.Equal(@"Portable\Tool.exe", Assert.Single(games).Key);
    }

    [Fact]
    public async Task AMissingFolderIsReportedAndOffersNothing()
    {
        var folder = new ShortcutFolderConfig { Id = "folder:gone", Path = @"D:\Nowhere\Shortcuts" };
        var source = Source(Disk(), folder);

        Assert.Equal(new SourceAvailability(false, "Folder missing"), source.Detect());
        Assert.Equal("Shortcuts", source.DisplayName);
        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(@"C:\Steam\steam.exe", true)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Portal 2\portal2.exe", true)]
    [InlineData(@"D:\SteamLibrary\SteamApps\common\Game\game.exe", true)]
    [InlineData(@"D:\Games\steamapps-backup\game.exe", false)]
    [InlineData(@"D:\Games\Moonlit\moonlit.exe", false)]
    public void AnythingSteamAlreadyRunsIsRecognised(string program, bool expected)
    {
        Assert.Equal(expected, ShortcutFolderSource.IsSteam(program));
    }

    [Fact]
    public async Task ALinkIntoASteamLibraryIsNotOffered()
    {
        const string portal = @"D:\SteamLibrary\steamapps\common\Portal 2\portal2.exe";
        var disk = new LibraryFakeDisk().With(portal).With($@"{Folder}\Portal 2.lnk");
        var source = new ShortcutFolderSource(
            new ShortcutFolderConfig { Id = "folder:abc", Path = Folder }, disk.DirectoryExists, disk.FileExists,
            disk.List, _ => new ShellLinkInfo(portal, "", ""), disk.ReadText, Resolve);

        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AGameFarDownAFolderTreeIsStillOffered()
    {
        // No depth, file or folder count stops the scan: whatever the folder holds is offered.
        var relative = string.Join('\\', Enumerable.Range(0, 12).Select(index => $"level{index}")) + @"\Deep.exe";
        var disk = Disk().With($@"{Folder}\{relative}");

        var found = await Source(disk).DiscoverAsync(CancellationToken.None);

        Assert.Contains(found, game => game.Key == relative);
    }

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("Uninstall Moonlit.lnk", true)]
    [InlineData("DXSETUP.exe", true)]
    [InlineData("UnityCrashHandler64.exe", true)]
    [InlineData("Moonlit.exe", false)]
    public void InstallersAndHelpersAreNeverGames(string fileName, bool expected)
    {
        Assert.Equal(expected, ShortcutFolderSource.IsTool(fileName));
    }
}
