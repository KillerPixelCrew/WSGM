using WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

/// <summary>
///     Which processes a followed game is: those running from its install folder, or a Java process
///     whose command line names its instance. Never the launcher that started it.
/// </summary>
public sealed class FollowedGameRuleTests
{
    private static readonly string Launcher =
        FollowedGameRule.Normalize(@"C:\Program Files (x86)\Epic Games\Launcher\EpicGamesLauncher.exe");

    private static readonly string Instance =
        FollowedGameRule.Normalize(@"C:\Users\A\AppData\Roaming\PrismLauncher\instances\Pack");

    private static bool Matches(string directory, string image, string? commandLine = null, bool marker = false)
    {
        return FollowedGameRule.Matches(FollowedGameRule.Folder(directory), marker ? [Instance] : [], Launcher, image,
            () => commandLine);
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
    public void AJavaProcessNamingTheInstanceIsTheGame()
    {
        Assert.True(Matches(string.Empty, @"C:\Java\bin\javaw.exe",
            "javaw -Djava.library.path=C:/Users/A/AppData/Roaming/PrismLauncher/instances/Pack/natives -cp x",
            true));
    }

    [Fact]
    public void AJavaProcessOfASiblingInstanceIsNot()
    {
        Assert.False(Matches(string.Empty, @"C:\Java\bin\javaw.exe",
            "javaw -Djava.library.path=C:/Users/A/AppData/Roaming/PrismLauncher/instances/Pack 2/natives",
            true));
    }

    [Fact]
    public void OnlyJavaIsReadForTheMarker()
    {
        Assert.False(Matches(string.Empty, @"C:\Tools\other.exe",
            @"other.exe C:\Users\A\AppData\Roaming\PrismLauncher\instances\Pack", true));
    }
}
