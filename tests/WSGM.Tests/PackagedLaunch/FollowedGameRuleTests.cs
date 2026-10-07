extern alias packagedLaunch;
using packagedLaunch::WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

/// <summary>
///     Which processes a followed game is: those running from its install folder, or a Java process
///     whose command line names its instance. Never the launcher that started it.
/// </summary>
public sealed class FollowedGameRuleTests
{
    private const string Instance = @"C:\Users\A\AppData\Roaming\PrismLauncher\instances\Pack";

    private static readonly string[] Java = ["java.exe", "javaw.exe"];

    private static readonly string Launcher =
        FollowedGameRule.Normalize(@"C:\Program Files (x86)\Epic Games\Launcher\EpicGamesLauncher.exe");

    private static bool Matches(
        string directory, string image, string? commandLine = null, bool marker = false,
        string[]? alsoKnownAs = null)
    {
        FollowedGameTarget target = new(
            [.. new[] { directory }.Concat(alsoKnownAs ?? []).Select(FollowedGameRule.Folder)],
            marker ? [FollowedGameRule.Normalize(Instance)] : [],
            Java,
            [Launcher]);
        return FollowedGameRule.Matches(target, image, () => commandLine);
    }

    [Fact]
    public void AProcessInsideTheInstallFolderIsTheGame()
    {
        Assert.True(Matches(@"D:\Games\Hades", @"D:\Games\Hades\x64\Hades.exe"));
    }

    [Fact]
    public void AFolderThatOnlySharesItsNameAsAPrefixIsNot()
    {
        Assert.False(Matches(@"D:\Games\Hades", @"D:\Games\Hades II\Hades2.exe"));
    }

    [Fact]
    public void TheLauncherIsNeverTheGame()
    {
        Assert.False(Matches(@"C:\Program Files (x86)\Epic Games",
            @"C:\Program Files (x86)\Epic Games\Launcher\EpicGamesLauncher.exe"));
    }

    [Fact]
    public void AGameBehindAJunctionIsFoundByTheFolderItReallyLivesIn()
    {
        // The image path is reported after the junction is resolved, so the folder's final path is
        // one of the spellings the target carries.
        Assert.True(Matches(@"C:\Games\Hades", @"E:\Library\Hades\x64\Hades.exe", alsoKnownAs: [@"E:\Library\Hades"]));
    }

    [Fact]
    public void AJavaProcessNamingTheInstanceIsTheGame()
    {
        Assert.True(Matches(string.Empty, @"C:\Java\bin\javaw.exe",
            "javaw -Djava.library.path=C:/Users/A/AppData/Roaming/PrismLauncher/instances/Pack/natives -cp x",
            true));
    }

    [Fact]
    public void AJavaProcessOfASiblingInstanceIsNot()
    {
        // Pack and Pack 2 side by side: following Pack must never contain, and so never kill, Pack 2.
        Assert.False(Matches(string.Empty, @"C:\Java\bin\javaw.exe",
            "javaw -Djava.library.path=C:/Users/A/AppData/Roaming/PrismLauncher/instances/Pack 2/natives",
            true));
    }

    [Theory]
    [InlineData("javaw \"-Dminecraft.dir=C:\\Users\\A\\AppData\\Roaming\\PrismLauncher\\instances\\Pack\" -cp x")]
    [InlineData("javaw -cp C:\\Users\\A\\AppData\\Roaming\\PrismLauncher\\instances\\Pack;lib.jar Main")]
    [InlineData("javaw -Dminecraft.dir=C:\\Users\\A\\AppData\\Roaming\\PrismLauncher\\instances\\Pack")]
    public void TheInstanceEndsAtAQuoteASemicolonOrTheEndOfTheLine(string commandLine)
    {
        Assert.True(Matches(string.Empty, @"C:\Java\bin\javaw.exe", commandLine, true));
    }

    [Fact]
    public void OnlyTheMarkerImagesAreReadForTheMarker()
    {
        Assert.False(Matches(string.Empty, @"C:\Tools\other.exe",
            @"other.exe C:\Users\A\AppData\Roaming\PrismLauncher\instances\Pack\file.txt", true));
    }
}
