using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Epic discovery over in-memory manifests, uninstall entries and protocol registrations.</summary>
public sealed class EpicLibrarySourceTests
{
    private const string ProgramData = @"C:\ProgramData";
    private const string Manifests = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
    private const string InstalledList = @"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat";
    private const string LauncherProgram = @"C:\Epic\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe";

    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase) { Manifests };
    private readonly HashSet<string> _executables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UninstallEntry> _uninstall = [];
    private bool _protocolRegistered = true;

    private EpicLibrarySource Source()
    {
        return new EpicLibrarySource(
            () => _uninstall,
            ProgramData,
            directory => _files.Keys
                .Where(path => string.Equals(Path.GetDirectoryName(path), directory,
                                   StringComparison.OrdinalIgnoreCase)
                               && path.EndsWith(".item", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            path => _files.GetValueOrDefault(path),
            path => _executables.Contains(path),
            path => _directories.Contains(path),
            uri => _protocolRegistered ? new ProtocolCommand(LauncherProgram, uri) : null);
    }

    private void AddManifest(
        string appName,
        string installLocation = @"D:\Games\Moonlit",
        string categories = "\"public\", \"games\", \"applications\"",
        string compatibleApps = "",
        string technicalType = "games",
        string launchExecutable = "Binaries/Moonlit.exe")
    {
        _directories.Add(installLocation);
        _files[$@"{Manifests}\{appName}.item"] =
            $$"""
              {
                "AppName": "{{appName}}",
                "DisplayName": "{{appName}} Game",
                "InstallLocation": "{{installLocation.Replace(@"\", @"\\", StringComparison.Ordinal)}}",
                "LaunchExecutable": "{{launchExecutable}}",
                "LaunchCommand": " -nolauncher ",
                "CatalogNamespace": "ns",
                "CatalogItemId": "item",
                "AppCategories": [{{categories}}],
                "CompatibleApps": [{{compatibleApps}}],
                "TechnicalType": "{{technicalType}}"
              }
              """;
    }

    [Fact]
    public async Task AGameOffersTheLauncherFirstAndItsOwnExecutableSecond()
    {
        AddManifest("Moonlit");
        _executables.Add(@"D:\Games\Moonlit\Binaries\Moonlit.exe");

        var game = Assert.Single(await Source().DiscoverAsync(CancellationToken.None));

        Assert.Equal("epic", game.SourceId);
        Assert.Equal("Moonlit", game.Key);
        Assert.Equal("Moonlit Game", game.Name);
        Assert.True(game.IsGame);
        Assert.Equal(["launcher", "direct"], game.CommandRoutes.Select(route => route.Id));
        var launcher = game.CommandRoutes[0];
        Assert.Equal(LauncherProgram, launcher.Target);
        Assert.Equal(
            "com.epicgames.launcher://apps/ns%3Aitem%3AMoonlit?action=launch&silent=true", launcher.LaunchOptions);
        var direct = game.CommandRoutes[1];
        Assert.Equal(@"D:\Games\Moonlit\Binaries\Moonlit.exe", direct.Target);
        Assert.Equal(@"D:\Games\Moonlit", direct.StartDirectory);
        Assert.Equal("-nolauncher", direct.LaunchOptions);
        Assert.Equal(launcher.Label, game.Launch.Label);
        Assert.True(game.Launch.Validated);
    }

    [Fact]
    public async Task WithoutAProtocolRegistrationTheExecutableIsTheDefault()
    {
        _protocolRegistered = false;
        AddManifest("Moonlit");
        _executables.Add(@"D:\Games\Moonlit\Binaries\Moonlit.exe");

        var game = Assert.Single(await Source().DiscoverAsync(CancellationToken.None));

        Assert.Equal("direct", Assert.Single(game.CommandRoutes).Id);
    }

    [Fact]
    public async Task AGameWithNoUsableRouteIsSkipped()
    {
        _protocolRegistered = false;
        AddManifest("Moonlit");

        Assert.Empty(await Source().DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AddOnsAndEngineComponentsAreNotListed()
    {
        AddManifest("Dlc", @"D:\Games\Dlc", "\"addons\"");
        AddManifest("Plugin", @"D:\Games\Plugin", "\"plugins\"");
        AddManifest("Compatible", @"D:\Games\Compatible", compatibleApps: "\"UE_5.3\"");
        AddManifest("Engine", @"D:\Games\Engine", technicalType: "plugins/engine");
        AddManifest("Launchable", @"D:\Games\Launchable", "\"addons\", \"addons/launchable\"");

        var game = Assert.Single(await Source().DiscoverAsync(CancellationToken.None));

        Assert.Equal("Launchable", game.Key);
    }

    [Fact]
    public async Task AMovedGameIsFoundThroughTheLaunchersInstalledList()
    {
        AddManifest("Moonlit", @"C:\Old\Moonlit");
        _directories.Remove(@"C:\Old\Moonlit");
        _directories.Add(@"E:\Moonlit");
        _files[InstalledList] =
            """{ "InstallationList": [ { "AppName": "Moonlit", "InstallLocation": "E:/Moonlit" } ] }""";

        var game = Assert.Single(await Source().DiscoverAsync(CancellationToken.None));

        Assert.Equal(@"E:\Moonlit", game.InstallPath);
    }

    [Fact]
    public async Task AManifestThatIsNotJsonIsSkipped()
    {
        _files[$@"{Manifests}\Broken.item"] = "{ \"AppName\": ";
        AddManifest("Moonlit");

        Assert.Equal("Moonlit", Assert.Single(await Source().DiscoverAsync(CancellationToken.None)).Key);
    }

    [Fact]
    public async Task NothingIsFoundWhenTheLauncherIsNotInstalled()
    {
        _directories.Remove(Manifests);
        AddManifest("Moonlit");

        var source = Source();

        Assert.False(source.Detect().Installed);
        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public void TheLaunchersUninstallEntryIsEnoughToDetectIt()
    {
        _directories.Remove(Manifests);
        _uninstall.Add(new UninstallEntry("{guid}", "Epic Games Launcher", @"C:\Epic", "Epic Games, Inc.", "", ""));
        _executables.Add(@"C:\Epic\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe");

        Assert.True(Source().Detect().Installed);
    }

    [Fact]
    public async Task DiscoveryHonoursCancellation()
    {
        AddManifest("Moonlit");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Source().DiscoverAsync(new CancellationToken(true)));
    }
}
