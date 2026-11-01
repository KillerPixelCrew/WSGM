using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class GameToolsPhasesTests
{
    [Theory]
    [InlineData(GameToolPhase.Starting, "install", "Starting")]
    [InlineData(GameToolPhase.Download, "install", "Downloading OptiScaler 0.9.4 from GitHub")]
    [InlineData(GameToolPhase.Verify, "update", "Checking the package")]
    [InlineData(GameToolPhase.Copy, "switchChannel", "Backing up game files and copying OptiScaler")]
    [InlineData(GameToolPhase.Settings, "install", "Applying settings")]
    [InlineData(GameToolPhase.Settings, "configure", "Saving settings")]
    [InlineData(GameToolPhase.Remove, "remove", "Removing OptiScaler and restoring the game's files")]
    [InlineData(GameToolPhase.Rescan, "repair", "Checking the game folder")]
    public void EveryStablePhaseIdHasItsLine(string phase, string operation, string expected)
    {
        Assert.Equal(expected, GameToolsPhases.Detail(phase, operation, "OptiScaler", "0.9.4", "GitHub"));
    }

    [Fact]
    public void ADownloadBeforeTheReleaseIsKnownNamesOnlyTheTool()
    {
        Assert.Equal("Downloading OptiScaler", GameToolsPhases.Detail(GameToolPhase.Download, "install",
            "OptiScaler", null, null));
    }

    [Fact]
    public void TheInstallStepsAreTheFourPublishedIds()
    {
        Assert.Equal(["download", "verify", "copy", "settings"],
            [GameToolPhase.Download, GameToolPhase.Verify, GameToolPhase.Copy, GameToolPhase.Settings]);
    }
}
