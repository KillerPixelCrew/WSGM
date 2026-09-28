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
    public void ApplyCopiesAChangeLeavesTheSameBytesAloneAndRemovesTheOverrideForSteamsOwn()
    {
        Directory.CreateDirectory(_root);
        var movie = Path.Combine(_root, "boot.webm");
        File.WriteAllBytes(movie, [1, 2, 3]);
        var overrides = Path.Combine(_root, "movies");
        var target = Path.Combine(overrides, "bigpicture_startup.webm");

        Assert.Equal(new AnimationApplyReport(true, null), AnimationOverrides.Apply(overrides, movie));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Equal(new AnimationApplyReport(false, null), AnimationOverrides.Apply(overrides, movie));
        Assert.Equal(new AnimationApplyReport(true, null), AnimationOverrides.Apply(overrides, null));
        Assert.False(File.Exists(target));
        Assert.Equal(new AnimationApplyReport(false, null), AnimationOverrides.Apply(overrides, null));
    }

    [Fact]
    public void AMissingMovieIsReportedAndItsOverrideRemovedSoSteamsOwnPlays()
    {
        var overrides = Path.Combine(_root, "movies");
        Directory.CreateDirectory(overrides);
        File.WriteAllBytes(Path.Combine(overrides, "bigpicture_startup.webm"), [1]);

        var report = AnimationOverrides.Apply(overrides, Path.Combine(_root, "gone.webm"));

        Assert.True(report.Changed);
        Assert.Contains("missing", report.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(overrides, "bigpicture_startup.webm")));
    }

    [Fact]
    public void ShuffleSkipsExclusionsAndAnswersEmptyWithNothingLeft()
    {
        List<AnimationEntry> entries =
        [
            new("b1", "Boot one", "", "b1.webm", null),
            new("b2", "Boot two", "", "b2.webm", null)
        ];

        Assert.Equal("b1", AnimationShuffle.Pick(entries, ["b2"], new Random(7)));
        Assert.Equal("", AnimationShuffle.Pick(entries, ["b1", "b2"], new Random(7)));
        Assert.Equal("", AnimationShuffle.Pick([], [], new Random(1)));
    }

    [Fact]
    public void TheConfigurationIsNormalizedAndCloned()
    {
        AnimationsConfig config = new()
        {
            Boot = " b1 ", ShuffleExclusions = ["a", " a ", "", "b"], ShuffleOnStart = true
        };

        ConfigStore.NormalizeAnimations(config);
        var clone = config.Clone();
        clone.Boot = "t";
        clone.ShuffleExclusions.Add("c");

        Assert.Equal("b1", config.Boot);
        Assert.Equal(["a", "b"], config.ShuffleExclusions);
        Assert.True(clone.ShuffleOnStart);
    }
}
