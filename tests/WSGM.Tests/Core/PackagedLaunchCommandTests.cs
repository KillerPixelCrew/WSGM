using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     The command line the importer writes and the launcher parses. Nothing here defaults: the two
///     modes differ in whether anything is written into the game, so an unrecognized value is a
///     refusal rather than a guess.
/// </summary>
public sealed class PackagedLaunchCommandTests
{
    private const string Aumid = "Publisher.Game_abc123!App";

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
    public void OverlongGameArgumentsAreRefusedOnBothSides()
    {
        var tooLong = new string('x', PackagedLaunchCommand.MaximumArgumentsLength + 1);

        Refused("--aumid", Aumid, "--mode", "controller-only", "--args", tooLong);
        Assert.Throws<ArgumentException>(() => PackagedLaunchCommand.Compose(
            new PackagedLaunchRequest(Aumid, PackagedLaunchMode.ControllerOnly, GameArguments: tooLong)));
    }
}
