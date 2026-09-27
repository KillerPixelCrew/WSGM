using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     The follow form of the packaged launcher's command line: what a launcher title's shortcut
///     carries, and how the launcher reads it back with the launcher's own arguments untouched.
/// </summary>
public sealed class FollowCommandTests
{
    private const string BattleNet = @"C:\Program Files (x86)\Battle.net\Battle.net.exe";
    private const string Game = @"C:\Program Files (x86)\Overwatch";

    private static PackagedFollowRequest RoundTrip(PackagedFollowRequest request)
    {
        var composed = PackagedLaunchCommand.ComposeFollow(request);
        Assert.True(PackagedLaunchCommand.TryParseFollow(composed, out var parsed, out var error), error);
        return parsed;
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
}
