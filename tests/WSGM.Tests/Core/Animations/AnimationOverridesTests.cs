using WSGM.Core;

namespace WSGM.Tests.Core.Animations;

/// <summary>The boot override: written under the name the Windows client asks for, and only when it differs.</summary>
public sealed class AnimationOverridesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "WSGM.Tests.animovr." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void TheOverrideIsTheFileTheWindowsClientAsksFor()
    {
        Assert.Equal("bigpicture_startup.webm", AnimationOverrides.BootFileName);
        Assert.Equal(Path.Combine(@"C:\Steam", "config", "uioverrides", "movies"),
            AnimationOverrides.Directory(@"C:\Steam"));
    }

    [Fact]
    public void ApplyCopiesAChangeLeavesItsLastCopyAloneAndRemovesTheOverrideForSteamsOwn()
    {
        Directory.CreateDirectory(_root);
        var movie = Path.Combine(_root, "boot.webm");
        File.WriteAllBytes(movie, [1, 2, 3]);
        var overrides = Path.Combine(_root, "movies");
        var target = Path.Combine(overrides, "bigpicture_startup.webm");

        Assert.Equal(new AnimationApplyReport(true, null), AnimationOverrides.Apply(overrides, movie));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Equal(new AnimationApplyReport(false, null), AnimationOverrides.Apply(overrides, movie));
        File.WriteAllBytes(movie, [4, 5, 6]);
        File.SetLastWriteTimeUtc(movie, File.GetLastWriteTimeUtc(target).AddMinutes(1));
        Assert.Equal(new AnimationApplyReport(true, null), AnimationOverrides.Apply(overrides, movie));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(new AnimationApplyReport(true, null), AnimationOverrides.Apply(overrides, null));
        Assert.False(File.Exists(target));
        Assert.Equal(new AnimationApplyReport(false, null), AnimationOverrides.Apply(overrides, null));
    }

    [Fact]
    public void AMissingMovieIsReportedAndItsOverrideRemovedSoSteamsOwnPlays()
    {
        var overrides = Path.Combine(_root, "movies");
        Directory.CreateDirectory(_root);
        var movie = Path.Combine(_root, "gone.webm");
        File.WriteAllBytes(movie, [1]);
        AnimationOverrides.Apply(overrides, movie);
        File.Delete(movie);

        var report = AnimationOverrides.Apply(overrides, movie);

        Assert.True(report.Changed);
        Assert.Contains("missing", report.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(overrides, "bigpicture_startup.webm")));
    }

    [Fact]
    public void ShufflePicksFromTheLibraryAndAnswersEmptyForAnEmptyOne()
    {
        List<AnimationEntry> entries =
        [
            new("b1", "Boot one", "b1.webm", null),
            new("b2", "Boot two", "b2.webm", null)
        ];

        Assert.Contains(AnimationShuffle.Pick(entries, new Random(7)), new[] { "b1", "b2" });
        Assert.Equal("", AnimationShuffle.Pick([], new Random(1)));
    }

    [Fact]
    public void TheConfigurationIsNormalizedAndCloned()
    {
        AnimationsConfig config = new() { Boot = " b1 ", ShuffleOnStart = true };

        ConfigStore.NormalizeAnimations(config);
        var clone = config.Clone();
        clone.Boot = "t";

        Assert.Equal("b1", config.Boot);
        Assert.True(clone.ShuffleOnStart);
    }

    [Fact]
    public void AnOverrideWsgmDidNotWriteIsNeverRemovedAndComesBackAfterWsgmsOwn()
    {
        var overrides = Path.Combine(_root, "movies");
        Directory.CreateDirectory(overrides);
        var target = Path.Combine(overrides, AnimationOverrides.BootFileName);
        File.WriteAllBytes(target, [7, 7]);
        var movie = Path.Combine(_root, "boot.webm");
        File.WriteAllBytes(movie, [1, 2, 3]);

        Assert.Equal(new AnimationApplyReport(false, null), AnimationOverrides.Apply(overrides, null));
        Assert.Equal([7, 7], File.ReadAllBytes(target));

        Assert.True(AnimationOverrides.Apply(overrides, movie).Changed);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Equal([7, 7], File.ReadAllBytes(target + AnimationOverrides.OriginalSuffix));

        Assert.True(AnimationOverrides.Apply(overrides, null).Changed);
        Assert.Equal([7, 7], File.ReadAllBytes(target));
        Assert.False(File.Exists(target + AnimationOverrides.OriginalSuffix));
        Assert.False(File.Exists(target + AnimationOverrides.MarkerSuffix));
    }

    [Fact]
    public void AFileSomeoneReplacedWsgmsCopyWithIsTheirs()
    {
        var overrides = Path.Combine(_root, "movies");
        Directory.CreateDirectory(_root);
        var target = Path.Combine(overrides, AnimationOverrides.BootFileName);
        var movie = Path.Combine(_root, "boot.webm");
        File.WriteAllBytes(movie, [1, 2, 3]);
        AnimationOverrides.Apply(overrides, movie);

        File.WriteAllBytes(target, [9, 9, 9, 9]);

        Assert.False(AnimationOverrides.Apply(overrides, null).Changed);
        Assert.Equal([9, 9, 9, 9], File.ReadAllBytes(target));
    }
}
