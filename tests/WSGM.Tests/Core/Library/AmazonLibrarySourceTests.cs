using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Amazon Games discovery from its install table and each game's fuel.json.</summary>
public sealed class AmazonLibrarySourceTests
{
    private const string LocalAppData = @"C:\Users\Player\AppData\Local";
    private const string ClientExe = @"C:\Users\Player\AppData\Local\Amazon Games\App\Amazon Games.exe";

    private const string Database =
        @"C:\Users\Player\AppData\Local\Amazon Games\Data\Games\Sql\GameInstallInfo.sqlite";

    private const string GameFolder = @"C:\Amazon Games\Library\Moonlit";

    private static readonly ProtocolCommand Protocol = new(ClientExe, "\"amazon-games://play/amzn1.adg.product.1\"");

    private static AmazonLibrarySource Source(
        string? fuel,
        ProtocolCommand? protocol = null,
        IReadOnlyList<UninstallEntry>? uninstall = null,
        params string[] files)
    {
        HashSet<string> existing = new(files, StringComparer.OrdinalIgnoreCase) { ClientExe, Database };
        return new AmazonLibrarySource(
            LocalAppData,
            () => uninstall ?? [],
            _ => [new AmazonInstall("amzn1.adg.product.1", "Moonlit", "C:/Amazon Games/Library/Moonlit")],
            path => path == GameFolder + @"\fuel.json" ? fuel : null,
            existing.Contains,
            folder => folder == GameFolder,
            _ => protocol);
    }

    [Fact]
    public async Task AGameThatNeedsNoSignInStartsDirectlyWithTheLauncherAsTheAlternative()
    {
        const string fuel = """
                            {
                              // Amazon ships comments and trailing commas.
                              "SchemaVersion": "2",
                              "Main": {
                                "Command": "bin/Moonlit.exe",
                                "Args": ["-windowed", "--profile=My Saves"],
                                "WorkingSubdirOverride": "bin",
                              },
                            }
                            """;
        var source = Source(fuel, Protocol, files: [GameFolder + @"\bin\Moonlit.exe"]);

        var game = Assert.Single(await source.DiscoverAsync(CancellationToken.None));

        Assert.Equal("amazon", game.SourceId);
        Assert.Equal("amzn1.adg.product.1", game.Key);
        Assert.Equal(GameFolder, game.InstallPath);
        Assert.Equal(["direct", "launcher"], game.CommandRoutes.Select(route => route.Id));
        var direct = game.CommandRoutes[0];
        Assert.Equal(GameFolder + @"\bin\Moonlit.exe", direct.Target);
        Assert.Equal(GameFolder + @"\bin", direct.StartDirectory);
        Assert.Equal("-windowed \"--profile=My Saves\"", direct.LaunchOptions);
        Assert.Equal(direct.Label, game.Launch.Label);
        var launcher = game.CommandRoutes[1];
        Assert.Equal(ClientExe, launcher.Target);
        Assert.Equal(Protocol.Arguments, launcher.LaunchOptions);
    }

    [Fact]
    public async Task AGameThatAsksForASignInGoesThroughTheLauncherFirst()
    {
        const string fuel = """
                            {"Main": {"Command": "Moonlit.exe", "ClientId": "abc", "AuthScopes": ["scope"]}}
                            """;
        var source = Source(fuel, Protocol, files: [GameFolder + @"\Moonlit.exe"]);

        var game = Assert.Single(await source.DiscoverAsync(CancellationToken.None));

        Assert.Equal(["launcher", "direct"], game.CommandRoutes.Select(route => route.Id));
        Assert.Equal(GameFolder, game.CommandRoutes[1].StartDirectory);
    }

    [Fact]
    public async Task WithoutFuelTheLauncherIsTheOnlyRoute()
    {
        var game = Assert.Single(await Source(null, Protocol).DiscoverAsync(CancellationToken.None));

        Assert.Equal("launcher", Assert.Single(game.CommandRoutes).Id);
    }

    [Fact]
    public async Task AGameWithNoComposableRouteIsSkipped()
    {
        Assert.Empty(await Source(null).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ADirectRouteNeedsItsExecutableOnDisk()
    {
        const string fuel = """{"Main": {"Command": "Missing.exe"}}""";

        Assert.Empty(await Source(fuel).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public void TheRegisteredClientIsFoundByItsUninstallEntry()
    {
        var entry = new UninstallEntry(
            "Amazon Games", "Amazon Games", @"D:\Amazon Games\App", "Amazon.com Services LLC",
            "\"D:\\Amazon Games\\App\\Uninstall Amazon Games.exe\"", string.Empty);
        var source = new AmazonLibrarySource(
            @"C:\Nowhere", () => [entry], _ => [], _ => null,
            path => path == @"D:\Amazon Games\App\Amazon Games.exe", _ => false, _ => null);

        Assert.True(source.Detect().Installed);
    }

    [Fact]
    public async Task WithoutTheClientNothingIsFound()
    {
        var source = new AmazonLibrarySource(
            LocalAppData, () => [], _ => throw new InvalidOperationException("Must not be read."), _ => null,
            _ => false, _ => false, _ => null);

        Assert.False(source.Detect().Installed);
        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public void AFuelFileThatDoesNotParseIsNoFuelFile()
    {
        Assert.Null(AmazonLibrarySource.ParseFuel("not json"));
        Assert.Null(AmazonLibrarySource.ParseFuel("{\"SchemaVersion\": \"2\"}"));
    }

    [Fact]
    public async Task ArgumentsArriveAsTheyWereWrittenEvenEndingInASeparatorOrHoldingAQuote()
    {
        const string fuel = """
                            {"Main": {"Command": "Moonlit.exe",
                              "Args": ["--path=C:\\My Games\\", "--name=say \"hi\"", "--plain"]}}
                            """;
        var source = Source(fuel, files: [GameFolder + @"\Moonlit.exe"]);

        var route = Assert.Single(Assert.Single(await source.DiscoverAsync(CancellationToken.None)).CommandRoutes);

        Assert.Equal(@"""--path=C:\My Games\\"" ""--name=say \""hi\"""" --plain", route.LaunchOptions);
    }

    [Fact]
    public async Task AnUnreadableInstallTableFailsTheScanRatherThanListingNothing()
    {
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase) { ClientExe, Database };
        var source = new AmazonLibrarySource(
            LocalAppData, () => [],
            _ => throw new LauncherDatabaseException("GameInstallInfo.sqlite could not be read.", new IOException()),
            _ => null, existing.Contains, _ => true, _ => null);

        await Assert.ThrowsAsync<LauncherDatabaseException>(() => source.DiscoverAsync(CancellationToken.None));
    }
}
