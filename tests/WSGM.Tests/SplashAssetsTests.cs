using WSGM.Core;

namespace WSGM.Tests;

public sealed class SplashAssetsTests : IDisposable
{
    private readonly string _root = System.IO.Directory
        .CreateTempSubdirectory("wsgm-splash-assets-")
        .FullName;

    private string SourceDir => Path.Combine(_root, "source");
    private string TargetDir => Path.Combine(_root, "target");

    public SplashAssetsTests()
    {
        System.IO.Directory.CreateDirectory(SourceDir);
        System.IO.Directory.CreateDirectory(TargetDir);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private string WriteSource(string name, string content = "image-bytes")
    {
        var path = Path.Combine(SourceDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void MaterializeCopiesBothSlotsToDeterministicNamesKeepingTheSourceExtension()
    {
        var splash = new SplashConfig
        {
            LogoImagePath = WriteSource("my logo.png", "logo-bytes"),
            BackgroundImagePath = WriteSource("wallpaper.JPG", "bg-bytes"),
        };

        SplashAssets.Materialize(splash, TargetDir);

        Assert.Equal(Path.Combine(TargetDir, "logo.png"), splash.LogoImagePath);
        Assert.Equal(Path.Combine(TargetDir, "background.JPG"), splash.BackgroundImagePath);
        Assert.Equal("logo-bytes", File.ReadAllText(splash.LogoImagePath));
        Assert.Equal("bg-bytes", File.ReadAllText(splash.BackgroundImagePath));
    }

    [Fact]
    public void MaterializeOverwritesTheExistingCopyAndDeletesStaleSiblingExtensions()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg"), "old-jpg");
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "old-png");
        File.WriteAllText(Path.Combine(TargetDir, "unrelated.bmp"), "not-a-slot-file");
        var splash = new SplashConfig { LogoImagePath = WriteSource("new.png", "new-png") };

        SplashAssets.Materialize(splash, TargetDir);

        Assert.Equal(Path.Combine(TargetDir, "logo.png"), splash.LogoImagePath);
        Assert.Equal("new-png", File.ReadAllText(splash.LogoImagePath));
        Assert.False(File.Exists(Path.Combine(TargetDir, "logo.jpg")));
        Assert.True(File.Exists(Path.Combine(TargetDir, "unrelated.bmp")));
    }

    [Fact]
    public void MaterializeWithEmptyPathsDeletesStaleCopiesOfBothSlots()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "stale");
        File.WriteAllText(Path.Combine(TargetDir, "logo.gif"), "stale");
        File.WriteAllText(Path.Combine(TargetDir, "background.jpg"), "stale");
        var splash = new SplashConfig { LogoImagePath = "", BackgroundImagePath = "" };

        SplashAssets.Materialize(splash, TargetDir);

        Assert.Equal("", splash.LogoImagePath);
        Assert.Equal("", splash.BackgroundImagePath);
        Assert.Empty(System.IO.Directory.GetFiles(TargetDir));
    }

    [Fact]
    public void MaterializeLeavesPathsAlreadyInsideTheTargetDirectoryUntouched()
    {
        var splash = new SplashConfig { LogoImagePath = WriteSource("logo.png", "logo-bytes") };
        SplashAssets.Materialize(splash, TargetDir);
        var materialized = splash.LogoImagePath;
        var writeTime = File.GetLastWriteTimeUtc(materialized);

        SplashAssets.Materialize(splash, TargetDir);

        Assert.Equal(materialized, splash.LogoImagePath);
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(materialized));
        Assert.Equal("logo-bytes", File.ReadAllText(materialized));
    }

    [Fact]
    public void MaterializeKeepsTheOriginalPathAndNeverThrowsWhenTheSourceIsUnreadable()
    {
        var missing = Path.Combine(SourceDir, "does-not-exist.png");
        var splash = new SplashConfig
        {
            LogoImagePath = missing,
            BackgroundImagePath = WriteSource("bg.png", "bg-bytes"),
        };

        SplashAssets.Materialize(splash, TargetDir);

        Assert.Equal(missing, splash.LogoImagePath);
        Assert.False(File.Exists(Path.Combine(TargetDir, "logo.png")));
        Assert.Equal(Path.Combine(TargetDir, "background.png"), splash.BackgroundImagePath);
    }

    [Fact]
    public void MaterializeCreatesTheTargetDirectoryWhenItDoesNotExistYet()
    {
        var freshTarget = Path.Combine(_root, "fresh", "splash");
        var splash = new SplashConfig { BackgroundImagePath = WriteSource("bg.webp", "bg-bytes") };

        SplashAssets.Materialize(splash, freshTarget);

        Assert.Equal(Path.Combine(freshTarget, "background.webp"), splash.BackgroundImagePath);
        Assert.Equal("bg-bytes", File.ReadAllText(splash.BackgroundImagePath));
    }
}
