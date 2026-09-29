using System.Diagnostics;
using WSGM.Core;
using WSGM.Device.Tests;

namespace WSGM.Tests.Core.Themes;

/// <summary>Steam's <c>themes_custom</c> link is what serves every theme's fonts and images.</summary>
public sealed class ThemePathsTests
{
    [Fact]
    public void ALinkLeftByCssLoaderIsRepointedAndItsOldFolderKept()
    {
        // A tester moved his themes from CSS Loader's folder to WSGM's: the link still led to the old
        // folder, so every font fell back to Times New Roman and every image was missing.
        using TemporaryDirectory temporary = new();
        var steam = temporary.GetPath("Steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamui"));
        var homebrew = temporary.GetPath("homebrew", "themes");
        Directory.CreateDirectory(homebrew);
        File.WriteAllText(Path.Combine(homebrew, "kept.txt"), "still here");
        var wsgm = temporary.GetPath("wsgm", "themes");
        var link = ThemePaths.SteamLinkPath(steam);
        Junction(link, homebrew);

        var outcome = ThemePaths.EnsureSteamLink(steam, wsgm);

        Assert.Contains("now leads to", outcome, StringComparison.Ordinal);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(wsgm)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(new DirectoryInfo(link).LinkTarget!)),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("still here", File.ReadAllText(Path.Combine(homebrew, "kept.txt")));
    }

    [Fact]
    public void ARealFolderInThePlaceOfTheLinkIsLeftAlone()
    {
        using TemporaryDirectory temporary = new();
        var steam = temporary.GetPath("Steam");
        var link = ThemePaths.SteamLinkPath(steam);
        Directory.CreateDirectory(link);
        File.WriteAllText(Path.Combine(link, "mine.css"), "body{}");

        var outcome = ThemePaths.EnsureSteamLink(steam, temporary.GetPath("wsgm", "themes"));

        Assert.Contains("real folder", outcome, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(link, "mine.css")));
    }

    private static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit(10000);
        Assert.True(Directory.Exists(link), "the test junction could not be created");
    }
}
