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

        Assert.True(AnimationOverrides.Apply(overrides, movie).Changed);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Equal(new AnimationApplyReport(false, null), AnimationOverrides.Apply(overrides, movie));
        File.WriteAllBytes(movie, [4, 5, 6]);
        File.SetLastWriteTimeUtc(movie, File.GetLastWriteTimeUtc(target).AddMinutes(1));
        Assert.True(AnimationOverrides.Apply(overrides, movie).Changed);
        Assert.Equal([4, 5, 6], File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(new AnimationApplyReport(true, null), AnimationOverrides.Apply(overrides, null));
        Assert.False(File.Exists(target));
        Assert.Equal(new AnimationApplyReport(false, null), AnimationOverrides.Apply(overrides, null));
    }

    [Fact]
    public void TheVolumeIsTheOpusGainOfTheCopyAndTakesBackSteamsDoubledPlayback()
    {
        // OpusHead as WebM carries it: magic, version, channels, pre-skip, rate, then the Q7.8 gain.
        Directory.CreateDirectory(_root);
        byte[] header = [0x63, 0xA2, 0x93, .. "OpusHead"u8, 1, 2, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0x00, 0x01, 0];
        var movie = Path.Combine(_root, "opus.webm");
        File.WriteAllBytes(movie, header);
        var overrides = Path.Combine(_root, "movies");
        var target = Path.Combine(overrides, "bigpicture_startup.webm");
        var gainAt = 3 + 16;

        var full = AnimationOverrides.Apply(overrides, movie);
        Assert.Equal(new AnimationApplyReport(true, null), full);
        // The author's +1 dB, less the 6.02 dB Steam adds by playing the movie twice.
        Assert.Equal(256 - 1541, BitConverter.ToInt16(File.ReadAllBytes(target), gainAt));
        Assert.Equal(new AnimationApplyReport(false, null), AnimationOverrides.Apply(overrides, movie));

        Assert.True(AnimationOverrides.Apply(overrides, movie, 50).Changed);
        Assert.Equal(256 - 3083, BitConverter.ToInt16(File.ReadAllBytes(target), gainAt));
        Assert.True(AnimationOverrides.Apply(overrides, movie, 0).Changed);
        Assert.Equal(short.MinValue, BitConverter.ToInt16(File.ReadAllBytes(target), gainAt));
        Assert.Equal(header, File.ReadAllBytes(movie));
    }

    [Fact]
    public void AMovieWhoseSoundIsNotOpusSaysItsVolumeCannotBeSet()
    {
        Directory.CreateDirectory(_root);
        var movie = Path.Combine(_root, "vorbis.webm");
        File.WriteAllBytes(movie, [.. "A_VORBIS"u8]);

        var report = AnimationOverrides.Apply(Path.Combine(_root, "movies"), movie);

        Assert.True(report.Changed);
        Assert.Contains("not Opus", report.Note, StringComparison.Ordinal);
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
    public void InterruptedOwnershipPromotionPreservesTheOriginalMovie()
    {
        var overrides = Path.Combine(_root, "movies");
        Directory.CreateDirectory(overrides);
        var target = Path.Combine(overrides, AnimationOverrides.BootFileName);
        var source = Path.Combine(_root, "boot.webm");
        File.WriteAllBytes(source, [1, 2, 3]);
        File.WriteAllBytes(target, [7, 7]);
        AnimationOverrides.Apply(overrides, source);
        var marker = target + AnimationOverrides.MarkerSuffix;
        File.Move(marker, marker + ".pending");

        Assert.True(AnimationOverrides.Apply(overrides, null).Changed);
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(target));
        Assert.False(File.Exists(marker + ".pending"));
    }

    [Fact]
    public void AnUnownedReplacementCannotOverwriteAnExistingOriginalBackup()
    {
        var overrides = Path.Combine(_root, "movies");
        Directory.CreateDirectory(overrides);
        var target = Path.Combine(overrides, AnimationOverrides.BootFileName);
        File.WriteAllBytes(target, [9]);
        File.WriteAllBytes(target + AnimationOverrides.OriginalSuffix, [7]);
        var source = Path.Combine(_root, "boot.webm");
        File.WriteAllBytes(source, [1, 2, 3]);
        var result = AnimationOverrides.Apply(overrides, source);
        Assert.False(result.Changed);
        Assert.NotNull(result.Error);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(target));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(target + AnimationOverrides.OriginalSuffix));
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
