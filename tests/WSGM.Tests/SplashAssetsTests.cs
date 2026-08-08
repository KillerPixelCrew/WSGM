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

    private string[] FileNames() =>
        System.IO.Directory
            .GetFiles(TargetDir)
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

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

    [Fact]
    public void PrepareRewritesThePathsToTheFinalNamesWithoutTouchingTheLiveFilesYet()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };

        using var staged = SplashAssets.Prepare(splash, TargetDir);

        Assert.Equal(Path.Combine(TargetDir, "logo.png"), splash.LogoImagePath);
        Assert.Equal("live-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void RollbackKeepsThePreviouslyMaterializedBytesAndLeavesNoSidecarBehind()
    {
        var first = new SplashConfig
        {
            LogoImagePath = WriteSource("first.png", "first-logo"),
            BackgroundImagePath = WriteSource("first-bg.png", "first-bg"),
        };
        SplashAssets.Materialize(first, TargetDir);
        var liveFiles = System.IO.Directory.GetFiles(TargetDir).OrderBy(f => f).ToArray();

        // A save whose config write fails: stage, then roll back.
        var second = new SplashConfig
        {
            LogoImagePath = WriteSource("second.png", "second-logo"),
            BackgroundImagePath = WriteSource("second-bg.jpg", "second-bg"),
        };
        var staged = SplashAssets.Prepare(second, TargetDir);
        staged.Rollback();
        staged.Dispose();

        Assert.Equal(liveFiles, System.IO.Directory.GetFiles(TargetDir).OrderBy(f => f).ToArray());
        Assert.Equal("first-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
        Assert.Equal("first-bg", File.ReadAllText(Path.Combine(TargetDir, "background.png")));
    }

    [Fact]
    public void DisposeWithoutCommitRollsBackTheStagedCopies()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };

        using (SplashAssets.Prepare(splash, TargetDir)) { }

        Assert.Equal(new[] { "logo.png" }, FileNames());
        Assert.Equal("live-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void CommitReplacesTheLiveFilesAndRemovesTheSidecars()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "live-logo");
        var splash = new SplashConfig
        {
            LogoImagePath = WriteSource("picked.jpg", "new-logo"),
            BackgroundImagePath = WriteSource("picked-bg.png", "new-bg"),
        };

        using var staged = SplashAssets.Prepare(splash, TargetDir);
        staged.Commit();

        Assert.Equal(Path.Combine(TargetDir, "logo.jpg"), splash.LogoImagePath);
        Assert.Equal("new-logo", File.ReadAllText(splash.LogoImagePath));
        Assert.Equal("new-bg", File.ReadAllText(splash.BackgroundImagePath));
        Assert.False(File.Exists(Path.Combine(TargetDir, "logo.png"))); // Stale extension gone.
        Assert.Equal(new[] { "background.png", "logo.jpg" }, FileNames());
    }

    [Fact]
    public void ClearingASlotRemovesTheLiveFileOnlyOnCommit()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = "" };

        var staged = SplashAssets.Prepare(splash, TargetDir);
        Assert.True(File.Exists(Path.Combine(TargetDir, "logo.png")));

        // A failed save leaves the cleared slot's file in place, matching the
        // still-persisted config that still points at it.
        staged.Rollback();
        Assert.True(File.Exists(Path.Combine(TargetDir, "logo.png")));

        using var committed = SplashAssets.Prepare(splash, TargetDir);
        committed.Commit();
        Assert.False(File.Exists(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void PrepareSweepsASidecarOrphanedByACrashedSaveSoCommitLeavesOnlyTheNewCopy()
    {
        // A save killed between Prepare and Commit leaves "logo.jpg.wsgmnew" behind.
        // DeleteCopies can never match it — its file name without extension is
        // "logo.jpg", not "logo" — so only the sidecar sweep gets rid of it, and a
        // later ".png" pick would otherwise carry it forever.
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg.wsgmnew"), "orphan-sidecar");
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };

        using var staged = SplashAssets.Prepare(splash, TargetDir);
        staged.Commit();

        Assert.Equal(new[] { "logo.png" }, FileNames());
        Assert.Equal("new-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void PrepareSweepsAnOrphanedSidecarEvenWhenTheSaveIsRolledBack()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg.wsgmnew"), "orphan-sidecar");
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };

        var staged = SplashAssets.Prepare(splash, TargetDir);
        staged.Rollback();
        staged.Dispose();

        Assert.DoesNotContain(
            FileNames(),
            name => name.EndsWith(".wsgmnew", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(new[] { "logo.jpg" }, FileNames());
        Assert.Equal("live-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.jpg")));
    }

    [Fact]
    public void CommitAfterRollbackIsANoOpAndRollbackAfterCommitKeepsTheCommittedFiles()
    {
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };
        var rolledBack = SplashAssets.Prepare(splash, TargetDir);
        rolledBack.Rollback();
        rolledBack.Commit();
        Assert.Empty(System.IO.Directory.GetFiles(TargetDir));

        var again = new SplashConfig { LogoImagePath = WriteSource("picked2.png", "newer-logo") };
        var committed = SplashAssets.Prepare(again, TargetDir);
        committed.Commit();
        committed.Rollback();
        committed.Dispose();

        Assert.Equal("newer-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
    }
}
