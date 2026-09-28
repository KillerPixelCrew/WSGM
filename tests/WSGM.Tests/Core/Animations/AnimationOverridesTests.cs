using WSGM.Core;

namespace WSGM.Tests.Core.Animations;

/// <summary>The override files: written under the names the Windows client asks for, and only when they differ.</summary>
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
    public void TheSlotsNameTheFilesTheWindowsClientAsksFor()
    {
        Assert.Equal("bigpicture_startup.webm", AnimationSlots.FileName(AnimationSlots.Boot));
        Assert.Equal("steam_os_suspend.webm", AnimationSlots.FileName(AnimationSlots.Suspend));
        Assert.Equal("steam_os_suspend_from_throbber.webm", AnimationSlots.FileName(AnimationSlots.Throbber));
        Assert.Equal(Path.Combine(@"C:\Steam", "config", "uioverrides", "movies"),
            AnimationOverrides.Directory(@"C:\Steam"));
        Assert.True(AnimationTargets.Fits(AnimationTargets.Suspend, AnimationSlots.Throbber));
        Assert.False(AnimationTargets.Fits(AnimationTargets.Boot, AnimationSlots.Suspend));
        Assert.True(AnimationTargets.Fits(AnimationTargets.Any, AnimationSlots.Boot));
    }

    [Fact]
    public void ApplyCopiesWhatChangedRemovesWhatIsStockAndLeavesTheSameBytesAlone()
    {
        Directory.CreateDirectory(_root);
        var boot = Path.Combine(_root, "boot.webm");
        var suspend = Path.Combine(_root, "suspend.webm");
        File.WriteAllBytes(boot, [1, 2, 3]);
        File.WriteAllBytes(suspend, [4, 5]);
        var overrides = Path.Combine(_root, "movies");

        var first = AnimationOverrides.Apply(overrides, new Dictionary<string, string?>
        {
            [AnimationSlots.Boot] = boot, [AnimationSlots.Suspend] = suspend, [AnimationSlots.Throbber] = null
        });
        Assert.Equal([AnimationSlots.Boot, AnimationSlots.Suspend], first.Changed);
        Assert.Empty(first.Errors);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(overrides, "bigpicture_startup.webm")));
        Assert.Equal([4, 5], File.ReadAllBytes(Path.Combine(overrides, "steam_os_suspend.webm")));
        Assert.False(File.Exists(Path.Combine(overrides, "steam_os_suspend_from_throbber.webm")));

        var again = AnimationOverrides.Apply(overrides, new Dictionary<string, string?>
        {
            [AnimationSlots.Boot] = boot, [AnimationSlots.Suspend] = suspend
        });
        Assert.Empty(again.Changed);

        var stock = AnimationOverrides.Apply(overrides, new Dictionary<string, string?>
        {
            [AnimationSlots.Boot] = null, [AnimationSlots.Suspend] = suspend, [AnimationSlots.Throbber] = suspend
        });
        Assert.Equal([AnimationSlots.Boot, AnimationSlots.Throbber], stock.Changed);
        Assert.False(File.Exists(Path.Combine(overrides, "bigpicture_startup.webm")));
        Assert.Equal([4, 5], File.ReadAllBytes(Path.Combine(overrides, "steam_os_suspend_from_throbber.webm")));
    }

    [Fact]
    public void AMissingMovieIsReportedAndItsOverrideRemovedSoSteamsOwnPlays()
    {
        var overrides = Path.Combine(_root, "movies");
        Directory.CreateDirectory(overrides);
        File.WriteAllBytes(Path.Combine(overrides, "bigpicture_startup.webm"), [1]);

        var report = AnimationOverrides.Apply(overrides, new Dictionary<string, string?>
        {
            [AnimationSlots.Boot] = Path.Combine(_root, "gone.webm")
        });

        Assert.Equal([AnimationSlots.Boot], report.Changed);
        Assert.Contains("Boot", Assert.Single(report.Errors), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(overrides, "bigpicture_startup.webm")));
    }

    [Fact]
    public void ShufflePicksOnlyWhatFitsEachSlotAndSkipsExclusions()
    {
        List<AnimationEntry> entries =
        [
            new("b1", "Boot one", "", AnimationTargets.Boot, "b1.webm", null),
            new("b2", "Boot two", "", AnimationTargets.Boot, "b2.webm", null),
            new("s1", "Suspend one", "", AnimationTargets.Suspend, "s1.webm", null),
            new("custom:x.webm", "x", "", AnimationTargets.Any, "x.webm", null)
        ];

        var picked = AnimationShuffle.Pick(entries, ["b2", "custom:x.webm"], new Random(7));

        Assert.Equal("b1", picked[AnimationSlots.Boot]);
        Assert.Equal("s1", picked[AnimationSlots.Suspend]);
        Assert.Equal("s1", picked[AnimationSlots.Throbber]);
        Assert.Equal("", AnimationShuffle.Pick([], [], new Random(1))[AnimationSlots.Boot]);
    }

    [Fact]
    public void TheConfigurationIsNormalizedAndCloned()
    {
        AnimationsConfig config = new()
        {
            Boot = " b1 ", Suspend = null!, ShuffleExclusions = ["a", " a ", "", "b"], ShuffleOnStart = true
        };

        ConfigStore.NormalizeAnimations(config);
        var clone = config.Clone();
        clone.Set(AnimationSlots.Throbber, "t");
        clone.ShuffleExclusions.Add("c");

        Assert.Equal(("b1", "", ""), (config.Boot, config.Suspend, config.Throbber));
        Assert.Equal(["a", "b"], config.ShuffleExclusions);
        Assert.Equal("t", clone.Get(AnimationSlots.Throbber));
        Assert.Equal("", config.Get(AnimationSlots.Throbber));
        Assert.True(clone.ShuffleOnStart);
    }
}
