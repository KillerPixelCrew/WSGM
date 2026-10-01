using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>GOG discovery over in-memory uninstall entries and <c>goggame-*.info</c> files.</summary>
public sealed class GogLibrarySourceTests
{
    private const string Galaxy = @"C:\GOG Galaxy";
    private const string Install = @"D:\GOG\Moonlit";

    private const string PrimaryTask =
        """
        {
          "gameId": "1207658924",
          "rootGameId": "1207658924",
          "playTasks": [
            { "isPrimary": false, "type": "FileTask", "path": "Tools\\Config.exe" },
            { "isPrimary": true, "type": "FileTask", "path": "bin/x64/Moonlit.exe", "workingDir": "bin/x64",
              "arguments": "-skipintro" }
          ]
        }
        """;

    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase) { Install };

    private readonly HashSet<string> _executables = new(StringComparer.OrdinalIgnoreCase)
    {
        $@"{Galaxy}\GalaxyClient.exe"
    };

    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<UninstallEntry> _uninstall =
    [
        new("1207658924_is1", "Moonlit", Install, "GOG.com", "", "")
    ];

    private string? _galaxyPath = Galaxy;

    private GogLibrarySource Source()
    {
        return new GogLibrarySource(
            () => _galaxyPath,
            path => _files.GetValueOrDefault(path),
            path => _executables.Contains(path),
            path => _directories.Contains(path));
    }

    private void AddInfo(string id, string json)
    {
        _files[$@"{Install}\goggame-{id}.info"] = json;
    }

    [Fact]
    public async Task AGameStartsDirectlyFirstAndThroughGalaxySecond()
    {
        AddInfo("1207658924", PrimaryTask);
        _executables.Add($@"{Install}\bin\x64\Moonlit.exe");

        var game = Assert.Single(await Source().DiscoverAsync(_uninstall, CancellationToken.None));

        Assert.Equal("gog", game.SourceId);
        Assert.Equal("1207658924", game.Key);
        Assert.Equal("Moonlit", game.Name);
        Assert.Equal(["direct", "launcher"], game.CommandRoutes.Select(route => route.Id));
        var direct = game.CommandRoutes[0];
        Assert.Equal($@"{Install}\bin\x64\Moonlit.exe", direct.Target);
        Assert.Equal($@"{Install}\bin\x64", direct.StartDirectory);
        Assert.Equal("-skipintro", direct.LaunchOptions);
        var launcher = game.CommandRoutes[1];
        Assert.Equal($@"{Galaxy}\GalaxyClient.exe", launcher.Target);
        Assert.Equal(Galaxy, launcher.StartDirectory);
        Assert.Equal(
            $"/launchViaAutostart /gameId=1207658924 /command=runGame /path=\"{Install}\"", launcher.LaunchOptions);
        Assert.Equal(direct.Label, game.Launch.Label);
    }

    [Fact]
    public async Task WithoutGalaxyOnlyTheDirectRouteIsOffered()
    {
        _galaxyPath = null;
        AddInfo("1207658924", PrimaryTask);
        _executables.Add($@"{Install}\bin\x64\Moonlit.exe");

        var source = Source();
        var game = Assert.Single(await source.DiscoverAsync(_uninstall, CancellationToken.None));

        Assert.Equal(new SourceAvailability(true, "Games only"), source.Detect(_uninstall));
        Assert.Equal("direct", Assert.Single(game.CommandRoutes).Id);
    }

    [Fact]
    public async Task AMissingExecutableLeavesOnlyGalaxy()
    {
        AddInfo("1207658924", PrimaryTask);

        var game = Assert.Single(await Source().DiscoverAsync(_uninstall, CancellationToken.None));

        Assert.Equal("launcher", Assert.Single(game.CommandRoutes).Id);
    }

    [Fact]
    public async Task ATitleWithoutAPrimaryTaskIsDlc()
    {
        AddInfo("1207658924", """{ "playTasks": [ { "isPrimary": false, "path": "x.exe" } ] }""");

        Assert.Empty(await Source().DiscoverAsync(_uninstall, CancellationToken.None));
    }

    [Fact]
    public async Task ATitleWhoseRootIsAnotherGameIsDlc()
    {
        AddInfo("1207658924", PrimaryTask.Replace(
            "\"rootGameId\": \"1207658924\"", "\"rootGameId\": \"1\"", StringComparison.Ordinal));
        _executables.Add($@"{Install}\bin\x64\Moonlit.exe");

        Assert.Empty(await Source().DiscoverAsync(_uninstall, CancellationToken.None));
    }

    [Fact]
    public async Task ATitleWithoutAnInfoFileIsSkipped()
    {
        Assert.Empty(await Source().DiscoverAsync(_uninstall, CancellationToken.None));
    }

    [Theory]
    [InlineData("1207658924_is1", "GOG.com", "Moonlit", true)]
    [InlineData("1207658924_is1", "Other", "Moonlit", false)]
    [InlineData("Moonlit_is1", "GOG.com", "Moonlit", false)]
    [InlineData("1207658924_is1_old", "GOG.com", "Moonlit", false)]
    [InlineData("1207658924_is1", "GOG.com", "GOGPACK Bundle", false)]
    public void OnlyGogInstallerEntriesAreGames(string keyName, string publisher, string name, bool expected)
    {
        var entry = new UninstallEntry(keyName, name, Install, publisher, "", "");

        Assert.Equal(expected, GogLibrarySource.GameId(entry) is not null);
    }

    [Fact]
    public void NothingIsDetectedWithoutGalaxyOrGames()
    {
        _galaxyPath = null;
        _uninstall.Clear();

        Assert.Equal(SourceAvailability.NotFound, Source().Detect(_uninstall));
    }

    [Fact]
    public void GalaxyIsDetectedByItsClient()
    {
        _uninstall.Clear();

        Assert.Equal(new SourceAvailability(true, "Installed"), Source().Detect(_uninstall));
    }

    [Fact]
    public async Task AGameInstalledAtADriveRootKeepsTheRootAndItsQuote()
    {
        // "E:" alone is the current folder on E:, and a bare trailing separator would escape the quote
        // that closes Galaxy's /path argument.
        _uninstall[0] = _uninstall[0] with { InstallLocation = @"E:\" };
        _directories.Add(@"E:\");
        _files[@"E:\goggame-1207658924.info"] = PrimaryTask;

        var game = Assert.Single(await Source().DiscoverAsync(_uninstall, CancellationToken.None));

        Assert.Equal(@"E:\", game.InstallPath);
        Assert.Equal(
            @"/launchViaAutostart /gameId=1207658924 /command=runGame /path=""E:\\""",
            Assert.Single(game.CommandRoutes).LaunchOptions);
    }
}
