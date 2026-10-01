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

    private static BattleNetLibrarySource Source(params string[] files)
    {
        HashSet<string> existing = new(files, StringComparer.OrdinalIgnoreCase) { ClientExe };
        return new BattleNetLibrarySource(existing.Contains, _ => true, () => null);
    }

    private static UninstallEntry Classic(string name, string folder)
    {
        return new UninstallEntry(
            name, name, folder, "Blizzard Entertainment", $@"{folder}\Uninstall.exe", string.Empty);
    }

    [Fact]
    public async Task AModernGameLaunchesThroughBattleNetByItsProductCode()
    {
        UninstallEntry[] programs = [Client, Game("Diablo III", "diablo3_enus", @"D:\Games\Diablo III")];

        var game = Assert.Single(await Source().DiscoverAsync(programs, CancellationToken.None));

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
        UninstallEntry[] programs = [Client, Game("Warcraft: Remastered", "w1r_enus", @"D:\Games\W1R")];

        Assert.Equal("W1R", Assert.Single(await Source().DiscoverAsync(programs, CancellationToken.None)).Key);
    }

    [Fact]
    public async Task TestAndBetaInstallsAndUnknownIdsAreSkipped()
    {
        UninstallEntry[] programs =
        [
            Client,
            Game("Overwatch Test", "prometheus_test", @"D:\Games\OWTest"),
            Game("Something Beta", "wow_beta", @"D:\Games\WoWBeta"),
            Game("Unknown", "mystery", @"D:\Games\Mystery")
        ];

        Assert.Empty(await Source().DiscoverAsync(programs, CancellationToken.None));
    }

    [Fact]
    public async Task AGameInstalledTwiceIsOfferedOnce()
    {
        UninstallEntry[] programs =
        [
            Client,
            Game("World of Warcraft", "wow", @"D:\Games\WoW"),
            Game("World of Warcraft", "wow_enus", @"E:\Games\WoW")
        ];

        var game = Assert.Single(await Source().DiscoverAsync(programs, CancellationToken.None));
        Assert.Equal("WoW", game.Key);
        Assert.Equal(@"D:\Games\WoW", game.InstallPath);
    }

    [Fact]
    public async Task AnotherProductSharingTheIdPrefixIsNotTakenForIt()
    {
        // Launching Classic by retail's code would start the wrong game, so a variant the table does
        // not know is skipped instead.
        UninstallEntry[] programs =
            [Client, Game("World of Warcraft Classic", "wow_classic", @"D:\Games\WoWClassic")];

        Assert.Empty(await Source().DiscoverAsync(programs, CancellationToken.None));
    }

    [Theory]
    [InlineData("wow", "WoW")]
    [InlineData("wow_enus", "WoW")]
    [InlineData("fenris", "Fen")]
    [InlineData("Fen", "Fen")]
    [InlineData("w1r_dede", "W1R")]
    [InlineData("w1", "W1")]
    [InlineData("wow_classic", null)]
    [InlineData("wow_classic_era", null)]
    [InlineData("diablo3_ptr", null)]
    public void AnIdMatchesItsProductNotOneSharingItsPrefix(string uid, string? code)
    {
        Assert.Equal(code, BattleNetLibrarySource.ProductCode(uid));
    }

    [Fact]
    public async Task AGameOnlyTheAgentRecordsIsFoundByTheSameRule()
    {
        // Diablo IV's agent uid is fenris, which the table knows by the prefix Fen.
        var database = BattleNetProductDatabaseTests.Database(("fenris", "fen", "D:/Games/Diablo IV"));
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase) { ClientExe };
        var source = new BattleNetLibrarySource(existing.Contains, _ => true, () => database);

        var game = Assert.Single(await source.DiscoverAsync([Client], CancellationToken.None));

        Assert.Equal("Fen", game.Key);
        Assert.Equal(@"D:\Games\Diablo IV", game.InstallPath);
        Assert.Equal("--exec=\"launch Fen\"", Assert.Single(game.CommandRoutes).LaunchOptions);
    }

    [Fact]
    public async Task DiabloIIAloneIsNotAlsoOfferedAsLordOfDestruction()
    {
        var source = Source(@"C:\Games\Diablo II\Diablo II.exe");
        UninstallEntry[] programs = [Client, Classic("Diablo II", @"C:\Games\Diablo II")];

        Assert.Equal("D2", Assert.Single(await source.DiscoverAsync(programs, CancellationToken.None)).Key);
    }

    [Fact]
    public async Task LordOfDestructionIsOfferedWhenItsDataIsInstalled()
    {
        var source = Source(@"C:\Games\Diablo II\Diablo II.exe", @"C:\Games\Diablo II\d2exp.mpq");
        UninstallEntry[] programs = [Client, Classic("Diablo II", @"C:\Games\Diablo II")];

        Assert.Equal(
            ["D2", "D2X"], (await source.DiscoverAsync(programs, CancellationToken.None)).Select(game => game.Key));
    }

    [Fact]
    public async Task AClassicGameIsFoundWithoutTheClient()
    {
        var classic = Classic("Warcraft III", @"C:\Games\Warcraft III");
        var source = new BattleNetLibrarySource(
            path => path == @"C:\Games\Warcraft III\Warcraft III.exe", _ => true, () => null);

        Assert.Equal(new SourceAvailability(true, "Games only"), source.Detect([classic]));
        Assert.Equal("W3C", Assert.Single(await source.DiscoverAsync([classic], CancellationToken.None)).Key);
    }

    [Fact]
    public async Task AClassicGameStartsFromItsOwnExecutable()
    {
        var classic = Classic("Warcraft III", @"C:\Games\Warcraft III");
        var source = Source(@"C:\Games\Warcraft III\Frozen Throne.exe");

        var game = Assert.Single(await source.DiscoverAsync([Client, classic], CancellationToken.None));

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
        UninstallEntry[] programs = [Game("Diablo III", "diablo3_enus", @"D:\Games\Diablo III")];
        var source = new BattleNetLibrarySource(_ => false, _ => true, () => null);

        Assert.False(source.Detect(programs).Installed);
        Assert.Empty(await source.DiscoverAsync(programs, CancellationToken.None));
    }

    [Fact]
    public void TheClientIsFoundByItsUninstallEntry()
    {
        Assert.True(Source().Detect([Client]).Installed);
    }
}
