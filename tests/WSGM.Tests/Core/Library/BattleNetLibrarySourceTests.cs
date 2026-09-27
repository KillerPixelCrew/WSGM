using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Battle.net discovery from uninstall entries, as Playnite reads them.</summary>
public sealed class BattleNetLibrarySourceTests
{
    private const string ClientFolder = @"C:\Program Files (x86)\Battle.net";
    private const string ClientExe = @"C:\Program Files (x86)\Battle.net\Battle.net.exe";

    private static readonly UninstallEntry Client = new(
        "Battle.net",
        "Battle.net",
        ClientFolder,
        "Blizzard Entertainment",
        "\"C:\\Program Files (x86)\\Battle.net\\Battle.net Launcher.exe\" --lang=enUS --uid=battle.net --displayname=\"Battle.net\"",
        string.Empty);

    private static UninstallEntry Game(string displayName, string uid, string folder)
    {
        return new UninstallEntry(
            displayName,
            displayName,
            folder,
            "Blizzard Entertainment",
            $"\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid={uid} --displayname=\"{displayName}\"",
            string.Empty);
    }

    private static BattleNetLibrarySource Source(
        IReadOnlyList<UninstallEntry> entries, params string[] files)
    {
        HashSet<string> existing = new(files, StringComparer.OrdinalIgnoreCase) { ClientExe };
        return new BattleNetLibrarySource(() => entries, existing.Contains, _ => true);
    }

    [Fact]
    public async Task AModernGameLaunchesThroughBattleNetByItsProductCode()
    {
        var source = Source([Client, Game("Diablo III", "diablo3_enus", @"D:\Games\Diablo III")]);

        var game = Assert.Single(await source.DiscoverAsync(CancellationToken.None));

        Assert.Equal("battlenet", game.SourceId);
        Assert.Equal("D3", game.Key);
        Assert.Equal("Diablo III", game.Name);
        Assert.True(game.IsGame);
        Assert.True(game.Launch.Validated);
        var route = Assert.Single(game.CommandRoutes);
        Assert.Equal("launcher", route.Id);
        Assert.Equal(ClientExe, route.Target);
        Assert.Equal(ClientFolder, route.StartDirectory);
        Assert.Equal("--exec=\"launch D3\"", route.LaunchOptions);
    }

    [Fact]
    public async Task TheFirstMatchingPrefixDecidesTheProduct()
    {
        // w1r must not be read as w1, and a regional suffix on the uid is ignored.
        var source = Source([Client, Game("Warcraft: Remastered", "w1r_enus", @"D:\Games\W1R")]);

        Assert.Equal("W1R", Assert.Single(await source.DiscoverAsync(CancellationToken.None)).Key);
    }

    [Fact]
    public async Task TestAndBetaInstallsAndUnknownIdsAreSkipped()
    {
        var source = Source(
        [
            Client,
            Game("Overwatch Test", "prometheus_test", @"D:\Games\OWTest"),
            Game("Something Beta", "wow_beta", @"D:\Games\WoWBeta"),
            Game("Unknown", "mystery", @"D:\Games\Mystery")
        ]);

        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AGameInstalledTwiceIsOfferedOnce()
    {
        var source = Source(
        [
            Client,
            Game("World of Warcraft", "wow", @"D:\Games\WoW"),
            Game("World of Warcraft Classic", "wow_classic", @"D:\Games\WoWClassic")
        ]);

        Assert.Equal("WoW", Assert.Single(await source.DiscoverAsync(CancellationToken.None)).Key);
    }

    [Fact]
    public async Task AClassicGameStartsFromItsOwnExecutable()
    {
        var classic = new UninstallEntry(
            "Warcraft III", "Warcraft III", @"C:\Games\Warcraft III", "Blizzard Entertainment",
            @"C:\Games\Warcraft III\Uninstall.exe", string.Empty);
        var source = Source([Client, classic], @"C:\Games\Warcraft III\Frozen Throne.exe");

        var game = Assert.Single(await source.DiscoverAsync(CancellationToken.None));

        Assert.Equal("W3CX", game.Key);
        var route = Assert.Single(game.CommandRoutes);
        Assert.Equal("direct", route.Id);
        Assert.Equal(@"C:\Games\Warcraft III\Frozen Throne.exe", route.Target);
        Assert.Equal(@"C:\Games\Warcraft III", route.StartDirectory);
        Assert.Equal(string.Empty, route.LaunchOptions);
    }

    [Fact]
    public async Task WithoutTheClientNothingIsFound()
    {
        var entries = new[] { Game("Diablo III", "diablo3_enus", @"D:\Games\Diablo III") };
        var source = new BattleNetLibrarySource(() => entries, _ => false, _ => true);

        Assert.False(source.Detect().Installed);
        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public void TheClientIsFoundByItsUninstallEntry()
    {
        Assert.True(Source([Client]).Detect().Installed);
    }
}
