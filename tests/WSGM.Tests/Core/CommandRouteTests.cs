using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     Launcher titles: shortcuts that run an exact command, owned by that command rather than by a
///     WSGM marker, and the protocol handler that turns a launcher URI into one.
/// </summary>
public sealed class CommandRouteTests
{
    private const string Launcher = @"C:\WSGM\WSGM.PackagedLaunch.exe";
    private const string Epic = @"C:\Program Files (x86)\Epic Games\Launcher\EpicGamesLauncher.exe";
    private const string Uri = "com.epicgames.launcher://apps/ns%3Aid%3AFortnite?action=launch&silent=true";
    private const string GameExe = @"D:\Games\Hades\Hades.exe";

    private static readonly ShortcutRoute ThroughLauncher =
        new("launcher", "Epic Games", Epic, @"C:\Program Files (x86)\Epic Games\Launcher", Uri, "Evidence.");

    private static readonly ShortcutRoute Direct =
        new("direct", "Game executable", GameExe, @"D:\Games\Hades", "-windowed", "Evidence.");

    private static DiscoveredGame Game(string source = "epic")
    {
        return new DiscoveredGame(source, "Hades", "Hades", @"D:\Games\Hades",
            new GameLaunch("Epic Games", true, "Evidence."), MultiplayerVerdict.Unknown, "Evidence.", true, [], [],
            [ThroughLauncher, Direct]);
    }

    private static ImportedEntry Record(ShortcutRoute route, string source = "epic")
    {
        var fields = CommandShortcut.Compose(route, Launcher);
        return new ImportedEntry
        {
            Source = source,
            Key = "Hades",
            AppId = 3000000001u,
            Name = "Hades",
            Target = fields.Target,
            LaunchOptions = fields.LaunchOptions,
            Mode = nameof(ImportMode.SteamIntegration),
            Route = route.Id,
            ConfirmedUtc = "2026-09-27T00:00:00.0000000Z"
        };
    }

    private static ExistingShortcut Live(ShortcutRoute route, uint appId = 3000000001u, string? options = null)
    {
        var fields = CommandShortcut.Compose(route, Launcher);
        return new ExistingShortcut(appId, fields.Target, options ?? fields.LaunchOptions);
    }

    [Fact]
    public void ANewLauncherTitleIsAddedOnItsFirstRoute()
    {
        var entry = Assert.Single(ImportPlan.Build(
            [Game()], [], [], Launcher, ImportMode.SteamIntegration, false));

        Assert.Equal(ImportAction.Add, entry.Action);
        Assert.Equal("launcher", entry.Route);
        Assert.False(entry.RequiresAcknowledgement);
    }

    [Fact]
    public void AnUnrecordedShortcutRunningOneOfItsRoutesIsAdoptedOnThatRoute()
    {
        var entry = Assert.Single(ImportPlan.Build(
            [Game()], [], [Live(Direct)], Launcher, ImportMode.SteamIntegration, false));

        Assert.Equal(ImportAction.Adopt, entry.Action);
        Assert.Equal("direct", entry.Route);
    }

    [Fact]
    public void ARecordedShortcutStillRunningWhatWasWrittenIsSkipped()
    {
        var entry = Assert.Single(ImportPlan.Build(
            [Game()], [Record(Direct)], [Live(Direct)], Launcher, ImportMode.SteamIntegration, false));

        Assert.Equal(ImportAction.Skip, entry.Action);
        Assert.Equal("direct", entry.Route);
    }

    [Fact]
    public void ARecordedShortcutWhoseArgumentsWereEditedIsAConflict()
    {
        var entry = Assert.Single(ImportPlan.Build(
            [Game()], [Record(Direct)], [Live(Direct, options: "-windowed -dx11")], Launcher,
            ImportMode.SteamIntegration, false));

        Assert.Equal(ImportAction.Conflict, entry.Action);
    }

    [Fact]
    public void AGoneTitleWhoseShortcutIsUnchangedIsOfferedForRemoval()
    {
        var entry = Assert.Single(ImportPlan.Build(
            [], [Record(Direct)], [Live(Direct)], Launcher, ImportMode.SteamIntegration, false));

        Assert.Equal(ImportAction.Remove, entry.Action);
        Assert.True(entry.Selectable);
    }

    [Fact]
    public void AnUntickedSourceIsNeitherScannedNorOfferedForRemoval()
    {
        var plan = ImportPlan.Build(
            [], [Record(Direct)], [Live(Direct)], Launcher, ImportMode.SteamIntegration, false,
            source => source != "epic");

        Assert.Empty(plan);
    }

    [Fact]
    public void AQuotedCommandPutsTheUriWhereTheRegistrationSays()
    {
        var command = ProtocolHandler.Compose($"\"{Epic}\" %1", Uri);

        Assert.NotNull(command);
        Assert.Equal(Epic, command.Program);
        Assert.Equal(Uri, command.Arguments);
    }

    [Fact]
    public void AnUnquotedProgramEndsAtItsExtension()
    {
        var command = ProtocolHandler.Compose(@"C:\Program Files\Ubisoft\upc.exe ""%1""", "uplay://launch/5/0");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Program Files\Ubisoft\upc.exe", command.Program);
        Assert.Equal("\"uplay://launch/5/0\"", command.Arguments);
    }

    [Fact]
    public void ARegistrationWithoutAPlaceholderGetsTheUriAppended()
    {
        var command = ProtocolHandler.Compose(@"""C:\Amazon Games\Amazon Games.exe"" -silent", "amazon-games://play/x");

        Assert.NotNull(command);
        Assert.Equal("-silent \"amazon-games://play/x\"", command.Arguments);
    }

    [Fact]
    public void ExplorerIsNeverTheProgram()
    {
        Assert.Null(ProtocolHandler.Compose(@"C:\Windows\explorer.exe ""%1""", Uri));
    }

    [Fact]
    public void AComposedCommandQuotesOnlyWhatNeedsIt()
    {
        var fields = CommandShortcut.Compose(ThroughLauncher, Launcher);

        Assert.Equal($"\"{Epic}\"", fields.Target);
        Assert.Equal(Uri, fields.LaunchOptions);
        Assert.True(CommandShortcut.Runs(new ExistingShortcut(1, Epic, Uri), ThroughLauncher, Launcher));
    }

    [Fact]
    public void AFollowedRouteRunsThroughThePackagedLauncher()
    {
        var followed = ThroughLauncher with { FollowDirectory = @"D:\Games\Hades" };
        var fields = CommandShortcut.Compose(followed, Launcher);

        Assert.Equal(Launcher, fields.Target);
        Assert.StartsWith(@"--follow --dir ""D:\Games\Hades"" -- ", fields.LaunchOptions);
        Assert.EndsWith(Uri, fields.LaunchOptions);
        Assert.True(CommandShortcut.Runs(new ExistingShortcut(1, fields.Target, fields.LaunchOptions), followed,
            Launcher));
        Assert.False(CommandShortcut.Runs(new ExistingShortcut(1, fields.Target, fields.LaunchOptions), followed,
            string.Empty));
    }

    [Fact]
    public void AFollowedRouteCannotBeComposedWithoutTheLauncher()
    {
        Assert.Throws<ArgumentException>(() =>
            CommandShortcut.Compose(ThroughLauncher with { FollowDirectory = @"D:\Games\Hades" }, string.Empty));
    }
}
