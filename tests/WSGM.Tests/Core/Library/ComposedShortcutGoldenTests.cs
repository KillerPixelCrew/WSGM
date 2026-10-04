using WSGM.Core;

namespace WSGM.Tests.Core.Library;

/// <summary>Exact Steam fields for the source route shapes, independent of live launchers and Steam.</summary>
public sealed class ComposedShortcutGoldenTests
{
    private const string Launcher = @"C:\WSGM App\WSGM.PackagedLaunch.exe";
    private const string QuotedLauncher = @"""C:\WSGM App\WSGM.PackagedLaunch.exe""";
    private const string LauncherDirectory = @"""C:\WSGM App""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmazonDiscoveryComposesBothRouteOrdersExactly(bool signIn)
    {
        const string client = @"C:\Amazon Games\Amazon Games.exe";
        const string folder = @"D:\Amazon Game";
        var fuel = signIn
            ? "{\"Main\":{\"Command\":\"game.exe\",\"ClientId\":\"client\",\"AuthScopes\":[\"scope\"]}}"
            : "{\"Main\":{\"Command\":\"game.exe\",\"Args\":[\"--profile=My Saves\"]}}";
        AmazonLibrarySource source = new(@"C:\Data",
            _ => [new AmazonInstall("game", "Game", folder)],
            path => path == folder + @"\fuel.json" ? fuel : null,
            _ => true, _ => true,
            _ => new ProtocolCommand(client, @"""amazon-games://play/game"""));
        var game = Assert.Single(await source.DiscoverAsync([], CancellationToken.None));
        Assert.Equal(signIn ? ["launcher", "direct"] : ["direct", "launcher"],
            game.CommandRoutes.Select(route => route.Id));
        var direct = new ShortcutFields(@"""D:\Amazon Game\game.exe""", @"""D:\Amazon Game""",
            signIn ? "" : @"""--profile=My Saves""");
        var followed = new ShortcutFields(QuotedLauncher, LauncherDirectory,
            @"--follow --dir ""D:\Amazon Game"" -- ""C:\Amazon Games\Amazon Games.exe"" ""amazon-games://play/game""");

        Assert.Equal(signIn ? [followed, direct] : [direct, followed],
            game.CommandRoutes.Select(route => ShortcutTestFields.Compose(route, Launcher)));
    }

    [Theory]
    [InlineData(ImportMode.SteamIntegration, false, false, "--aumid Publisher.Game_abc!App --mode steam-overlay")]
    [InlineData(ImportMode.SteamIntegration, false, true, "--aumid Publisher.Game_abc!App --mode steam-overlay")]
    [InlineData(ImportMode.SteamIntegration, true, true,
        "--aumid Publisher.Game_abc!App --mode steam-overlay --multiplayer --acknowledge-ban-risk")]
    [InlineData(ImportMode.ControllerOnly, false, false, "--aumid Publisher.Game_abc!App --mode controller-only")]
    [InlineData(ImportMode.ControllerOnly, false, true, "--aumid Publisher.Game_abc!App --mode controller-only")]
    [InlineData(ImportMode.ControllerOnly, true, false,
        "--aumid Publisher.Game_abc!App --mode controller-only --multiplayer")]
    [InlineData(ImportMode.ControllerOnly, true, true,
        "--aumid Publisher.Game_abc!App --mode controller-only --multiplayer --acknowledge-ban-risk")]
    public void XboxPackagedModesKeepTheirExactStoredFields(ImportMode mode, bool multiplayer, bool acknowledged,
        string options)
    {
        Assert.Equal(new ShortcutFields(QuotedLauncher, LauncherDirectory, options),
            PackagedLauncherShortcut.Compose(Launcher, "Publisher.Game_abc!App", mode, multiplayer, acknowledged));
    }

    [Fact]
    public void XboxMultiplayerOverlayWithoutAcknowledgementRefusesComposition()
    {
        var failure = Assert.Throws<ArgumentException>(() => PackagedLauncherShortcut.Compose(Launcher,
            "Publisher.Game_abc!App", ImportMode.SteamIntegration, true, false));
        Assert.Contains("without an acknowledged ban risk", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DirectRoutes))]
    public void DirectSourceShapesKeepTargetWorkingDirectoryAndArguments(string source, string target,
        string directory, string arguments, string expectedTarget, string expectedDirectory)
    {
        ShortcutRoute route = new("direct", source, target, directory, arguments, "Fixture route");
        Assert.Equal(new ShortcutFields(expectedTarget, expectedDirectory, arguments),
            ShortcutTestFields.Compose(route, Launcher));
    }

    public static IEnumerable<object[]> DirectRoutes()
    {
        yield return
        [
            "Epic direct", @"D:\Epic Game\Binaries\game.exe", @"D:\Epic Game", "-nolauncher",
            @"""D:\Epic Game\Binaries\game.exe""", @"""D:\Epic Game"""
        ];
        yield return
        [
            "GOG direct", @"D:\GOG Game\game.exe", @"D:\GOG Game", "-fullscreen",
            @"""D:\GOG Game\game.exe""", @"""D:\GOG Game"""
        ];
        yield return
        [
            "Ubisoft direct", @"D:\Ubisoft Game\bin\game.exe", @"D:\Ubisoft Game\bin", "",
            @"""D:\Ubisoft Game\bin\game.exe""", @"""D:\Ubisoft Game\bin"""
        ];
        yield return
        [
            "Battle.net classic", @"D:\Diablo II\Diablo II.exe", @"D:\Diablo II", "",
            @"""D:\Diablo II\Diablo II.exe""", @"""D:\Diablo II"""
        ];
        yield return
        [
            "Amazon direct first", @"D:\Amazon Game\bin\game.exe", @"D:\Amazon Game\data",
            "--first " + '"' + "two words" + '"',
            @"""D:\Amazon Game\bin\game.exe""", @"""D:\Amazon Game\data"""
        ];
        yield return
        [
            "Amazon launcher first direct alternative", @"D:\Amazon Game\game.exe", @"D:\Amazon Game", "-online",
            @"""D:\Amazon Game\game.exe""", @"""D:\Amazon Game"""
        ];
        yield return
        [
            "itch", @"D:\itch Game\game.exe", @"D:\itch Game", "",
            @"""D:\itch Game\game.exe""", @"""D:\itch Game"""
        ];
        yield return
        [
            "Folder exe", @"D:\Folder Game\game.exe", "", "",
            @"""D:\Folder Game\game.exe""", @"""D:\Folder Game"""
        ];
        yield return
        [
            "Folder resolved lnk", @"D:\Folder Game\game.exe", @"E:\Working Data", @"--profile ""Local Player""",
            @"""D:\Folder Game\game.exe""", @"""E:\Working Data"""
        ];
        yield return
        [
            "Folder resolved url", @"C:\Ubisoft\upc.exe", @"C:\Ubisoft", "uplay://launch/123/0",
            @"C:\Ubisoft\upc.exe", @"C:\Ubisoft"
        ];
    }

    [Theory]
    [MemberData(nameof(FollowedRoutes))]
    public void FollowedSourceShapesKeepTheirExactStoredFields(string source, string target, string arguments,
        string directory, string marker, string expected)
    {
        ShortcutRoute route = new("launcher", source, target, "", arguments, "Fixture route", directory, marker);
        Assert.Equal(new ShortcutFields(QuotedLauncher, LauncherDirectory, expected),
            ShortcutTestFields.Compose(route, Launcher));
    }

    public static IEnumerable<object[]> FollowedRoutes()
    {
        yield return
        [
            "Epic launcher", @"C:\Epic\Launcher.exe",
            "com.epicgames.launcher://apps/ns%3Aitem%3Agame?action=launch&silent=true", @"D:\Epic Game", "",
            @"--follow --dir ""D:\Epic Game"" -- ""C:\Epic\Launcher.exe"" com.epicgames.launcher://apps/ns%3Aitem%3Agame?action=launch&silent=true"
        ];
        yield return
        [
            "GOG Galaxy", @"C:\GOG Galaxy\GalaxyClient.exe",
            @"/launchViaAutostart /gameId=123 /command=runGame /path=""D:\GOG Game""", @"D:\GOG Game", "",
            @"--follow --dir ""D:\GOG Game"" -- ""C:\GOG Galaxy\GalaxyClient.exe"" /launchViaAutostart /gameId=123 /command=runGame /path=""D:\GOG Game"""
        ];
        yield return
        [
            "Ubisoft launcher", @"C:\Ubisoft\upc.exe", "uplay://launch/123/0", @"D:\Ubisoft Game", "",
            @"--follow --dir ""D:\Ubisoft Game"" -- ""C:\Ubisoft\upc.exe"" uplay://launch/123/0"
        ];
        yield return
        [
            "Battle.net launcher", @"C:\Battle.net\Battle.net.exe", @"--exec=""launch Pro""", @"D:\Overwatch", "",
            @"--follow --dir ""D:\Overwatch"" -- ""C:\Battle.net\Battle.net.exe"" --exec=""launch Pro"""
        ];
        yield return
        [
            "Amazon direct first launcher alternative", @"C:\Amazon Games\Amazon Games.exe",
            @"""amazon-games://play/game""", @"D:\Amazon Game", "",
            @"--follow --dir ""D:\Amazon Game"" -- ""C:\Amazon Games\Amazon Games.exe"" ""amazon-games://play/game"""
        ];
        yield return
        [
            "Amazon launcher first", @"C:\Amazon Games\Amazon Games.exe", @"""amazon-games://play/game""",
            @"D:\Amazon Game", "",
            @"--follow --dir ""D:\Amazon Game"" -- ""C:\Amazon Games\Amazon Games.exe"" ""amazon-games://play/game"""
        ];
        yield return
        [
            "Prism", @"C:\Prism\prismlauncher.exe", @"--launch ""1.20 Fabric""", "", @"D:\Minecraft\1.20 Fabric",
            @"--follow --marker ""D:\Minecraft\1.20 Fabric"" -- ""C:\Prism\prismlauncher.exe"" --launch ""1.20 Fabric"""
        ];
        yield return
        [
            "ATLauncher", @"C:\ATLauncher\ATLauncher.exe", @"--launch ""Modded"" --close-launcher --no-launcher-update",
            "", @"D:\Minecraft\Modded",
            @"--follow --marker ""D:\Minecraft\Modded"" -- ""C:\ATLauncher\ATLauncher.exe"" --launch ""Modded"" --close-launcher --no-launcher-update"
        ];
        yield return
        [
            "Directory and marker", @"C:\Launcher\start.exe", "  --arg=raw  ", @"D:\Game Folder\",
            @"D:\Instance Folder\",
            @"--follow --dir ""D:\Game Folder"" --marker ""D:\Instance Folder"" -- ""C:\Launcher\start.exe"" --arg=raw"
        ];
    }

    [Theory]
    [InlineData(@"D:\", "")]
    [InlineData("", @"D:\")]
    public void DriveRootFollowingRefusesWithoutPartialShortcutFields(string directory, string marker)
    {
        ShortcutRoute route = new("launcher", "Drive root", @"C:\Launcher\start.exe", "", "", "Fixture route",
            directory, marker);
        Assert.False(CommandShortcut.TryCompose(route, Launcher, out var fields, out var refusal));
        Assert.Equal(new ShortcutFields("", "", ""), fields);
        Assert.Contains("root", refusal, StringComparison.OrdinalIgnoreCase);
    }
}
