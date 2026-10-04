using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     The command line the importer writes and the launcher parses. Nothing here defaults: the two
///     modes differ in whether anything is written into the game, so an unrecognized value is a
///     refusal rather than a guess. The follow form is here too: what a launcher title's shortcut
///     carries, and how the launcher reads it back with the launcher's own arguments untouched.
/// </summary>
public sealed class PackagedLaunchCommandTests
{
    private const string Aumid = "Publisher.Game_abc123!App";
    private const string BattleNet = @"C:\Program Files (x86)\Battle.net\Battle.net.exe";
    private const string Game = @"C:\Program Files (x86)\Overwatch";

    private static PackagedFollowRequest RoundTrip(PackagedFollowRequest request)
    {
        var composed = PackagedLaunchCommand.ComposeFollow(request);
        Assert.True(PackagedLaunchCommand.TryParseFollow(composed, out var parsed, out var error), error);
        return parsed;
    }

    private static PackagedLaunchRequest Parsed(params string[] arguments)
    {
        Assert.True(PackagedLaunchCommand.TryParse(arguments, out var command, out var error), error);
        Assert.Equal(PackagedLaunchAction.Launch, command.Action);
        return command.Request!;
    }

    private static string Refused(params string[] arguments)
    {
        Assert.False(PackagedLaunchCommand.TryParse(arguments, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error!;
    }

    [Fact]
    public void AComposedRequestParsesBackToItself()
    {
        // The importer and the launcher share this file precisely so this holds.
        PackagedLaunchRequest original = new(
            Aumid, PackagedLaunchMode.SteamOverlay, true, true, "-windowed");

        var parsed = Parsed(PackagedLaunchCommand.Compose(original).Split(' '));

        Assert.Equal(original.Aumid, parsed.Aumid);
        Assert.Equal(original.Mode, parsed.Mode);
        Assert.True(parsed.Multiplayer);
        Assert.True(parsed.AcknowledgedBanRisk);
        Assert.Equal("-windowed", parsed.GameArguments);
    }

    [Fact]
    public void TheComposedFormNamesOnlyWhatWasAskedFor()
    {
        var composed = PackagedLaunchCommand.Compose(
            new PackagedLaunchRequest(Aumid, PackagedLaunchMode.ControllerOnly));

        Assert.Equal($"--aumid {Aumid} --mode controller-only", composed);
    }

    [Fact]
    public void AStandingAcknowledgementIsNotWrittenForASinglePlayerTitle()
    {
        // A shortcut should record what the user actually accepted, not carry a blanket consent
        // that would silently apply if the title were later found to be multiplayer.
        var composed = PackagedLaunchCommand.Compose(
            new PackagedLaunchRequest(Aumid, PackagedLaunchMode.SteamOverlay, AcknowledgedBanRisk: true));

        Assert.DoesNotContain("--acknowledge-ban-risk", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverlayRouteForAMultiplayerTitleNeedsAnAcknowledgedBanRisk()
    {
        var error = Refused("--aumid", Aumid, "--mode", "steam-overlay", "--multiplayer");

        Assert.Contains("--acknowledge-ban-risk", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposingThatSameRequestIsRefusedRatherThanWritten()
    {
        // The refusal belongs on both sides: a shortcut that cannot launch is worse than one that
        // was never created, because the user only finds out when they press play.
        Assert.Throws<ArgumentException>(() => PackagedLaunchCommand.Compose(
            new PackagedLaunchRequest(Aumid, PackagedLaunchMode.SteamOverlay, true)));
    }

    [Fact]
    public void ControllerOnlyNeedsNoAcknowledgementForAMultiplayerTitle()
    {
        // Controller-only injects nothing, so there is no risk to accept.
        var parsed = Parsed("--aumid", Aumid, "--mode", "controller-only", "--multiplayer");

        Assert.Equal(PackagedLaunchMode.ControllerOnly, parsed.Mode);
        Assert.True(parsed.Multiplayer);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("steam")]
    [InlineData("")]
    public void AnUnrecognizedModeIsRefusedRatherThanDefaulted(string mode)
    {
        var error = Refused("--aumid", Aumid, "--mode", mode);

        Assert.Contains("not a launch mode", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingModeIsRefused()
    {
        Assert.Contains("--mode", Refused("--aumid", Aumid), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingAumidIsRefused()
    {
        Assert.Contains("--aumid", Refused("--mode", "controller-only"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no-separator")]
    [InlineData("!missing-family")]
    [InlineData("missing-app!")]
    [InlineData("two!separators!here")]
    [InlineData("  ")]
    public void AMalformedAumidIsRefused(string aumid)
    {
        Assert.False(PackagedLaunchCommand.ValidAumid(aumid));
        Refused("--aumid", aumid, "--mode", "controller-only");
    }

    [Fact]
    public void AnUnknownOptionIsRefusedRatherThanIgnored()
    {
        var error = Refused("--aumid", Aumid, "--mode", "controller-only", "--inject", "evil.dll");

        Assert.Contains("--inject", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedOptionIsRefusedRatherThanSilentlyTakingOne()
    {
        var error = Refused("--aumid", Aumid, "--aumid", "Other_x!App", "--mode", "controller-only");

        Assert.Contains("more than once", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionWithoutItsValueIsRefused()
    {
        Assert.Contains("needs a value", Refused("--aumid"), StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryTakesNoOtherOption()
    {
        Assert.True(PackagedLaunchCommand.TryParse(["--recover"], out var command, out _));
        Assert.Equal(PackagedLaunchAction.Recover, command.Action);

        Refused("--recover", "--aumid", Aumid);
    }

    [Fact]
    public void HelpOutranksEverythingElseOnTheLine()
    {
        Assert.True(PackagedLaunchCommand.TryParse(["--aumid", Aumid, "--help"], out var command, out _));
        Assert.Equal(PackagedLaunchAction.Help, command.Action);
    }

    [Fact]
    public void ThePackageFamilyIsTheHalfBeforeTheSeparator()
    {
        Assert.Equal("Publisher.Game_abc123", Parsed("--aumid", Aumid, "--mode", "controller-only")
            .PackageFamilyName);
    }

    [Fact]
    public void LongGameArgumentsUseTheWindowsLimitWhenComposed()
    {
        var longArguments = new string('x', 3000);
        var request = new PackagedLaunchRequest(Aumid, PackagedLaunchMode.ControllerOnly,
            GameArguments: longArguments);
        Assert.Contains(longArguments, PackagedLaunchCommand.Compose(request), StringComparison.Ordinal);
        Assert.Equal(longArguments,
            Parsed("--aumid", Aumid, "--mode", "controller-only", "--args", longArguments).GameArguments);

        var tooLong = new string('x', PackagedLaunchCommand.WindowsCommandLineLimit);
        Assert.Throws<ArgumentException>(() => PackagedLaunchCommand.Compose(
            new PackagedLaunchRequest(Aumid, PackagedLaunchMode.ControllerOnly, GameArguments: tooLong)));
    }

    [Fact]
    public void ALauncherArgumentWithQuotesSurvivesTheRoundTrip()
    {
        PackagedFollowRequest request = new(BattleNet, "--exec=\"launch Pro\"", Game, string.Empty);

        Assert.Equal(request, RoundTrip(request));
    }

    [Fact]
    public void AMarkerAloneIsEnoughForAJavaGame()
    {
        PackagedFollowRequest request = new(
            @"C:\Users\A\AppData\Local\Programs\PrismLauncher\prismlauncher.exe", "--launch \"My Pack\"",
            string.Empty, @"C:\Users\A\AppData\Roaming\PrismLauncher\instances\My Pack");

        Assert.Equal(request, RoundTrip(request));
    }

    [Fact]
    public void AMarkerIsLookedForInTheJavaRuntimeOnly()
    {
        PackagedFollowRequest withMarker = new(BattleNet, string.Empty, string.Empty, @"C:\Instances\Pack");
        PackagedFollowRequest withoutMarker = new(BattleNet, string.Empty, Game, string.Empty);

        Assert.Equal(["java.exe", "javaw.exe"], withMarker.MarkerImages);
        Assert.Empty(withoutMarker.MarkerImages);
    }

    [Fact]
    public void AnEmptyArgumentListStaysEmpty()
    {
        PackagedFollowRequest request = new(BattleNet, string.Empty, Game, string.Empty);

        Assert.Equal(request, RoundTrip(request));
    }

    [Fact]
    public void ARequestThatCannotRecogniseItsGameIsRefused()
    {
        Assert.Throws<ArgumentException>(() => PackagedLaunchCommand.ComposeFollow(
            new PackagedFollowRequest(BattleNet, string.Empty, string.Empty, string.Empty)));
    }

    [Theory]
    [InlineData("--follow --dir \"C:\\Games\\X\"")]
    [InlineData("--follow --dir \"C:\\Games\\X\" -- \"relative.exe\"")]
    [InlineData("--follow --dir \"C:\\Games\\X\" -- \"C:\\Tools\\script.bat\"")]
    [InlineData("--dir \"C:\\Games\\X\" -- \"C:\\Games\\X\\launcher.exe\"")]
    [InlineData("--follow --what \"C:\\Games\\X\" -- \"C:\\Games\\X\\launcher.exe\"")]
    public void AMalformedFollowLineIsRefused(string line)
    {
        Assert.False(PackagedLaunchCommand.TryParseFollow(line, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ADoubleDashInsideAQuotedFolderIsNotTheSeparator()
    {
        PackagedFollowRequest request = new(BattleNet, "-x", @"D:\Games -- Old\Game", string.Empty);

        Assert.Equal(request, RoundTrip(request));
    }

    [Fact]
    public void AnInstallFolderIsWrittenWithoutItsTrailingBackslash()
    {
        // Under Windows' argument rules a backslash before the closing quote escapes it and swallows
        // the rest of the line, which is how a launcher's own install path broke the shortcut.
        var composed = PackagedLaunchCommand.ComposeFollow(
            new PackagedFollowRequest(BattleNet, "--exec=\"launch Pro\"", Game + "\\", Game + "\\Marker\\"));

        Assert.Contains($"--dir \"{Game}\" ", composed, StringComparison.Ordinal);
        Assert.Contains($"--marker \"{Game}\\Marker\" ", composed, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortcutWrittenWithATrailingBackslashIsReadBackInTheOneForm()
    {
        var line = $"--follow --dir \"{Game}\\\" -- \"{BattleNet}\" --exec=\"launch Pro\"";

        Assert.True(PackagedLaunchCommand.TryParseFollow(line, out var parsed, out var error), error);
        Assert.Equal(new PackagedFollowRequest(BattleNet, "--exec=\"launch Pro\"", Game, string.Empty), parsed);
    }

    [Theory]
    [InlineData("E:\\")]
    [InlineData("E:")]
    [InlineData("\\")]
    public void ADriveRootIsRefusedByName(string root)
    {
        var refusal = PackagedLaunchCommand.FollowRefusal(new PackagedFollowRequest(BattleNet, "", root, ""));

        Assert.NotNull(refusal);
        Assert.Contains("drive root", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative.exe", "absolute")]
    [InlineData("C:\\Tools\\script.bat", ".exe")]
    [InlineData("C:\\Tools\\\"odd\".exe", "quote")]
    public void EachProgramRefusalNamesItsCondition(string program, string condition)
    {
        var refusal = PackagedLaunchCommand.FollowRefusal(new PackagedFollowRequest(program, "", Game, ""));

        Assert.NotNull(refusal);
        Assert.Contains(condition, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageSaysEitherFolderOrMarkerWillDo()
    {
        Assert.Contains("--follow [--dir <folder>] [--marker <path>]", PackagedLaunchCommand.Usage,
            StringComparison.Ordinal);
        Assert.Contains("at least one of the two", PackagedLaunchCommand.Usage, StringComparison.Ordinal);
    }
}
