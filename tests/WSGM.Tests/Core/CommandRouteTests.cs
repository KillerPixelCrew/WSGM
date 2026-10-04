using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     Launcher titles' shortcuts: an exact command, owned by that command rather than by a WSGM
///     marker, and the protocol handler that turns a launcher URI into one. What the plan does with
///     them is <see cref="ImportPlanTests" />.
/// </summary>
public sealed class CommandRouteTests
{
    private const string Launcher = @"C:\WSGM\WSGM.PackagedLaunch.exe";
    private const string Epic = @"C:\Program Files (x86)\Epic Games\Launcher\EpicGamesLauncher.exe";
    private const string Uri = "com.epicgames.launcher://apps/ns%3Aid%3AFortnite?action=launch&silent=true";

    private static readonly ShortcutRoute ThroughLauncher =
        new("launcher", "Epic Games", Epic, @"C:\Program Files (x86)\Epic Games\Launcher", Uri, "Evidence.");

    [Fact]
    public void AQuotedCommandPutsTheUriWhereTheRegistrationSays()
    {
        var command = ProtocolHandler.Compose($"\"{Epic}\" %1", Uri, AnyFile);

        Assert.NotNull(command);
        Assert.Equal(Epic, command.Program);
        Assert.Equal(Uri, command.Arguments);
    }

    [Fact]
    public void AnUnquotedProgramEndsAtItsExtension()
    {
        var command = ProtocolHandler.Compose(
            @"C:\Program Files\Ubisoft\upc.exe ""%1""", "uplay://launch/5/0", AnyFile);

        Assert.NotNull(command);
        Assert.Equal(@"C:\Program Files\Ubisoft\upc.exe", command.Program);
        Assert.Equal("\"uplay://launch/5/0\"", command.Arguments);
    }

    [Fact]
    public void ARegistrationWithoutAPlaceholderGetsTheUriAppended()
    {
        var command = ProtocolHandler.Compose(
            @"""C:\Amazon Games\Amazon Games.exe"" -silent", "amazon-games://play/x", AnyFile);

        Assert.NotNull(command);
        Assert.Equal("-silent \"amazon-games://play/x\"", command.Arguments);
    }

    [Fact]
    public void ExplorerIsNeverTheProgram()
    {
        Assert.Null(ProtocolHandler.Compose(@"C:\Windows\explorer.exe ""%1""", Uri, AnyFile));
    }

    [Fact]
    public void EveryArgumentPlaceholderIsTheUriAndLaterArgumentsAreNothing()
    {
        var command = ProtocolHandler.Compose($"\"{Epic}\" --open %* %2 --x %9", Uri, AnyFile);

        Assert.NotNull(command);
        Assert.Equal($"--open {Uri}  --x", command.Arguments);
    }

    [Fact]
    public void AUriWithAQuoteIsRefusedRatherThanEscaped()
    {
        Assert.Null(ProtocolHandler.Compose($"\"{Epic}\" \"%1\"", "foo://x\" --evil \"y", AnyFile));
    }

    [Theory]
    [InlineData("com.epicgames.launcher", true)]
    [InlineData("uplay", true)]
    [InlineData("amazon-games", true)]
    [InlineData("1password", false)]
    [InlineData(@"a\b\c", false)]
    [InlineData("", false)]
    public void OnlyAWellFormedSchemeIsLookedUp(string scheme, bool valid)
    {
        Assert.Equal(valid, ProtocolHandler.ValidScheme(scheme));
    }

    [Fact]
    public void AProgramRegisteredByItsBareNameIsTheSystemOne()
    {
        var command = ProtocolHandler.Compose("rundll32.exe url.dll,FileProtocolHandler %1", Uri, AnyFile);

        Assert.NotNull(command);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), command.Program);
        Assert.Null(ProtocolHandler.Compose("rundll32.exe %1", Uri, _ => false));
        Assert.Null(ProtocolHandler.Compose(@"tools\launcher.exe %1", Uri, AnyFile));
    }

    [Fact]
    public void AComposedCommandQuotesOnlyWhatNeedsIt()
    {
        var fields = ShortcutTestFields.Compose(ThroughLauncher, Launcher);

        Assert.Equal($"\"{Epic}\"", fields.Target);
        Assert.Equal(Uri, fields.LaunchOptions);
        Assert.True(CommandShortcut.Runs(new ExistingShortcut(1, Epic, Uri), ThroughLauncher, Launcher));
    }

    [Fact]
    public void AFollowedRouteRunsThroughThePackagedLauncher()
    {
        var followed = ThroughLauncher with { FollowDirectory = @"D:\Games\Hades" };
        var fields = ShortcutTestFields.Compose(followed, Launcher);

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
        Assert.False(CommandShortcut.TryCompose(
            ThroughLauncher with { FollowDirectory = @"D:\Games\Hades" }, string.Empty, out _, out _));
    }

    [Fact]
    public void ARouteThatCannotBeComposedRefusesItselfWithoutThrowing()
    {
        var withoutLauncher = ThroughLauncher with { FollowDirectory = @"D:\Games\Hades" };
        var relative = ThroughLauncher with
        {
            Target = "EpicGamesLauncher.exe", StartDirectory = "", FollowDirectory = @"D:\Games\Hades"
        };

        Assert.False(CommandShortcut.TryCompose(withoutLauncher, string.Empty, out var none, out var missing));
        Assert.Equal(new ShortcutFields("", "", ""), none);
        Assert.Contains("WSGM.PackagedLaunch", missing, StringComparison.Ordinal);
        Assert.False(CommandShortcut.TryCompose(relative, Launcher, out _, out var refusal));
        Assert.NotEmpty(refusal);
        Assert.False(CommandShortcut.Runs(new ExistingShortcut(1, Launcher, "--follow"), relative, Launcher));
    }

    [Fact]
    public void AFollowedRouteStartingElsewhereThanItsProgramIsRefusedNotDropped()
    {
        var elsewhere = ThroughLauncher with
        {
            StartDirectory = @"D:\Games\Hades", FollowDirectory = @"D:\Games\Hades"
        };

        Assert.False(CommandShortcut.TryCompose(elsewhere, Launcher, out _, out var refusal));
        Assert.Contains(@"D:\Games\Hades", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderFollowShortcutWithATrailingSeparatorStillRunsTheRoute()
    {
        // What a release before the composer trimmed the folder wrote.
        var followed = ThroughLauncher with { FollowDirectory = @"D:\Games\Hades" };
        var written = $@"--follow --dir ""D:\Games\Hades\"" -- ""{Epic}"" {Uri}";
        var sibling = written.Replace(@"Hades\""", @"Hades 2\""", StringComparison.Ordinal);

        Assert.True(CommandShortcut.Runs(new ExistingShortcut(1, Launcher, written), followed, Launcher));
        Assert.False(CommandShortcut.Runs(new ExistingShortcut(1, Launcher, sibling), followed, Launcher));
    }

    [Fact]
    public void LauncherRoutesStartInTheirProgramsFolder()
    {
        var route = ShortcutRoute.ThroughLauncher(
            new ProtocolCommand(Epic, Uri), "Epic Games", ShortcutRoute.FollowedLauncherEvidence("Epic Games"),
            @"D:\Games\Hades");

        Assert.Equal("launcher", route.Id);
        Assert.Equal(@"C:\Program Files (x86)\Epic Games\Launcher", route.StartDirectory);
        Assert.Equal(@"D:\Games\Hades", route.FollowDirectory);
        Assert.StartsWith("Starts through Epic Games.", route.Evidence, StringComparison.Ordinal);
        Assert.True(CommandShortcut.TryCompose(route, Launcher, out _, out _));
    }

    private static bool AnyFile(string path)
    {
        return true;
    }
}
